// File: ApprovalPolicyTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Every (mode, kind) pair, exhaustively and by enumeration rather than by a hand-written
// list. The point of putting the decision in one pure function was that adding a mutation
// tool forces a decision here instead of silently shipping ungated — a test that names the
// six kinds it knows about would defeat that, because a seventh would simply not be tested.
//
// So these iterate Enum.GetValues: a new MutationKind fails this file until someone says
// what each mode does with it.

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
        public void Manual_RequiresApprovalForEveryMutation(MutationKind kind)
        {
            Assert.True(ApprovalPolicy.Requires(ApprovalMode.Manual, kind),
                $"Manual did not require approval for {kind} — an ungated mutation in manual mode is the whole defect this prevents.");
        }

        // The guard against a vacuous file: if MutationKind were ever emptied, both theories
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
        [InlineData("AUTO", ApprovalMode.Auto)]
        [InlineData("Manual", ApprovalMode.Manual)]
        [InlineData("  manual  ", ApprovalMode.Manual)]
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
        [InlineData("plan")]
        public void UnknownText_DoesNotParse_AndYieldsAuto(string? text)
        {
            Assert.False(ApprovalModeText.TryParse(text!, out var mode));
            Assert.Equal(ApprovalMode.Auto, mode);
        }

        [Theory]
        [InlineData(ApprovalMode.Auto, "auto")]
        [InlineData(ApprovalMode.Manual, "manual")]
        public void TheModeFormatsBackToItsOwnSpelling(ApprovalMode mode, string expected)
        {
            Assert.Equal(expected, ApprovalModeText.Format(mode));

            // Round-trips, so a value written to devmind.json reads back as itself.
            Assert.True(ApprovalModeText.TryParse(ApprovalModeText.Format(mode), out var back));
            Assert.Equal(mode, back);
        }
    }
}
