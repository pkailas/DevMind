// File: ScratchpadMessageTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// The scratchpad used to be appended into the combined system prompt, which lands in
// _conversationHistory[0]. UpdateSystemPrompt skips the write only when the prompt text is
// byte-identical, and the model is instructed to update its scratchpad at every major
// decision point — so the skip almost never held, history[0] was rewritten nearly every
// turn, and the whole stable prefix (tool catalogue, AGENTS.md, memory index, standing
// rules) was reprocessed. The one part designed to change was invalidating the part
// designed not to.
//
// These assert on the REQUEST BODIES the server actually received, not on an internal flag
// or a private field: what reaches the wire is what the backend's cache sees, and that is
// the claim being made.

using Newtonsoft.Json.Linq;
using Xunit;

namespace DevMind.Core.Tests
{
    public sealed class ScratchpadMessageTests
    {
        private const string Prompt = "You are a test assistant. Tool catalogue and standing rules go here.";

        private static LlmClient NewClient(FakeSseServer server)
        {
            var client = new LlmClient(new FakeLlmOptions());

            // Both probes skipped, so the only requests the server sees are the sends.
            string? prior = Environment.GetEnvironmentVariable("DEVMIND_SERVER_TYPE");
            Environment.SetEnvironmentVariable("DEVMIND_SERVER_TYPE", "llama");
            try { client.Configure(server.BaseUrl, apiKey: null!); }
            finally { Environment.SetEnvironmentVariable("DEVMIND_SERVER_TYPE", prior); }

            return client;
        }

        private static async Task SendAsync(LlmClient client, string userMessage, string scratchpad)
        {
            var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            client.IncrementTurn();
            await client.SendMessageAsync(
                userMessage,
                onToken: _ => { },
                onComplete: () => done.TrySetResult(true),
                onError: ex => done.TrySetException(ex),
                combinedSystemPrompt: Prompt,
                taskScratchpad: scratchpad);

            Assert.True(await Task.WhenAny(done.Task, Task.Delay(15000)) == done.Task,
                "SendMessageAsync did not complete within 15s");
            await done.Task;
        }

        private static JArray MessagesOf(FakeSseServer server, int requestIndex)
            => (JArray)JObject.Parse(server.RequestBodies[requestIndex])["messages"]!;

        private static string? ScratchpadTextIn(JArray messages)
        {
            foreach (JToken m in messages)
            {
                string? content = (string?)m["content"];
                if (content != null && content.Contains("--- CURRENT SCRATCHPAD ---", StringComparison.Ordinal))
                    return content;
            }
            return null;
        }

        /// <summary>A message’s content as a non-null string; absent content reads as empty.</summary>
        private static string ContentOf(JToken message) => (string?)message["content"] ?? "";

        /// <summary>A message’s role as a non-null string.</summary>
        private static string RoleOf(JToken message) => (string?)message["role"] ?? "";

        /// <summary>The message at <paramref name="fromEnd"/> places from the end (0 = last).</summary>
        private static JToken FromEnd(JArray messages, int fromEnd) => messages[messages.Count - 1 - fromEnd];

        private static int ScratchpadCount(JArray messages)
        {
            int n = 0;
            foreach (JToken m in messages)
            {
                string? content = (string?)m["content"];
                if (content != null && content.Contains("--- CURRENT SCRATCHPAD ---", StringComparison.Ordinal))
                    n++;
            }
            return n;
        }

        // ── The deciding test ───────────────────────────────────────────────────

        [Fact]
        public async Task TwoSendsDifferingOnlyInScratchpad_LeaveTheSystemMessageByteIdentical()
        {
            using var server = new FakeSseServer();
            var client = NewClient(server);

            await SendAsync(client, "first", "step 1: read the config");
            await SendAsync(client, "second", "step 1: done\nstep 2: patch the parser");

            Assert.Equal(2, server.RequestBodies.Count);

            JToken first = MessagesOf(server, 0)[0];
            JToken second = MessagesOf(server, 1)[0];

            Assert.Equal("system", RoleOf(first));
            Assert.Equal(ContentOf(first), ContentOf(second));

            // Not vacuous: the scratchpad really did change between the two sends, and the
            // stable prefix really is present in the message that stayed identical.
            Assert.Contains("Tool catalogue and standing rules", ContentOf(first), StringComparison.Ordinal);
            Assert.NotEqual(ScratchpadTextIn(MessagesOf(server, 0)), ScratchpadTextIn(MessagesOf(server, 1)));
        }

        // ── Moving it must not lose it ──────────────────────────────────────────

        [Fact]
        public async Task TheScratchpadContentStillReachesTheRequest()
        {
            using var server = new FakeSseServer();
            var client = NewClient(server);

            await SendAsync(client, "go", "remember: the parser is in Foo.cs");

            string? text = ScratchpadTextIn(MessagesOf(server, 0));
            Assert.NotNull(text);
            Assert.Contains("remember: the parser is in Foo.cs", text!, StringComparison.Ordinal);

            // ...and it is no longer inside the system prompt, which is the point.
            JToken system = MessagesOf(server, 0)[0];
            Assert.DoesNotContain("CURRENT SCRATCHPAD", ContentOf(system), StringComparison.Ordinal);
        }

        // ── Exactly one, replaced never accumulated ─────────────────────────────

        [Fact]
        public async Task AcrossManySends_ExactlyOneScratchpadMessageIsPresent_AndItIsTheCurrentOne()
        {
            using var server = new FakeSseServer();
            var client = NewClient(server);

            await SendAsync(client, "a", "note one");
            await SendAsync(client, "b", "note two");
            await SendAsync(client, "c", "note three");

            for (int i = 0; i < 3; i++)
                Assert.Equal(1, ScratchpadCount(MessagesOf(server, i)));

            string? last = ScratchpadTextIn(MessagesOf(server, 2));
            Assert.Contains("note three", last!, StringComparison.Ordinal);
            Assert.DoesNotContain("note one", last!, StringComparison.Ordinal);
            Assert.DoesNotContain("note two", last!, StringComparison.Ordinal);
        }

        [Fact]
        public async Task ClearingTheScratchpad_RemovesTheMessage_RatherThanLeavingAnEmptyOne()
        {
            using var server = new FakeSseServer();
            var client = NewClient(server);

            await SendAsync(client, "a", "something to remember");
            Assert.Equal(1, ScratchpadCount(MessagesOf(server, 0)));

            await SendAsync(client, "b", "");
            Assert.Equal(0, ScratchpadCount(MessagesOf(server, 1)));
        }

        [Fact]
        public async Task NoScratchpadAtAll_AddsNoMessage()
        {
            using var server = new FakeSseServer();
            var client = NewClient(server);

            await SendAsync(client, "a", "");

            JArray messages = MessagesOf(server, 0);
            Assert.Equal(0, ScratchpadCount(messages));
            Assert.Equal("system", RoleOf(messages[0]));
            Assert.Equal("user", RoleOf(FromEnd(messages, 0)));
        }

        // ── Position ────────────────────────────────────────────────────────────

        // Second-to-last: late enough that changing it invalidates only the tail, while the
        // real user message keeps final position — where the generation prompt follows it.
        [Fact]
        public async Task TheScratchpadSitsImmediatelyBeforeTheUserMessage()
        {
            using var server = new FakeSseServer();
            var client = NewClient(server);

            await SendAsync(client, "the actual question", "some state");

            JArray messages = MessagesOf(server, 0);

            JToken last = FromEnd(messages, 0);
            Assert.Equal("user", RoleOf(last));
            Assert.Equal("the actual question", ContentOf(last));

            JToken penultimate = FromEnd(messages, 1);
            Assert.Contains("--- CURRENT SCRATCHPAD ---", ContentOf(penultimate), StringComparison.Ordinal);
            Assert.Equal("user", RoleOf(penultimate));
        }

        // ── No skin may put it back in the prompt ───────────────────────────────

        // The tests above drive LlmClient with a system prompt they supply themselves, so
        // they cannot see a SKIN re-appending the scratchpad to the prompt it builds — which
        // is exactly the defect, and it lived in three separate skins. The marker now belongs
        // in one place: the factory that builds the dedicated message.
        [Fact]
        public void NoProductionSourceOutsideTheMessageFactoryMentionsTheScratchpadMarker()
        {
            string root = RepoRoot();
            string factory = Path.Combine(root, "DevMind.Core", "LlmClient.cs");

            var offenders = new List<string>();
            int scanned = 0;

            foreach (string file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
            {
                string rel = file.Substring(root.Length).Replace(Path.DirectorySeparatorChar, '/');
                bool skip = false;
                foreach (string bad in new[] { "/_archive/", "/bin/", "/obj/", "/dist/", "/publish/",
                                               "/tools/", "/.vs/", "/.devmind/", "/CodeReviewBenchmarkWithBugs/",
                                               ".Core.Tests/", ".Cli.Tests/", ".McpServer.Tests/", ".TUI.Tests/" })
                    if (rel.Contains(bad, StringComparison.OrdinalIgnoreCase)) { skip = true; break; }
                if (skip) continue;
                if (string.Equals(file, factory, StringComparison.OrdinalIgnoreCase)) continue;

                scanned++;
                if (File.ReadAllText(file).Contains("CURRENT SCRATCHPAD", StringComparison.Ordinal))
                    offenders.Add(rel);
            }

            Assert.True(scanned > 0, "Scanned zero source files — RepoRoot() resolved wrong; the guard is vacuous.");
            Assert.True(offenders.Count == 0,
                "The scratchpad marker is back outside the message factory — a skin is building it into the " +
                "system prompt again, which rewrites history[0] on every scratchpad edit and reprocesses the " +
                "whole stable prefix. Offenders: " + string.Join(", ", offenders));
        }

        private static string RepoRoot()
        {
            // DevMind.Core.Tests/bin/<cfg>/<tfm>/ → up 3 = DevMind.Core.Tests → up 1 = repo root.
            string dir = AppContext.BaseDirectory;
            for (int up = 0; up < 4; up++)
            {
                var d = new DirectoryInfo(dir);
                if (d.Parent == null) break;
                dir = d.Parent.FullName;
            }
            return dir;
        }

        // ── The prefix really is stable, not merely message[0] ──────────────────

        // history[0] being identical is the headline, but the cache prefix is every token up
        // to the first difference. This pins that everything BEFORE the scratchpad is also
        // unchanged across a scratchpad-only edit — otherwise the win would be theoretical.
        [Fact]
        public async Task EveryMessageBeforeTheScratchpadIsUnchangedAcrossAScratchpadOnlyEdit()
        {
            using var server = new FakeSseServer();
            var client = NewClient(server);

            await SendAsync(client, "same question", "before");
            await SendAsync(client, "same question", "after");

            JArray a = MessagesOf(server, 0);
            JArray b = MessagesOf(server, 1);

            // The second send carries the first exchange too, so compare the shared prefix
            // up to the point either one first mentions the scratchpad.
            int firstScratchpad = 0;
            while (firstScratchpad < a.Count
                   && !ContentOf(a[firstScratchpad]).Contains("CURRENT SCRATCHPAD", StringComparison.Ordinal))
                firstScratchpad++;

            Assert.True(firstScratchpad > 0, "no scratchpad message found — the guard would be vacuous");

            for (int i = 0; i < firstScratchpad; i++)
                Assert.Equal(a[i].ToString(Newtonsoft.Json.Formatting.None),
                             b[i].ToString(Newtonsoft.Json.Formatting.None));
        }
    }
}
