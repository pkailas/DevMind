// File: ContextOverflowRecoveryTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// A server rejection for size used to end the turn: it reached onError, the headless loop
// broke, and the job died with an HTTP error in its result — while holding the exact token
// count that said what to drop.
//
// These drive a real LlmClient over a real HttpClient against a socket server that answers
// the first POST with the rejection llama.cpp actually sends, including its Connection:
// close. That last detail is the point of using a real socket rather than a stubbed handler:
// the retry has to succeed over a connection the server has just closed, and no assertion
// about connection pooling is worth anything unless a real pool is doing it.
//
// The shape being pinned is narrow on purpose. One mechanical attempt, only when the
// compaction actually shrank the prompt, then an honest failure carrying the server's own
// numbers. A retry that re-sends an identical request is not recovery, it is a loop.

using Newtonsoft.Json.Linq;
using Xunit;

namespace DevMind.Core.Tests
{
    public sealed class ContextOverflowRecoveryTests
    {
        private const int PromptTokens = 450_011;
        private const int ContextSize  = 262_144;

        /// <summary>
        /// A client wired to the fake, with detection pinned to llama so Configure() does not
        /// probe. Mirrors the setup the other LlmClient socket tests use.
        /// </summary>
        private static LlmClient NewClient(FakeSseServer server)
        {
            var client = new LlmClient(new FakeLlmOptions());
            string? prior = Environment.GetEnvironmentVariable("DEVMIND_SERVER_TYPE");
            Environment.SetEnvironmentVariable("DEVMIND_SERVER_TYPE", "llama");
            try { client.Configure(server.BaseUrl, apiKey: null!); }
            finally { Environment.SetEnvironmentVariable("DEVMIND_SERVER_TYPE", prior); }
            return client;
        }

        /// <summary>
        /// Several large tool results, so a forced compaction has something real to reclaim.
        /// Distinct content per message: identical blobs would let a de-duplicating pass
        /// reclaim more than the test intends to measure.
        /// </summary>
        private static void SeedCompactableHistory(LlmClient client, int count = 6)
        {
            for (int i = 0; i < count; i++)
                client.AddToolResultMessage($"call_{i}", $"tool result {i} " + new string((char)('a' + i), 20_000));
        }

        private sealed class Capture
        {
            public readonly List<string> Tokens = new();
            public Exception? Error;
            public bool Completed;
            public string Text => string.Concat(Tokens);
        }

        private static async Task<Capture> SendAsync(LlmClient client, string userMessage)
        {
            var cap = new Capture();
            var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            await client.SendMessageAsync(
                userMessage,
                onToken: t => { lock (cap.Tokens) cap.Tokens.Add(t); },
                onComplete: () => { cap.Completed = true; done.TrySetResult(true); },
                onError: ex => { cap.Error = ex; done.TrySetResult(true); },
                combinedSystemPrompt: "You are a test assistant.");

            Assert.True(await Task.WhenAny(done.Task, Task.Delay(30_000)) == done.Task,
                "send timed out — the recovery loop is not terminating");
            return cap;
        }

        private static int CountUserMessages(string requestBody, string content)
        {
            var messages = (JArray)JObject.Parse(requestBody)["messages"]!;
            int n = 0;
            foreach (var m in messages)
                if ((string?)m["role"] == "user" && (string?)m["content"] == content) n++;
            return n;
        }

        // ── The positive case ──────────────────────────────────────────────────

        [Fact]
        public async Task ARejectedRequest_IsCompactedAndRetriedOnce_AndSucceeds()
        {
            using var server = new FakeSseServer { MaxChatPosts = 6 };
            server.StatusQueue.Add((400, FakeSseServer.BuildOverflowBody(PromptTokens, ContextSize)));

            var client = NewClient(server);
            SeedCompactableHistory(client);

            var cap = await SendAsync(client, "the user turn");

            Assert.Null(cap.Error);
            Assert.True(cap.Completed, "the turn did not complete — recovery did not happen");

            // Exactly one rejection and one retry.
            Assert.Equal(2, server.RequestBodies.Count);

            // The retry must be SMALLER. A retry that re-sends the same bytes is a loop
            // against a server that has already answered.
            Assert.True(server.RequestBodies[1].Length < server.RequestBodies[0].Length,
                $"the retried request ({server.RequestBodies[1].Length} bytes) did not shrink " +
                $"against the rejected one ({server.RequestBodies[0].Length} bytes)");

            // The user message is appended before the send and nothing removes it on failure,
            // so a retry that re-appends would put two consecutive user turns on the wire.
            Assert.Equal(1, CountUserMessages(server.RequestBodies[1], "the user turn"));

            // The transcript carries the server's own numbers, which is the whole reason this
            // path is worth having: they are measured, not estimated.
            Assert.Contains(PromptTokens.ToString("N0"), cap.Text, StringComparison.Ordinal);
            Assert.Contains(ContextSize.ToString("N0"), cap.Text, StringComparison.Ordinal);
            Assert.Contains("retrying", cap.Text, StringComparison.OrdinalIgnoreCase);
        }

        // ── Bookkeeping ────────────────────────────────────────────────────────

        // A forced compaction that skipped the counters would be invisible to the thrash
        // detector, so a session compacting on every turn would never be recognised as one.
        [Fact]
        public async Task TheForcedCompaction_IsCountedLikeAnyOther()
        {
            using var server = new FakeSseServer { MaxChatPosts = 6 };
            server.StatusQueue.Add((400, FakeSseServer.BuildOverflowBody(PromptTokens, ContextSize)));

            var client = NewClient(server);
            SeedCompactableHistory(client);
            client.IncrementTurn();

            int before = client.RecentCompactionCountForTest;

            var cap = await SendAsync(client, "the user turn");
            Assert.Null(cap.Error);

            Assert.True(client.RecentCompactionCountForTest > before,
                "the forced compaction did not increment the compaction counter — the thrash detector cannot see it");
            Assert.Equal(client.CurrentTurn, client.LastCompactionTurnForTest);
        }

        // ── Exhaustion ─────────────────────────────────────────────────────────

        [Fact]
        public async Task TwoRejections_FailWithTheServersNumbers_AfterExactlyTwoPosts()
        {
            using var server = new FakeSseServer { MaxChatPosts = 4 };
            server.StatusQueue.Add((400, FakeSseServer.BuildOverflowBody(PromptTokens, ContextSize)));
            server.StatusQueue.Add((400, FakeSseServer.BuildOverflowBody(PromptTokens, ContextSize)));

            var client = NewClient(server);
            SeedCompactableHistory(client);

            var cap = await SendAsync(client, "the user turn");

            Assert.NotNull(cap.Error);

            // By count, not by timeout: an unbounded loop must fail here as a number, so the
            // failure names the bug instead of hanging the suite.
            Assert.Equal(2, server.RequestBodies.Count);

            Assert.IsType<InvalidOperationException>(cap.Error);
            Assert.Contains(PromptTokens.ToString("N0"), cap.Error!.Message, StringComparison.Ordinal);
            Assert.Contains(ContextSize.ToString("N0"), cap.Error.Message, StringComparison.Ordinal);
            Assert.Contains("compaction", cap.Error.Message, StringComparison.OrdinalIgnoreCase);

            // WHICH guard stopped it matters. Here the first compaction made real progress
            // and the server rejected the smaller request anyway, so the ATTEMPT BOUND is
            // what must end it. Without that assertion this test passes either way: drop the
            // bound and the second compaction reclaims nothing, so the no-progress guard
            // catches it at the same POST count and the same two numbers. The bound would
            // then be untested — the count cannot distinguish two guards that stop in the
            // same place.
            Assert.Contains("rejected again", cap.Error.Message, StringComparison.Ordinal);

            // It must not read as a network fault — that sends the operator to the wrong problem.
            Assert.DoesNotContain("LLM request failed", cap.Error.Message, StringComparison.Ordinal);

            Assert.Equal(1, CountUserMessages(server.RequestBodies[1], "the user turn"));
        }

        // ── The no-progress guard ──────────────────────────────────────────────

        // Requirement C. With nothing compactable, the forced pass reclaims nothing, and
        // re-sending byte-identical content to a server that has already refused it is not a
        // retry. This fails if the loop ever retries blindly.
        [Fact]
        public async Task WhenCompactionReclaimsNothing_TheRequestIsNotReSent()
        {
            using var server = new FakeSseServer { MaxChatPosts = 4 };
            server.StatusQueue.Add((400, FakeSseServer.BuildOverflowBody(PromptTokens, ContextSize)));

            var client = NewClient(server);
            // Deliberately no tool results: a system message and one short user turn, both of
            // which every compaction pass pins.

            var cap = await SendAsync(client, "tiny");

            Assert.NotNull(cap.Error);
            Assert.Single(server.RequestBodies);
            Assert.Contains("reclaim nothing", cap.Error!.Message, StringComparison.OrdinalIgnoreCase);
        }

        // ── Everything else is untouched ───────────────────────────────────────

        // Requirement E. The exception text is compared character for character against what
        // the pre-existing error path produces, because the recovery helper now reads the
        // error body first and the caller reads it again to build this message.
        [Fact]
        public async Task ANonOverflowFailure_BehavesExactlyAsBefore()
        {
            using var server = new FakeSseServer { MaxChatPosts = 4 };
            server.StatusQueue.Add((404, "{\"error\":{\"message\":\"unknown model\"}}"));

            var client = NewClient(server);
            SeedCompactableHistory(client);

            var cap = await SendAsync(client, "the user turn");

            Assert.NotNull(cap.Error);
            Assert.Single(server.RequestBodies);   // no retry, no compaction

            Assert.IsType<HttpRequestException>(cap.Error);
            Assert.Equal(
                "LLM request failed: 404 Not Found — POST " + server.BaseUrl + "/chat/completions " +
                "— server said: {\"error\":{\"message\":\"unknown model\"}}",
                cap.Error!.Message);

            // No recovery chatter on a failure that was never recoverable.
            Assert.DoesNotContain("[CONTEXT] Server rejected", cap.Text, StringComparison.Ordinal);
        }

        // A 500 carrying the overflow wording is the server failing, not refusing. Status
        // wins, so this is not retried.
        [Fact]
        public async Task AnOverflowBodyOnAFiveHundred_IsNotRecovered()
        {
            using var server = new FakeSseServer { MaxChatPosts = 4 };
            server.StatusQueue.Add((500, FakeSseServer.BuildOverflowBody(PromptTokens, ContextSize)));

            var client = NewClient(server);
            SeedCompactableHistory(client);

            var cap = await SendAsync(client, "the user turn");

            Assert.NotNull(cap.Error);
            Assert.Single(server.RequestBodies);
            Assert.IsType<HttpRequestException>(cap.Error);
            Assert.StartsWith("LLM request failed: 500", cap.Error!.Message, StringComparison.Ordinal);
        }

        // An overflow reported with no counts still recovers: the watermark alone sizes the
        // compaction, and the transcript simply has nothing measured to quote.
        [Fact]
        public async Task AnOverflowWithNoNumbers_StillRecovers()
        {
            using var server = new FakeSseServer { MaxChatPosts = 6 };
            server.StatusQueue.Add((413, string.Empty));

            var client = NewClient(server);
            SeedCompactableHistory(client);

            var cap = await SendAsync(client, "the user turn");

            Assert.Null(cap.Error);
            Assert.True(cap.Completed);
            Assert.Equal(2, server.RequestBodies.Count);
            Assert.True(server.RequestBodies[1].Length < server.RequestBodies[0].Length);
        }
    }
}
