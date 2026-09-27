// File: BuildWarningsStateTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// H-32 follow-up: the standard is 0 errors AND 0 warnings, and the harness's full rebuild now
// gives a trustworthy warning count, so a VERIFIED count above zero makes the job
// stopped_incomplete with reason build_warnings, the count, and the first few warning lines.
// An unverified count (incremental build) never changes the state — the number is not evidence.

using DevMind.McpServer;
using Xunit;

namespace DevMind.McpServer.Tests
{
    public sealed class BuildWarningsStateTests
    {
        private static readonly string[] TwoWarnings =
        {
            "C:\\src\\A.cs(3,9): warning CS0168: The variable 'x' is declared but never used",
            "C:\\src\\B.cs(7,1): warning CS8618: Non-nullable field 'y' must contain a non-null value",
        };

        private static AgentJob Job(bool verified, int? count, IReadOnlyList<string>? lines = null) => new()
        {
            Id = "job-h32w",
            Prompt = "p",
            WorkingDirectory = @"C:\temp\hermetic",
            VerifyBuild = true,
            State = AgentJobState.Done,
            Result = new HeadlessAgentResult { Answer = "Added the handler; suite green." },
            Build = new BuildVerification
            {
                Command = "dotnet build x.slnx -t:Rebuild",
                ExitCode = 0,
                OutputTail = "Build succeeded.",
                WarningCountVerified = verified,
                WarningCount = count,
                WarningLines = lines ?? Array.Empty<string>(),
            },
        };

        [Fact]
        public void VerifiedZeroWarnings_Done()
        {
            var job = Job(verified: true, count: 0);
            Assert.False(job.IsIncomplete);
            Assert.Empty(job.IncompleteReasons());
        }

        [Fact]
        public void VerifiedTwoWarnings_StoppedIncomplete_WithReasonCountAndLines()
        {
            var job = Job(verified: true, count: 2, TwoWarnings);

            Assert.True(job.IsIncomplete);
            string[] reasons = job.IncompleteReasons();
            Assert.Equal(new[] { "build_warnings", "harness rebuild reported 2 warning(s)" }.Concat(TwoWarnings), reasons);
        }

        [Fact]
        public void VerifiedWarnings_WithNoExtractableLines_StillReportTheCount()
        {
            var job = Job(verified: true, count: 3);
            Assert.True(job.IsIncomplete);
            Assert.Equal(new[] { "build_warnings", "harness rebuild reported 3 warning(s)" }, job.IncompleteReasons());
        }

        [Theory]
        [InlineData(7)]
        [InlineData(null)]
        public void UnverifiedCount_NeverFailsTheJob(int? count)
        {
            // Incremental build (DEVMIND_BUILD_COMMAND, DEVMIND_VERIFY_REBUILD=0, another build
            // system): up-to-date projects don't re-emit warnings, so the count is not evidence.
            var job = Job(verified: false, count: count, TwoWarnings);
            Assert.False(job.IsIncomplete);
            Assert.DoesNotContain("build_warnings", job.IncompleteReasons());
        }

        [Fact]
        public void AFailedBuild_ReportsBuildFailure_NotWarnings()
        {
            var job = Job(verified: true, count: 0);
            job.Build = new BuildVerification { Command = "dotnet build x.slnx -t:Rebuild", ExitCode = 1, OutputTail = "error CS0103", WarningCountVerified = true, WarningCount = 0 };
            Assert.Equal(new[] { "build_verification_failed" }, job.IncompleteReasons());
        }
    }
}
