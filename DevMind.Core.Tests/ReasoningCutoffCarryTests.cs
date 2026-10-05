// File: ReasoningCutoffCarryTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// H-71: a response whose reasoning the backend's thinking budget cut off makes the NEXT
// request carry a one-shot harness note quoting the tail of that reasoning (escalated on the
// third consecutive cut-off). Reasoning stays out of history, and so does the note.
//
//   * detection and tail extraction (pure);
//   * the marker as a real Strata stream delivers it — split across reasoning_content deltas,
//     content following — captured 2026-10-05 against qwen3.8-flash at effort medium;
//   * LlmClient's one-shot note: second-to-last on the wire, once, never in history;
//   * the headless loop end to end with a scripted server.

using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace DevMind.Core.Tests
{
    public class ReasoningCutoffTests
    {
        private const string Marker = "I have thought about this long enough; time to give my answer.";

        // ── Detection ──

        [Fact]
        public void Marker_Present_IsDetected_CaseInsensitively()
        {
            Assert.True(ReasoningCutoff.TryFindMarker("Weighing it.\n" + Marker, ReasoningCutoff.DefaultMarkers, out int i));
            Assert.Equal("Weighing it.\n".Length, i);
            Assert.True(ReasoningCutoff.TryFindMarker("x I HAVE THOUGHT ABOUT THIS LONG ENOUGH", ReasoningCutoff.DefaultMarkers, out _));
        }

        [Fact]
        public void Marker_Absent_IsNotDetected()
        {
            Assert.False(ReasoningCutoff.TryFindMarker("I have thought about this. Done.", ReasoningCutoff.DefaultMarkers, out _));
            Assert.False(ReasoningCutoff.TryFindMarker("", ReasoningCutoff.DefaultMarkers, out _));
            Assert.False(ReasoningCutoff.TryFindMarker(null!, ReasoningCutoff.DefaultMarkers, out _));
        }

        [Fact]
        public void ThinkingOff_DetectsNothing_AndCountsNothing()
        {
            var carry = new ReasoningCutoffCarry(ReasoningCutoff.DefaultMarkers, carry: true);
            Assert.Null(carry.Observe("Some thought.\n" + Marker, thinkingOn: false, out bool cutOff));
            Assert.False(cutOff);
            Assert.Equal(0, carry.Cutoffs);
        }

        [Fact]
        public void Markers_ResolveFromConfig_NullIsDefault_EmptyIsOff()
        {
            Assert.Same(ReasoningCutoff.DefaultMarkers, ReasoningCutoff.ResolveMarkers(null!));
            Assert.Empty(ReasoningCutoff.ResolveMarkers(new List<string>()));
            Assert.Equal(new[] { "budget exhausted" }, ReasoningCutoff.ResolveMarkers(new List<string> { " budget exhausted ", "  " }));

            var off = new ReasoningCutoffCarry(ReasoningCutoff.ResolveMarkers(new List<string>()), carry: true);
            Assert.Null(off.Observe("Thought.\n" + Marker, thinkingOn: true, out bool cutOff));
            Assert.False(cutOff);
        }

        [Fact]
        public void Markers_AreReadFromDevmindJson()
        {
            string dir = Path.Combine(Path.GetTempPath(), $"devmind_h71cfg_{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            try
            {
                string path = Path.Combine(dir, "devmind.json");
                File.WriteAllText(path, "{ \"reasoningCutoffMarkers\": [\"Budget exhausted\", \"\", 3] }");
                var cfg = TuiConfig.LoadFrom(path);
                Assert.Equal(new[] { "Budget exhausted" }, cfg.ReasoningCutoffMarkers);

                var carry = new ReasoningCutoffCarry(ReasoningCutoff.ResolveMarkers(cfg.ReasoningCutoffMarkers), carry: true);
                Assert.NotNull(carry.Observe("Picking the port. BUDGET EXHAUSTED.", thinkingOn: true, out bool custom));
                Assert.True(custom);
                // The configured list replaces the default.
                Assert.Null(carry.Observe("Thought.\n" + Marker, thinkingOn: true, out bool dflt));
                Assert.False(dflt);

                File.WriteAllText(path, "{ \"reasoningEffort\": \"low\" }");
                Assert.Null(TuiConfig.LoadFrom(path).ReasoningCutoffMarkers);
            }
            finally
            {
                try { Directory.Delete(dir, recursive: true); } catch { }
            }
        }

        // ── Tail extraction ──

        [Fact]
        public void LongReasoning_TailIsAtMost600Chars_StartingAtASentence()
        {
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < 60; i++) sb.Append($"Sentence number {i} weighs option {i}. ");
            string reasoning = sb.ToString() + Marker;

            ReasoningCutoff.TryFindMarker(reasoning, ReasoningCutoff.DefaultMarkers, out int idx);
            string tail = ReasoningCutoff.ExtractTail(reasoning, idx);

            Assert.True(tail.Length <= ReasoningCutoff.TailChars, $"tail is {tail.Length} chars");
            Assert.True(tail.Length > 500, $"tail is only {tail.Length} chars");
            Assert.StartsWith("Sentence number", tail);
            Assert.EndsWith("weighs option 59.", tail);
            Assert.DoesNotContain("thought about this long enough", tail, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void LongReasoning_CutsBackToALineStart_AndCollapsesWhitespace()
        {
            string reasoning = new string('x', 700) + "\nthe test host\t keeps\n\n   restarting" + "\n\n" + Marker;
            ReasoningCutoff.TryFindMarker(reasoning, ReasoningCutoff.DefaultMarkers, out int idx);
            Assert.Equal("the test host keeps restarting", ReasoningCutoff.ExtractTail(reasoning, idx));
        }

        [Fact]
        public void ShortReasoning_IsKeptWhole_WithoutTheMarker()
        {
            string reasoning = "Should the fixture own the port?\n\n" + Marker + "\n";
            ReasoningCutoff.TryFindMarker(reasoning, ReasoningCutoff.DefaultMarkers, out int idx);
            Assert.Equal("Should the fixture own the port?", ReasoningCutoff.ExtractTail(reasoning, idx));
        }

        [Fact]
        public void EmptyTail_ProducesNoNote_ButCountsTheCutoff()
        {
            var carry = new ReasoningCutoffCarry(ReasoningCutoff.DefaultMarkers, carry: true);
            Assert.Null(carry.Observe("  \n" + Marker, thinkingOn: true, out bool cutOff));
            Assert.True(cutOff);
            Assert.Equal(1, carry.Cutoffs);
            Assert.Null(carry.Observe(Marker, thinkingOn: true, out _));
            Assert.Equal(2, carry.Cutoffs);
        }

        [Fact]
        public void InlineThinkText_ReadsThinkBlocks()
        {
            Assert.Equal("weighing\n" + Marker, ReasoningCutoff.InlineThinkText("<think>weighing\n" + Marker + "</think>Answer."));
            Assert.Equal("unterminated", ReasoningCutoff.InlineThinkText("<think>unterminated"));
            Assert.Equal("", ReasoningCutoff.InlineThinkText("No reasoning here."));
        }

        // ── Note text and escalation ──

        [Fact]
        public void Note_QuotesTheTail_AndAsksForADecisionInTheScratchpad()
        {
            var carry = new ReasoningCutoffCarry(ReasoningCutoff.DefaultMarkers, carry: true);
            var note = carry.Observe("Is it the test host or the port?\n" + Marker, thinkingOn: true, out _);
            Assert.NotNull(note);
            Assert.StartsWith("[harness] Your previous reasoning was cut off by the thinking budget while working on: \"Is it the test host or the port?\".", note!.Text);
            Assert.Contains("Do not re-open this.", note.Text);
            Assert.Contains("'Decision: …' (or 'Testing: …') to your SCRATCHPAD", note.Text);
            Assert.False(note.Escalated);
            Assert.DoesNotContain(ReasoningCutoffCarry.EscalationText, note.Text);
        }

        [Fact]
        public void ThirdConsecutiveCutoff_Escalates_AnUncutResponseResets()
        {
            var carry = new ReasoningCutoffCarry(ReasoningCutoff.DefaultMarkers, carry: true);
            ReasoningCarryNote? Cut(string t) => carry.Observe(t + "\n" + Marker, thinkingOn: true, out _);

            Assert.False(Cut("one")!.Escalated);
            Assert.False(Cut("two")!.Escalated);
            var third = Cut("three")!;
            Assert.True(third.Escalated);
            Assert.EndsWith("This is the third cut-off in a row. Stop deliberating and run an experiment now.", third.Text);
            Assert.Contains("This is cut-off 4 in a row.", Cut("four")!.Text);

            Assert.Null(carry.Observe("A full answer.", thinkingOn: true, out bool cutOff));
            Assert.False(cutOff);
            Assert.False(Cut("five")!.Escalated);
            Assert.Equal(5, carry.Cutoffs);
        }

        [Fact]
        public void CarryOff_CountsCutoffs_ButBuildsNoNote()
        {
            var carry = new ReasoningCutoffCarry(ReasoningCutoff.DefaultMarkers, carry: false);
            Assert.Null(carry.Observe("Thought.\n" + Marker, thinkingOn: true, out bool cutOff));
            Assert.True(cutOff);
            Assert.Equal(1, carry.Cutoffs);
            Assert.Equal(0, carry.NotesInjected);
        }
    }

    // ── Through LlmClient: the real stream shape, and the one-shot note ──

    public sealed class ReasoningCutoffClientTests : IDisposable
    {
        private readonly FakeSseServer _server = new FakeSseServer();
        private readonly LlmClient _client;

        public ReasoningCutoffClientTests()
        {
            _client = new LlmClient(new HeadlessOptions
            {
                SystemPrompt = "test",
                ShowLlmThinking = true,
                RequestTimeoutMinutes = 1,
                FirstTokenTimeoutMinutes = 1,
                ManualContextSize = 32768, // skip context probes
            });
            string? prior = Environment.GetEnvironmentVariable("DEVMIND_SERVER_TYPE");
            Environment.SetEnvironmentVariable("DEVMIND_SERVER_TYPE", "llama");
            try { _client.Configure(_server.BaseUrl, apiKey: null!); }
            finally { Environment.SetEnvironmentVariable("DEVMIND_SERVER_TYPE", prior); }
        }

        public void Dispose() => _server.Dispose();

        private static string Delta(string field, string text)
            => "data: {\"choices\":[{\"index\":0,\"delta\":{\"" + field + "\":" + JsonConvert.SerializeObject(text) + "},\"finish_reason\":null}]}\n\n";

        // The last reasoning_content deltas and first content deltas of a real Strata stream
        // (qwen3.8-flash, enable_thinking, effort medium, 2026-10-05) — token for token. The
        // marker arrives split across reasoning deltas; content starts after it.
        private static string RealStrataCutoffStream()
        {
            string[] reasoning =
            {
                "-", " f", "(n", ")", " mod", " ", "4", "7", ":", " n", "²", " +", " n", " +", " ", "4", "1", "\n\n",
                "I", " have", " thought", " about", " this", " long", " enough", ";", " time", " to", " give", " my", " answer", ".", "\n",
            };
            string[] content = { "#", " Composite", " values" };
            var sb = new System.Text.StringBuilder();
            foreach (string t in reasoning) sb.Append(Delta("reasoning_content", t));
            foreach (string t in content) sb.Append(Delta("content", t));
            return sb.Append("data: [DONE]\n\n").ToString();
        }

        private async Task SendAsync(string message)
        {
            var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            await _client.SendMessageAsync(message, onToken: _ => { },
                onComplete: () => done.TrySetResult(true), onError: ex => done.TrySetException(ex));
            await done.Task.WaitAsync(TimeSpan.FromSeconds(15));
        }

        private List<string> Bodies() { lock (_server.RequestBodies) return _server.RequestBodies.ToList(); }

        [Fact]
        public async Task RealStrataStream_MarkerArrivesInReasoning_NotContentOrHistory()
        {
            _server.SseQueue.Add(RealStrataCutoffStream());
            await SendAsync("enumerate");

            Assert.EndsWith("I have thought about this long enough; time to give my answer.\n", _client.LastReasoning);
            Assert.Equal("# Composite values", _client.LastAssistantText);
            Assert.DoesNotContain(_client.HistoryContentsForTest, c => c != null && c.Contains("long enough", StringComparison.Ordinal));

            var carry = new ReasoningCutoffCarry(ReasoningCutoff.DefaultMarkers, carry: true);
            var note = carry.Observe(_client.LastReasoning, thinkingOn: true, out bool cutOff);
            Assert.True(cutOff);
            Assert.Contains("while working on: \"- f(n) mod 47: n² + n + 41\".", note!.Text);
        }

        [Fact]
        public async Task NextRequestNote_IsSecondToLast_Once_ThenGone_AndNeverInHistory()
        {
            const string Note = "[harness] carried thought XYZZY";
            _server.SseQueue.Add(FakeSseServer.BuildTextSse("one"));
            _server.SseQueue.Add(FakeSseServer.BuildTextSse("two"));
            _server.SseQueue.Add(FakeSseServer.BuildTextSse("three"));

            await SendAsync("first");
            _client.SetNextRequestNote(Note);
            await SendAsync("second");
            Assert.DoesNotContain(_client.HistoryContentsForTest, c => c != null && c.Contains("XYZZY", StringComparison.Ordinal));
            await SendAsync("third");

            var bodies = Bodies();
            Assert.Equal(3, bodies.Count);
            Assert.DoesNotContain("XYZZY", bodies[0]);
            Assert.DoesNotContain("XYZZY", bodies[2]);

            var messages = (JArray)JObject.Parse(bodies[1])["messages"]!;
            Assert.Single(messages, m => ((string?)m["content"] ?? "").Contains("XYZZY", StringComparison.Ordinal));
            Assert.Equal(Note, (string?)messages[messages.Count - 2]["content"]);
            Assert.Equal("user", (string?)messages[messages.Count - 2]["role"]);
            Assert.Equal("second", (string?)messages[messages.Count - 1]["content"]);
            Assert.DoesNotContain(_client.HistoryContentsForTest, c => c != null && c.Contains("XYZZY", StringComparison.Ordinal));
        }
    }

    // ── Through the real headless loop ──

    public sealed class ReasoningCutoffSessionTests : IDisposable
    {
        private const string Marker = "I have thought about this long enough; time to give my answer.";
        private const string NoteHead = "Your previous reasoning was cut off by the thinking budget";
        private const string Escalation = "This is the third cut-off in a row.";

        private readonly string _dir;
        private string NoPromptFile => Path.Combine(_dir, "no-system-prompt.md");
        private string TranscriptPath => Path.Combine(_dir, "transcript.log");

        public ReasoningCutoffSessionTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), $"devmind_h71_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_dir);
            File.WriteAllText(Path.Combine(_dir, "a.txt"), "a");
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch { }
        }

        private static string ListFiles => FakeSseServer.BuildToolCallSse("list_files", "{\"glob\":\"*.txt\"}");
        private static string Done => FakeSseServer.BuildToolCallSse("task_done", "{\"summary\":\"done\"}");

        // A response whose reasoning ends in the cut-off marker, then a tool call.
        private static string Cut(string thought)
            => "data: {\"choices\":[{\"delta\":{\"reasoning_content\":" + JsonConvert.SerializeObject(thought + "\n\n" + Marker + "\n") + "}}]}\n\n"
               + ListFiles;

        private static string Uncut(string thought)
            => "data: {\"choices\":[{\"delta\":{\"reasoning_content\":" + JsonConvert.SerializeObject(thought) + "}}]}\n\n"
               + ListFiles;

        private async Task<(HeadlessAgentResult Result, List<string> Requests, IReadOnlyList<string> History)> Run(
            FakeSseServer server, bool think = true, bool carry = true)
        {
            string? prior = Environment.GetEnvironmentVariable("DEVMIND_SERVER_TYPE");
            Environment.SetEnvironmentVariable("DEVMIND_SERVER_TYPE", "llama");
            try
            {
                using var session = new HeadlessSession(
                    new HeadlessOptions
                    {
                        RequestTimeoutMinutes = 1,
                        FirstTokenTimeoutMinutes = 1,
                        ManualContextSize = 32768,
                        AgenticLoopMaxDepth = 12,
                        ShowLlmThinking = think,
                        ReasoningEffort = "medium",
                        CarryCutoffReasoning = carry,
                    },
                    server.BaseUrl, apiKey: null!, workingDirectory: _dir, buildCommand: "dotnet build",
                    promptFilePath: NoPromptFile);
                var result = await session.RunTurnAsync("Fix the test host.", transcriptPath: TranscriptPath);
                List<string> requests;
                lock (server.RequestBodies) requests = server.RequestBodies.ToList();
                return (result, requests, session.HistoryContentsForTest);
            }
            finally
            {
                Environment.SetEnvironmentVariable("DEVMIND_SERVER_TYPE", prior);
            }
        }

        private static int Count(string body, string text) => Regex.Matches(body, Regex.Escape(text)).Count;

        [Fact]
        public async Task CutoffIteration_NextRequestCarriesTheNoteOnce_TheOneAfterDoesNot_HistoryNever()
        {
            using var server = new FakeSseServer();
            server.SseQueue.Add(Cut("Should the TestHost own port 5005? QUUX"));   // request 1
            server.SseQueue.Add(ListFiles);                                        // request 2: carries the note
            server.SseQueue.Add(Done);                                             // request 3

            var (result, requests, history) = await Run(server);

            Assert.Null(result.Error);
            Assert.Equal(3, requests.Count);
            Assert.Equal(0, Count(requests[0], NoteHead));
            Assert.Equal(1, Count(requests[1], NoteHead));
            Assert.Equal(1, Count(requests[1], "QUUX"));
            Assert.Equal(0, Count(requests[2], NoteHead));
            Assert.Equal(0, Count(requests[2], "QUUX"));
            Assert.DoesNotContain(history, c => c != null && (c.Contains(NoteHead, StringComparison.Ordinal) || c.Contains("QUUX", StringComparison.Ordinal)));

            Assert.Equal(1, result.ThinkBudgetCutoffs);
            Assert.Equal(1, result.ReasoningCarryNotes);
            Assert.Equal(0, result.ReasoningCarryEscalations);
            var note = Assert.Single(result.Actions, a => a.Kind == "harness_note");
            Assert.StartsWith("reasoning cut off by the thinking budget — carried into the next request: \"Should the TestHost own port 5005? QUUX\"", note.Detail);

            string transcript = File.ReadAllText(TranscriptPath);
            Assert.Single(Regex.Matches(transcript, Regex.Escape("(thinking cut off)")));
            Assert.Matches(@"\[AGENTIC\] Iteration 1/12 — .*\(thinking cut off\)", transcript);
        }

        [Fact]
        public async Task ThreeConsecutiveCutoffs_EscalateTheThirdNote_AnUncutIterationResets()
        {
            using var server = new FakeSseServer();
            server.SseQueue.Add(Cut("thought A"));      // 1
            server.SseQueue.Add(Cut("thought B"));      // 2: note A
            server.SseQueue.Add(Cut("thought C"));      // 3: note B
            server.SseQueue.Add(Uncut("settled"));      // 4: note C, escalated
            server.SseQueue.Add(Cut("thought D"));      // 5: no note (4 was not cut off)
            server.SseQueue.Add(Done);                  // 6: note D, not escalated

            var (result, requests, _) = await Run(server);

            Assert.Null(result.Error);
            Assert.Equal(6, requests.Count);
            Assert.Equal(new[] { 0, 1, 1, 1, 0, 1 }, requests.Select(r => Count(r, NoteHead)));
            Assert.Equal(new[] { 0, 0, 0, 1, 0, 0 }, requests.Select(r => Count(r, Escalation)));
            Assert.Contains("thought C", requests[3]);
            Assert.Contains("thought D", requests[5]);

            Assert.Equal(4, result.ThinkBudgetCutoffs);
            Assert.Equal(4, result.ReasoningCarryNotes);
            Assert.Equal(1, result.ReasoningCarryEscalations);
            Assert.Equal(4, result.Actions.Count(a => a.Kind == "harness_note"));
        }

        [Fact]
        public async Task CarryOff_CountsCutoffs_InjectsNothing()
        {
            using var server = new FakeSseServer();
            server.SseQueue.Add(Cut("thought A"));
            server.SseQueue.Add(Cut("thought B"));
            server.SseQueue.Add(Done);

            var (result, requests, _) = await Run(server, carry: false);

            Assert.Null(result.Error);
            Assert.All(requests, r => Assert.Equal(0, Count(r, NoteHead)));
            Assert.Equal(2, result.ThinkBudgetCutoffs);
            Assert.Equal(0, result.ReasoningCarryNotes);
            Assert.Equal(0, result.ReasoningCarryEscalations);
            Assert.DoesNotContain(result.Actions, a => a.Kind == "harness_note");
            Assert.Equal(2, Regex.Matches(File.ReadAllText(TranscriptPath), Regex.Escape("(thinking cut off)")).Count);
        }

        [Fact]
        public async Task ThinkingOff_NoDetection_NoNote()
        {
            using var server = new FakeSseServer();
            server.SseQueue.Add(Cut("thought A"));
            server.SseQueue.Add(Done);

            var (result, requests, _) = await Run(server, think: false);

            Assert.Null(result.Error);
            Assert.All(requests, r => Assert.Equal(0, Count(r, NoteHead)));
            Assert.Equal(0, result.ThinkBudgetCutoffs);
            Assert.Equal(0, result.ReasoningCarryNotes);
            Assert.DoesNotContain("(thinking cut off)", File.ReadAllText(TranscriptPath));
        }
    }
}
