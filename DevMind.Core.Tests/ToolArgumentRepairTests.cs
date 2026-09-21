// File: ToolArgumentRepairTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// The repair ladder for a tool call's function.arguments. Before it existed there was one
// rung — a parse failure became "{}" — so a truncated call ran the tool with no arguments
// and failed for a reason unrelated to the truncation.
//
// Every test here asserts the repair produced the RIGHT arguments, never merely that it did
// not throw: a rung that turns {"filename":"a.cs","start_line":1 into {} has not thrown
// either, and is exactly the behaviour being replaced.
//
// Rung attribution is asserted alongside the value, because that is what makes the mutation
// tests meaningful — removing a rung must show up as the payload falling THROUGH to the next
// one, not merely as a different answer.

using Xunit;

namespace DevMind.Core.Tests
{
    public class ToolArgumentRepairTests
    {
        private static string? ArgOf(string json, string key)
            => (string?)Newtonsoft.Json.Linq.JObject.Parse(json)[key];

        // ── Rung 0: a clean payload is untouched ────────────────────────────────

        [Fact]
        public void WellFormedArguments_PassThroughByteForByte()
        {
            const string raw = "{\"filename\":\"Program.cs\",\"start_line\":10,\"end_line\":20}";

            var r = ToolArgumentRepair.Repair(raw);

            Assert.Equal(ToolArgumentRung.Clean, r.Rung);
            Assert.False(r.Repaired);
            Assert.Equal(raw, r.Json);          // compact in, compact out — identical
            Assert.Null(r.Describe("read_file"));
        }

        // Newtonsoft already accepts these at rung 0, so they must NOT be reported as
        // repairs. A ladder that claimed to have fixed them would be lying about the model.
        [Theory]
        [InlineData("{a:1}")]                       // unquoted key
        [InlineData("{'a':'b'}")]                   // single quotes
        [InlineData("{\"a\":1,}")]                  // trailing comma
        [InlineData("{\"a\":1,\"a\":2}")]           // duplicate keys
        public void ShapesNewtonsoftAlreadyAccepts_AreNotReportedAsRepairs(string raw)
        {
            var r = ToolArgumentRepair.Repair(raw);

            Assert.Equal(ToolArgumentRung.Clean, r.Rung);
            Assert.False(r.Repaired);
        }

        [Fact]
        public void AbsentArguments_AreTheEmptyObject_AndNotAFailure()
        {
            foreach (string? raw in new string?[] { null, "", "   " })
            {
                var r = ToolArgumentRepair.Repair(raw);
                Assert.Equal(ToolArgumentRung.Absent, r.Rung);
                Assert.Equal("{}", r.Json);
                Assert.False(r.Failed);
                Assert.Null(r.Describe("read_file"));
            }
        }

        // ── Rung 1: truncation ──────────────────────────────────────────────────

        [Fact]
        public void Truncation_UnclosedString_KeepsThePartialValue()
        {
            // Generation stopped mid-value — the dominant local-model failure.
            var r = ToolArgumentRepair.Repair("{\"command\":\"dotnet bui");

            Assert.Equal(ToolArgumentRung.ClosedTruncation, r.Rung);
            Assert.Equal("dotnet bui", ArgOf(r.Json, "command"));
        }

        [Fact]
        public void Truncation_UnclosedBrace_RecoversEveryCompleteMember()
        {
            var r = ToolArgumentRepair.Repair("{\"filename\":\"Program.cs\",\"start_line\":10");

            Assert.Equal(ToolArgumentRung.ClosedTruncation, r.Rung);
            Assert.Equal("Program.cs", ArgOf(r.Json, "filename"));
            Assert.Equal("10", ArgOf(r.Json, "start_line"));
        }

        // The cut landed after a key's colon, where closing the brace alone yields
        // {"a":1,"b":} — invalid. The second candidate drops the half-written member and
        // keeps everything that WAS complete.
        [Fact]
        public void Truncation_DanglingKey_DropsOnlyTheIncompleteMember()
        {
            var r = ToolArgumentRepair.Repair("{\"filename\":\"Program.cs\",\"start_line\":");

            Assert.Equal(ToolArgumentRung.ClosedTruncation, r.Rung);
            Assert.Equal("Program.cs", ArgOf(r.Json, "filename"));
            Assert.DoesNotContain("start_line", r.Json);
        }

        [Fact]
        public void Truncation_NestedArray_ClosesInnermostFirst()
        {
            var r = ToolArgumentRepair.Repair("{\"paths\":[\"a.cs\",\"b.cs\"");

            Assert.Equal(ToolArgumentRung.ClosedTruncation, r.Rung);
            Assert.Equal("[\"a.cs\",\"b.cs\"]", Newtonsoft.Json.Linq.JObject.Parse(r.Json)["paths"]!
                .ToString(Newtonsoft.Json.Formatting.None));
        }

        // Escaped quotes inside a value must not be read as the string ending — the scanner
        // would otherwise think the document closed and refuse to repair it.
        [Fact]
        public void Truncation_EscapedQuoteInsideValue_IsNotMistakenForTheEnd()
        {
            var r = ToolArgumentRepair.Repair("{\"content\":\"say \\\"hi\\\" and then stop");

            Assert.Equal(ToolArgumentRung.ClosedTruncation, r.Rung);
            Assert.Equal("say \"hi\" and then stop", ArgOf(r.Json, "content"));
        }

        // ── Rung 2: rewrite ─────────────────────────────────────────────────────

        [Fact]
        public void Rewrite_DuplicatedStream_TakesTheFirstCompleteObject()
        {
            var r = ToolArgumentRepair.Repair("{\"filename\":\"a.cs\"}{\"filename\":\"a.cs\"}");

            Assert.Equal(ToolArgumentRung.Rewritten, r.Rung);
            Assert.Equal("{\"filename\":\"a.cs\"}", r.Json);
        }

        [Fact]
        public void Rewrite_TrailingProse_IsDropped()
        {
            var r = ToolArgumentRepair.Repair("{\"filename\":\"a.cs\"} — now let me read it");

            Assert.Equal(ToolArgumentRung.Rewritten, r.Rung);
            Assert.Equal("a.cs", ArgOf(r.Json, "filename"));
        }

        [Fact]
        public void Rewrite_MarkdownFence_IsStripped()
        {
            var r = ToolArgumentRepair.Repair("```json\n{\"filename\":\"a.cs\"}\n```");

            Assert.Equal(ToolArgumentRung.Rewritten, r.Rung);
            Assert.Equal("a.cs", ArgOf(r.Json, "filename"));
        }

        [Fact]
        public void Rewrite_PythonLiterals_BecomeJsonLiterals()
        {
            var r = ToolArgumentRepair.Repair("{\"recursive\":True,\"force\":False,\"glob\":None}");

            Assert.Equal(ToolArgumentRung.Rewritten, r.Rung);
            Assert.Equal("True", ArgOf(r.Json, "recursive"));     // JToken renders bool true as "True"
            Assert.Equal(Newtonsoft.Json.Linq.JTokenType.Boolean,
                Newtonsoft.Json.Linq.JObject.Parse(r.Json)["recursive"]!.Type);
            Assert.Equal(Newtonsoft.Json.Linq.JTokenType.Null,
                Newtonsoft.Json.Linq.JObject.Parse(r.Json)["glob"]!.Type);
        }

        // The word must only be rewritten where it is a literal. A value that happens to be
        // the text "None" is data, and corrupting it would be worse than the parse failure.
        [Fact]
        public void Rewrite_PythonLiteralsInsideAStringValue_AreLeftAlone()
        {
            var r = ToolArgumentRepair.Repair("{\"pattern\":\"None of this\",\"recursive\":True}");

            Assert.Equal(ToolArgumentRung.Rewritten, r.Rung);
            Assert.Equal("None of this", ArgOf(r.Json, "pattern"));
        }

        [Fact]
        public void Rewrite_FencedAndDuplicated_HandlesBothInOnePass()
        {
            var r = ToolArgumentRepair.Repair("```\n{\"filename\":\"a.cs\"}{\"filename\":\"b.cs\"}\n```");

            Assert.Equal(ToolArgumentRung.Rewritten, r.Rung);
            Assert.Equal("a.cs", ArgOf(r.Json, "filename"));
        }

        // ── Rung 3: the fallback ────────────────────────────────────────────────

        [Fact]
        public void Unrepairable_FallsBackToTheEmptyObject_AndSaysSo()
        {
            var r = ToolArgumentRepair.Repair("not json at all, just a sentence");

            Assert.Equal(ToolArgumentRung.Failed, r.Rung);
            Assert.Equal("{}", r.Json);
            Assert.True(r.Failed);

            string note = r.Describe("read_file");
            Assert.NotNull(note);
            Assert.Contains("read_file", note!);
            Assert.Contains("could not be parsed or repaired", note!);
            Assert.Contains("running with empty arguments", note!);
            Assert.Contains("not json at all", note!);          // the raw head, for diagnosis
        }

        // A truncation so early that nothing survives is a FAILURE, not a repair. Reporting
        // an empty object as "recovered" would be the ladder lying about what it achieved —
        // and the operator would read a truncation that lost everything as one that was
        // handled. "{" is the case that matters most: closing it yields a perfectly valid
        // "{}", which only the empty-result rejection distinguishes from a real recovery.
        [Theory]
        [InlineData("{")]                    // closing this gives "{}" — valid, but nothing was recovered
        [InlineData("{ ")]
        [InlineData("{\"filename\":")]       // no complete member to fall back to
        [InlineData("{\"filename")]          // truncated inside the KEY
        public void TruncatedBeforeAnyCompleteMember_IsReportedAsFailure_NotAsRepair(string raw)
        {
            var r = ToolArgumentRepair.Repair(raw);

            Assert.Equal(ToolArgumentRung.Failed, r.Rung);
            Assert.Equal("{}", r.Json);
            Assert.False(r.Repaired);
            Assert.Contains("could not be parsed or repaired", r.Describe("read_file")!);
        }

        // ── Describe(): repairs are announced, clean calls are not ──────────────

        [Fact]
        public void Describe_NamesTheToolAndTheSize_ForEachRepairedRung()
        {
            string truncated = ToolArgumentRepair.Repair("{\"command\":\"dotnet bui").Describe("run_shell");
            Assert.Contains("run_shell", truncated!);
            Assert.Contains("truncated at 22 chars", truncated!);

            string rewritten = ToolArgumentRepair.Repair("{\"a\":1}{\"a\":1}").Describe("run_shell");
            Assert.Contains("malformed", rewritten!);
            Assert.Contains("repaired", rewritten!);
        }
    }
}
