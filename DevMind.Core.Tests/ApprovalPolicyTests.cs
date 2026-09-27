// File: ApprovalPolicyTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Every (mode, kind) pair, exhaustively and by enumeration rather than by a hand-written
// list. The point of putting the decision in one pure function was that adding a mutation
// tool forces a decision here instead of silently shipping ungated — a test that names the
// kinds it knows about would defeat that, because a new one would simply not be tested.
//
// So these iterate Enum.GetValues: a new MutationKind fails this file until someone says
// what each mode does with it.
//
// The one sanctioned exception is Memory (save_memory): it is dispatched today WITHOUT a
// confirm in Manual, and the rule for a new kind is "keep Auto and Manual EXACTLY as
// today" — so Manual + Memory is Allow, and the theories that would fail it are the ones
// written to exclude it, with the reason on the line. If a future change DOES gate
// save_memory in Manual, remove the exemption from both theories; that is a behaviour
// change that deserves its own test, not a silently widened one.

using Xunit;

namespace DevMind.Core.Tests
{
    public class ApprovalPolicyTests
    {
        public static IEnumerable<object[]> AllKinds =>
            Enum.GetValues<MutationKind>().Select(k => new object[] { k });

        [Theory]
        [MemberData(nameof(AllKinds))]
        public void Auto_NeverRequiresApproval(MutationKind kind)
        {
            Assert.False(ApprovalPolicy.Requires(ApprovalMode.Auto, kind),
                $"Auto asked for approval for {kind} — Auto is the long-standing behaviour and must not start prompting.");
        }

        [Theory]
        [MemberData(nameof(AllKinds))]
        public void Manual_RequiresApprovalForEveryGatedMutation(MutationKind kind)
        {
            // The kinds Manual never gated are an explicit, asserted Allow — NOT a skipped
            // row. Memory (save_memory) and SqlWrite (run_sql allow_write) are both
            // dispatched today without a confirm, and a new kind must not change that mode's
            // existing behaviour; asserting the Allow pins the exemption so a future
            // "silently gate it in Manual" shows up here as a failure.
            if (kind == MutationKind.Memory || kind == MutationKind.SqlWrite)
            {
                Assert.True(ApprovalPolicy.Decide(ApprovalMode.Manual, kind) == ApprovalDecision.Allow,
                    $"Manual decided {ApprovalPolicy.Decide(ApprovalMode.Manual, kind)} for {kind} — Manual never gated it; gating it now is a behaviour change this test must catch.");
                return;
            }

            Assert.True(ApprovalPolicy.Requires(ApprovalMode.Manual, kind),
                $"Manual did not require approval for {kind} — an ungated mutation in manual mode is the whole defect this prevents.");
        }

        // The full three-way decision, by enumeration. Plan refuses EVERY kind — that is
        // the whole of plan mode, and it is the row a future kind must not be able to skip.
        [Theory]
        [MemberData(nameof(AllKinds))]
        public void Plan_RefusesEveryMutation(MutationKind kind)
        {
            ApprovalDecision decision = ApprovalPolicy.Decide(ApprovalMode.Plan, kind);
            Assert.True(decision == ApprovalDecision.Refuse,
                $"Plan decided {decision} for {kind} — plan mode is analyze-only and has no exceptions.");
        }

        [Theory]
        [MemberData(nameof(AllKinds))]
        public void Auto_AllowsEveryMutation(MutationKind kind)
        {
            ApprovalDecision decision = ApprovalPolicy.Decide(ApprovalMode.Auto, kind);
            Assert.True(decision == ApprovalDecision.Allow,
                $"Auto decided {decision} for {kind} — Auto is the long-standing behaviour.");
        }

        [Theory]
        [MemberData(nameof(AllKinds))]
        public void Manual_DecidesAskExceptForKindsItNeverGated(MutationKind kind)
        {
            // Memory and SqlWrite are the kinds Manual never gated — an explicit Allow, not
            // a guess: every other kind is Ask.
            ApprovalDecision expected = kind == MutationKind.Memory || kind == MutationKind.SqlWrite
                ? ApprovalDecision.Allow
                : ApprovalDecision.Ask;

            ApprovalDecision decision = ApprovalPolicy.Decide(ApprovalMode.Manual, kind);
            Assert.True(decision == expected,
                $"Manual decided {decision} for {kind} — it must keep its exact existing behaviour ({expected}).");
        }

        // The guard against a vacuous file: if MutationKind were ever emptied, the theories
        // above would pass by running zero times.
        [Fact]
        public void TheKindsCoverEveryMutatingTool()
        {
            var kinds = Enum.GetValues<MutationKind>();

            Assert.Contains(MutationKind.Patch, kinds);
            Assert.Contains(MutationKind.CreateFile, kinds);
            Assert.Contains(MutationKind.AppendFile, kinds);
            Assert.Contains(MutationKind.DeleteFile, kinds);
            Assert.Contains(MutationKind.RenameFile, kinds);
            Assert.Contains(MutationKind.Shell, kinds);
            Assert.Contains(MutationKind.Memory, kinds);
            Assert.Contains(MutationKind.SqlWrite, kinds);
        }

        // Auto is the default everywhere it is not explicitly set, which is what keeps the
        // change inert for every existing user until they ask for it.
        [Fact]
        public void AutoIsTheDefaultEnumValue()
        {
            Assert.Equal(ApprovalMode.Auto, default(ApprovalMode));
        }

        // ── The text form, shared by --mode, devmind.json and /mode ────────────

        [Theory]
        [InlineData("auto", ApprovalMode.Auto)]
        [InlineData("manual", ApprovalMode.Manual)]
        [InlineData("plan", ApprovalMode.Plan)]
        [InlineData("AUTO", ApprovalMode.Auto)]
        [InlineData("Manual", ApprovalMode.Manual)]
        [InlineData("PLAN", ApprovalMode.Plan)]
        [InlineData("  plan  ", ApprovalMode.Plan)]
        public void TheModeParsesFromText(string text, ApprovalMode expected)
        {
            Assert.True(ApprovalModeText.TryParse(text, out var mode));
            Assert.Equal(expected, mode);
        }

        // A typo resolves to false so the caller keeps its default. Starting in the safe
        // default beats refusing to start over a mistyped launch argument.
        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData(null)]
        [InlineData("yolo")]
        [InlineData("manaul")]
        [InlineData("planning")]
        [InlineData("AUTO-ish")]
        public void UnknownText_DoesNotParse_AndYieldsAuto(string? text)
        {
            Assert.False(ApprovalModeText.TryParse(text!, out var mode));
            Assert.Equal(ApprovalMode.Auto, mode);
        }

        [Theory]
        [InlineData(ApprovalMode.Auto, "auto")]
        [InlineData(ApprovalMode.Manual, "manual")]
        [InlineData(ApprovalMode.Plan, "plan")]
        public void TheModeFormatsBackToItsOwnSpelling(ApprovalMode mode, string expected)
        {
            Assert.Equal(expected, ApprovalModeText.Format(mode));

            // Round-trips, so a value written to devmind.json reads back as itself.
            Assert.True(ApprovalModeText.TryParse(ApprovalModeText.Format(mode), out var back));
            Assert.Equal(mode, back);
        }
    }
}
