// File: JobIdHeaderTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Strata logs X-DevMind-Job-Id on every thinking record so the records can be tied
// back to the headless job that produced them. LlmClient.JobId is the single knob:
// set, every chat/completions request carries the header; unset (the TUI, the CLI,
// the document digester), it carries none — byte-for-byte the old request.
//
// The paths that must stamp it are all of them, not just the obvious one:
//   * the main streaming request (PostWithOverflowRecoveryAsync),
//   * its overflow-recovery retry — a request rebuilt mid-send,
//   * the compaction summariser (GenerateSummaryAsync), including its own single retry.
// The summariser path is reached here by driving a real forced MicroCompact against
// a compactable history, the same way the overflow-recovery tests do.
//
// The header is asserted on the wire — read from the request headers the fake server
// parses — because a property on the client proves nothing about what the backend sees.

using Xunit;

namespace DevMind.Core.Tests
{
    public sealed class JobIdHeaderTests
    {
        private const string JobId = "job-2297";
        private const string Header = "X-DevMind-Job-Id";

        private const int PromptTokens = 450_011;
        private const int ContextSize  = 262_144;

        /// <summary>
        /// A client wired to the fake, detection pinned to llama so Configure() does not probe.
        /// Mirrors the setup the other LlmClient socket tests use.
        /// </summary>
        private static LlmClient NewClient(FakeSseServer server, string? jobId, ILlmOptions? options = null)
        {
            var client = new LlmClient(options ?? new FakeLlmOptions());
            client.JobId = jobId;
            string? prior = Environment.GetEnvironmentVariable("DEVMIND_SERVER_TYPE");
            Environment.SetEnvironmentVariable("DEVMIND_SERVER_TYPE", "llama");
            try { client.Configure(server.BaseUrl, apiKey: null!); }
            finally { Environment.SetEnvironmentVariable("DEVMIND_SERVER_TYPE", prior); }
            return client;
        }

        /// <summary>
        /// Distinct tool results, long enough for a compaction pass to trim. Identical blobs
        /// would let a de-duplicating pass reclaim more than the test intends.
        /// </summary>
        private static void SeedCompactableHistory(LlmClient client, int count = 8)
        {
            for (int i = 0; i < count; i++)
                client.AddToolResultMessage($"call_{i}", $"tool result {i} " + new string((char)('a' + i), 20_000));
        }

        private static async Task SendAsync(LlmClient client, string userMessage = "the user turn")
        {
            var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            await client.SendMessageAsync(
                userMessage,
                onToken: _ => { },
                onComplete: () => done.TrySetResult(true),
                onError: ex => done.TrySetException(ex),
                combinedSystemPrompt: "You are a test assistant.");

            Assert.True(await Task.WhenAny(done.Task, Task.Delay(30_000)) == done.Task,
                "SendMessageAsync did not complete within 30s");
            await done.Task;
        }

        // ── The main streaming request ────────────────────────────────────────

        [Fact]
        public async Task WithJobId_TheChatRequest_CarriesTheHeader()
        {
            using var server = new FakeSseServer();
            var client = NewClient(server, JobId);

            await SendAsync(client);

            Assert.Single(server.RequestBodies);
            Assert.Equal(JobId, server.HeaderValue(0, Header));
        }

        [Fact]
        public async Task WithoutJobId_NoChatRequest_CarriesTheHeader()
        {
            using var server = new FakeSseServer();
            var client = NewClient(server, jobId: null);

            await SendAsync(client);
            await SendAsync(client, "the second turn");

            Assert.Equal(2, server.RequestBodies.Count);
            Assert.Null(server.HeaderValue(0, Header));
            Assert.Null(server.HeaderValue(1, Header));
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        public async Task WithABlankJobId_NoHeaderIsSent(string blankJobId)
        {
            using var server = new FakeSseServer();
            var client = NewClient(server, blankJobId);

            await SendAsync(client);

            Assert.Single(server.RequestBodies);
            Assert.Null(server.HeaderValue(0, Header));
        }

        // ── The overflow-recovery retry: the request rebuilt mid-send ─────────

        [Fact]
        public async Task TheOverflowRecoveryRetry_CarriesTheHeaderToo()
        {
            using var server = new FakeSseServer { MaxChatPosts = 6 };
            server.StatusQueue.Add((400, FakeSseServer.BuildOverflowBody(PromptTokens, ContextSize)));

            var client = NewClient(server, JobId);
            SeedCompactableHistory(client);

            await SendAsync(client);

            // One rejection, one retry — and the header on BOTH, the retried request being
            // built from scratch inside the recovery loop.
            Assert.Equal(2, server.RequestBodies.Count);
            Assert.Equal(JobId, server.HeaderValue(0, Header));
            Assert.Equal(JobId, server.HeaderValue(1, Header));
        }

        // ── The compaction summariser: its own request, its own retry ────────

        /// <summary>The summariser builds a fresh messages array with its own system prompt,
        /// which is what identifies its POST among the recorded chat POSTs.</summary>
        private const string SummariserMarker = "You are a context summarizer";

        [Fact]
        public async Task TheCompactionSummariserRequest_CarriesTheHeader()
        {
            using var server = new FakeSseServer();
            var client = NewClient(server, JobId, new SummarizingOptions());
            SeedCompactableHistory(client);

            // Force a compaction: the summariser POST is the request under test, and the
            // summary itself is irrelevant here (the fake server does not answer it with
            // JSON, so GenerateSummaryAsync returns null — the header is on the wire by then).
            string log = await client.MicroCompactToolResultsAsync(force: true, neededTokens: 0);

            Assert.NotNull(log); // a compaction ran, so the summariser ran
            int summaryIndex = server.RequestBodies.FindIndex(
                b => b.Contains(SummariserMarker, StringComparison.Ordinal));
            Assert.True(summaryIndex >= 0, "no summariser POST was recorded");
            Assert.Equal(JobId, server.HeaderValue(summaryIndex, Header));
        }

        [Fact]
        public async Task TheCompactionSummariserRequest_CarriesNoHeader_WhenJobIdIsNull()
        {
            using var server = new FakeSseServer();
            var client = NewClient(server, jobId: null, new SummarizingOptions());
            SeedCompactableHistory(client);

            string log = await client.MicroCompactToolResultsAsync(force: true, neededTokens: 0);

            Assert.NotNull(log);
            int summaryIndex = server.RequestBodies.FindIndex(
                b => b.Contains(SummariserMarker, StringComparison.Ordinal));
            Assert.True(summaryIndex >= 0, "no summariser POST was recorded");
            Assert.Null(server.HeaderValue(summaryIndex, Header));
        }

        [Fact]
        public async Task TheCompactionSummariserRetry_CarriesTheHeader()
        {
            using var server = new FakeSseServer { MaxChatPosts = 4 };
            // The summariser POST is the only chat POST a forced compaction sends, so this
            // 400 lands on its first attempt and drives its single retry.
            server.StatusQueue.Add((400, "{\"error\":{\"message\":\"no slot\"}}"));

            var client = NewClient(server, JobId, new SummarizingOptions());
            SeedCompactableHistory(client);

            string log = await client.MicroCompactToolResultsAsync(force: true, neededTokens: 0);

            Assert.NotNull(log);
            int[] summaryIndexes = server.RequestBodies
                .Select((b, i) => (b, i))
                .Where(x => x.b.Contains(SummariserMarker, StringComparison.Ordinal))
                .Select(x => x.i)
                .ToArray();
            Assert.True(summaryIndexes.Length >= 2,
                $"the summariser retried {summaryIndexes.Length - 1} time(s); expected the scripted 400 to drive one retry");
            foreach (int i in summaryIndexes)
                Assert.Equal(JobId, server.HeaderValue(i, Header));
        }

        /// <summary>Summarising compaction on — the only FakeLlmOptions setting these
        /// two tests need changed.</summary>
        private sealed class SummarizingOptions : ILlmOptions
        {
            public string SystemPrompt => "You are a test assistant.";
            public string ModelName => "test-model";
            public int RequestTimeoutMinutes => 1;
            public int FirstTokenTimeoutMinutes => 1;
            public bool ShowDebugOutput => false;
            public bool ShowContextBudget => false;
            public bool ShowLlmThinking => false;
            public ContextEvictionMode ContextEviction => ContextEvictionMode.Off;
            public int ManualContextSize => 32768;
            public LlmServerType ServerType => LlmServerType.LlamaServer;
            public string CustomContextEndpoint => null!;
            public int MicroCompactThreshold => 99;
            public int NearlineIngestThresholdChars => 8_000;
            public bool MicroCompactSummarize => true;   // the summariser path under test
            public bool MicroCompactBrainwash => false;
            public bool AlwaysConfirmPatch => false;
            public ApprovalMode ApprovalMode => ApprovalMode.Auto;
            public int AgenticLoopMaxDepth => 25;
            public int AgenticContextLimitPercent => 0;
        }
    }
}
