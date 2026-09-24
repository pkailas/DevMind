// File: AskCallerFindingsFirstTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Twice in one session a delegated agent called ask_caller with questions and nothing
// established behind them — once directly after being told not to. The caller then had to
// re-derive what the agent already knew before it could answer, which is the expensive half
// of the round trip and the half the agent was in a position to prevent.
//
// The prompts were the reason. Every place that mentioned ask_caller framed it purely as an
// exhaustion outcome — "after real research", "if research produces no new hypothesis" — so
// the agent learned WHEN to stop and never learned what a stop has to contain. The schema
// asked for "what you already tried", which an agent satisfies with a narration of attempts
// while never stating a single fact it established.
//
// So these pin shape, not timing: the rule exists, it carries its reason (the caller has not
// read this code), and it is positioned to be read BEFORE the exhaustion trigger rather than
// after it — a rule that arrives after the trigger that fires it is a rule that does not fire.

using Xunit;

namespace DevMind.Core.Tests
{
    public class AskCallerFindingsFirstTests
    {
        private const string Addendum = HeadlessAgent.HeadlessAddendum;

        // The prompt is hard-wrapped, and which word a wrap lands on is incidental
        // formatting no assertion here may be coupled to.
        private static string Flat(string s) => s.Replace('\n', ' ');

        [Fact]
        public void Addendum_RequiresFindingsBeforeQuestions()
        {
            string flat = Flat(Addendum);

            Assert.Contains("Lead every ask_caller with findings, then questions.", flat, StringComparison.Ordinal);

            // The three parts of the required order, in the order they must be written.
            Assert.Contains("name the files and lines", flat, StringComparison.Ordinal);
            Assert.Contains("what you tried and what each attempt produced", flat, StringComparison.Ordinal);
            Assert.Contains("only then ask 1-3 specific questions", flat, StringComparison.Ordinal);
        }

        // A bare rule is what gets rationalised past. What makes this one stick is the fact
        // the model cannot derive for itself: the caller has not read this code, so a bare
        // question is not a cheap stop — it hands back work the agent was already holding.
        [Fact]
        public void Addendum_CarriesTheReason_NotJustTheRule()
        {
            string flat = Flat(Addendum);

            Assert.Contains("is not a stop, it is an incomplete report", flat, StringComparison.Ordinal);
            Assert.Contains("the caller has not read this code", flat, StringComparison.Ordinal);
            Assert.Contains("Asking is cheap only when the findings travel with it.", flat, StringComparison.Ordinal);
        }

        // Placement, not merely presence. The exhaustion trigger ("research produced no new
        // hypothesis, so ask") is the sentence that sends the agent to ask_caller; the rule
        // governing what an ask contains has to have been read by the time it fires.
        [Fact]
        public void TheRule_IsReadBeforeTheExhaustionTrigger()
        {
            string flat = Flat(Addendum);

            int rule = flat.IndexOf("Lead every ask_caller with findings", StringComparison.Ordinal);
            int trigger = flat.IndexOf("If research produces no new hypothesis", StringComparison.Ordinal);

            Assert.True(rule >= 0, "the findings-first rule is missing from the headless addendum entirely");
            Assert.True(trigger >= 0, "the exhaustion trigger moved — this guard needs revisiting");
            Assert.True(rule < trigger,
                $"the findings-first rule (at {rule}) now sits after the exhaustion trigger (at {trigger}) " +
                "that sends the agent to ask_caller, so the agent reads 'ask' before it reads what an ask must contain");
        }

        // Its own blank-line-delimited block, like the other standing rules in this prompt.
        // A rule buried mid-paragraph in a long prompt does not fire.
        [Fact]
        public void TheRule_IsItsOwnBlock()
        {
            string[] blocks = Addendum.Split("\n\n", StringSplitOptions.RemoveEmptyEntries);

            int index = Array.FindIndex(blocks,
                b => b.Contains("Lead every ask_caller with findings", StringComparison.Ordinal));
            Assert.True(index >= 0, "the findings-first rule is not a block of its own — it has been buried mid-paragraph");

            Assert.StartsWith("Lead every ask_caller with findings",
                blocks[index].TrimStart(), StringComparison.Ordinal);
        }

        // The exhaustion trigger itself points back at the rule, so the sentence that sends
        // the agent to ask_caller does not read as a licence to send a bare question.
        [Fact]
        public void TheExhaustionTrigger_PointsBackAtTheRule()
        {
            Assert.Contains("call ask_caller instead of\ntrying again — findings first, as above.",
                Addendum, StringComparison.Ordinal);
        }

        // The shared tool-use prompt drives the TUI and console runs, which never see the
        // headless addendum. Both prompts have to say the same thing or the rule applies to
        // delegated agents only.
        [Fact]
        public void SharedToolUsePrompt_AgreesWithTheAddendum()
        {
            string prompt = LoopHelpers.BuildToolUsePrompt(buildCommand: "", projectNamespace: "",
                                                           workingDirectory: @"C:\work\repo");

            Assert.Contains("ask_caller is the valid way to stop, with your findings stated before the questions",
                prompt, StringComparison.Ordinal);
        }

        // The schema is the last thing the model reads before it fills the call in, and
        // "tried" is the field the findings have to arrive in — the parameter shape is
        // deliberately unchanged, so the description is what carries the requirement.
        [Fact]
        public void AskCallerSchema_AsksForFindingsFirst()
        {
            var ask = ToolRegistry.BuildToolsArray()
                .FirstOrDefault(t => (string?)t["function"]?["name"] == "ask_caller");
            Assert.True(ask != null, "ask_caller is missing from the registry — this guard is vacuous");

            string description = (string?)ask!["function"]?["description"] ?? "";
            Assert.Contains("findings", description, StringComparison.OrdinalIgnoreCase);

            string tried = (string?)ask["function"]?["parameters"]?["properties"]?["tried"]?["description"] ?? "";
            Assert.True(tried.Length > 0, "the 'tried' parameter is gone — the findings have nowhere to arrive");
            Assert.Contains("Your findings first", tried, StringComparison.Ordinal);
            Assert.Contains("naming files and lines", tried, StringComparison.Ordinal);
        }

        // Timing is explicitly NOT what changed. The rule governs the shape of an ask; the
        // triggers for when to ask, and the instruction to proceed through minor ambiguities
        // rather than asking about them, must both survive it.
        [Fact]
        public void WhenToAsk_IsUnchanged()
        {
            string flat = Flat(Addendum);

            Assert.Contains("For minor ambiguities make the most reasonable choice", flat, StringComparison.Ordinal);
            Assert.Contains("blocked on a consequential decision", flat, StringComparison.Ordinal);
            Assert.Contains("NEVER guess at facts you could not verify", flat, StringComparison.Ordinal);
        }
    }
}
