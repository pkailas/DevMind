// File: ToolUsePromptWorkingDirectoryTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Telling the model where it is.
//
// This is the guard for a defect that cost a whole run. The directive demands that every
// file-path argument be ABSOLUTE, and the only absolute path it named was the scratch output
// directory. Asked to work "in this folder", with no root given and one anchor available, the
// model wrote into <temp>\devmind\session1\ — a reasonable inference from what it had been
// told. The next shell command ran in the real working directory, could not find the file,
// and the folder the operator was watching stayed empty for the whole run.
//
// So the order matters as much as the presence. The working directory has to be stated before
// the output directory is offered, or the same inference is available again: the first
// absolute path a reader meets is the one they anchor on.

using Xunit;

namespace DevMind.Core.Tests
{
    public class ToolUsePromptWorkingDirectoryTests
    {
        private const string Dir = @"C:\Users\pkailas\AppData\Local\Temp\fmt-test";

        private static string Prompt(string? workingDirectory)
            => LoopHelpers.BuildToolUsePrompt("dotnet build", projectNamespace: null,
                                              workingDirectory: workingDirectory);

        [Fact]
        public void TheDirectiveNamesTheWorkingDirectory()
        {
            Assert.Contains($"Working directory: {Dir}", Prompt(Dir), StringComparison.Ordinal);
        }

        [Fact]
        public void ItSaysWhatRelativePathsAndThisFolderMean()
        {
            // The phrase in the operator's prompt is almost always "this folder". Without
            // this sentence the model has to guess what it refers to.
            string prompt = Prompt(Dir);

            Assert.Contains("Relative paths in every tool resolve against it", prompt, StringComparison.Ordinal);
            Assert.Contains("\"this folder\" means it", prompt, StringComparison.Ordinal);
        }

        [Fact]
        public void TheWorkingDirectoryIsStatedBeforeTheOutputDirectoryIsOffered()
        {
            // The whole defect in one assertion: the first absolute path the model meets is
            // the one it anchors on, and it used to be the scratch directory.
            string prompt = Prompt(Dir);

            int working = prompt.IndexOf("Working directory:", StringComparison.Ordinal);
            int output  = prompt.IndexOf("Large Output & Reports", StringComparison.Ordinal);

            Assert.True(working >= 0, "the directive no longer names the working directory");
            Assert.True(output > working,
                $"the output directory is offered at {output}, before the working directory at {working}");
        }

        [Fact]
        public void TheOutputDirectoryIsMarkedAsScratch_NotAPlaceToWorkIn()
        {
            // It was described only as "the dedicated output directory", which reads like a
            // sanctioned place to put things — including, as it turned out, a session folder.
            string prompt = Prompt(Dir);

            Assert.Contains("SCRATCH ARTIFACTS, NOT the working tree", prompt, StringComparison.Ordinal);
            Assert.Contains("not a place to create subfolders to work in", prompt, StringComparison.Ordinal);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void WithNoWorkingDirectoryTheLineIsOmitted_NotLeftBlank(string? workingDirectory)
        {
            // "Working directory: " with nothing after it is worse than silence: it states a
            // fact and then withholds it, and the model has no way to tell that from a root
            // that is genuinely the empty string.
            string prompt = Prompt(workingDirectory);

            Assert.DoesNotContain("Working directory:", prompt, StringComparison.Ordinal);
        }

        [Fact]
        public void TheRestOfTheDirectiveIsUnchangedByTheAddition()
        {
            // The sections every other test in this suite pins are still there and still in
            // the same order — this added a block, it did not rearrange the prompt.
            string prompt = Prompt(Dir);

            int catalog  = prompt.IndexOf("## Tool Catalog", StringComparison.Ordinal);
            int contract = prompt.IndexOf("## Termination Contract", StringComparison.Ordinal);
            int paths    = prompt.IndexOf("## Path Format", StringComparison.Ordinal);

            Assert.True(catalog >= 0 && contract > catalog && paths > contract);
        }
    }
}
