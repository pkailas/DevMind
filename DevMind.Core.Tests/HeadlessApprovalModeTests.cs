// File: HeadlessApprovalModeTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// A delegated job runs unattended, so there is nobody to answer a confirmation. That makes
// manual mode not merely useless there but actively misleading: BufferedAgenticHost's
// confirm path auto-continues by design (the caller bounds a run with the depth cap and a
// timeout, not by watching it), so a "manual" headless job would print approval prompts
// nobody reads and then perform every mutation anyway.
//
// Two things are pinned. HeadlessOptions is Auto and has no setter, so the mode cannot be
// turned on by a caller or a future refactor; and the confirm path still returns true
// without blocking, which is what makes the first guarantee load-bearing rather than
// decorative — if it ever started blocking, an unattended job would hang forever.

using Xunit;

namespace DevMind.Core.Tests
{
    public class HeadlessApprovalModeTests
    {
        [Fact]
        public void HeadlessOptions_AreAlwaysAuto()
        {
            Assert.Equal(ApprovalMode.Auto, new HeadlessOptions().ApprovalMode);
        }

        // Get-only on purpose. An object initialiser setting it would not compile, which is
        // the point: the guarantee is enforced by the type, not by everyone remembering.
        [Fact]
        public void HeadlessOptions_ExposeNoSetterForTheMode()
        {
            var property = typeof(HeadlessOptions).GetProperty(nameof(HeadlessOptions.ApprovalMode));

            Assert.NotNull(property);
            Assert.False(property!.CanWrite,
                "HeadlessOptions.ApprovalMode gained a setter — an unattended job must not be switchable into a mode that asks questions nobody will answer.");
        }

        // The existing behaviour, pinned as-is: auto-continue, immediately, without waiting.
        // A headless confirm that ever blocked would hang a delegated job indefinitely.
        [Fact]
        public async Task TheHeadlessConfirmPath_NeverBlocks_AndAnswersYes()
        {
            IAgenticHost host = new BufferedAgenticHost(Path.GetTempPath());

            var confirm = host.ConfirmContinueAsync("Delete file Foo.cs?");

            // Already finished: no scheduling, no waiting, no prompt to answer.
            Assert.True(confirm.IsCompleted,
                "the headless confirm path did not complete synchronously — an unattended job would stall here");
            Assert.True(await confirm);
        }
    }
}
