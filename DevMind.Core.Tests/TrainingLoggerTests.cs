// File: TrainingLoggerTests.cs  v1.0
//
// Unit tests for JsonlTrainingLogger constructor/property guards and
// ExtractToolResults static method behavior.

using System;
using System.Collections.Generic;
using Xunit;

namespace DevMind.Core.Tests
{
    public class TrainingLoggerTests
    {
        #region Guard Tests

        [Fact]
        public void Ctor_EnabledWithBlankFolder_Throws()
        {
            var ex = Assert.Throws<ArgumentException>(() =>
                new JsonlTrainingLogger(() => "test-session", enabled: true, logFolder: null));
            Assert.Contains("trainingLogFolder", ex.Message);
        }

        [Fact]
        public void Ctor_EnabledWithWhitespaceFolder_Throws()
        {
            var ex = Assert.Throws<ArgumentException>(() =>
                new JsonlTrainingLogger(() => "test-session", enabled: true, logFolder: "   "));
            Assert.Contains("trainingLogFolder", ex.Message);
        }

        [Fact]
        public void Ctor_DisabledWithBlankFolder_DoesNotThrow()
        {
            var logger = new JsonlTrainingLogger(() => "test-session", enabled: false, logFolder: null);
            Assert.False(logger.Enabled);
        }

        [Fact]
        public void Ctor_EnabledWithFolder_DoesNotThrow()
        {
            var logger = new JsonlTrainingLogger(() => "test-session", enabled: true, logFolder: @"C:\temp\tl-test");
            Assert.True(logger.Enabled);
            Assert.Equal(@"C:\temp\tl-test", logger.Folder);
        }

        [Fact]
        public void EnabledSetter_TrueWithBlankFolder_Throws()
        {
            var logger = new JsonlTrainingLogger(() => "test-session", enabled: false, logFolder: null);
            var ex = Assert.Throws<ArgumentException>(() => { logger.Enabled = true; });
            Assert.Contains("trainingLogFolder", ex.Message);
        }

        [Fact]
        public void FolderSetter_BlankWhileEnabled_Throws()
        {
            var logger = new JsonlTrainingLogger(() => "test-session", enabled: true, logFolder: @"C:\temp\tl-test");
            var ex = Assert.Throws<ArgumentException>(() => { logger.Folder = ""; });
            Assert.Contains("trainingLogFolder", ex.Message);
        }

        [Fact]
        public void FolderSetter_RetargetWhileEnabled_Works()
        {
            var logger = new JsonlTrainingLogger(() => "test-session", enabled: true, logFolder: @"C:\temp\tl-test");
            logger.Folder = @"C:\other\path";
            Assert.Equal(@"C:\other\path", logger.Folder);
            Assert.Equal(@"C:\other\path", logger.ResolvedFolder);
        }

        #endregion

        #region ExtractToolResults Tests

        [Fact]
        public void ExtractToolResults_ShellFailure_CapturesOutputContent()
        {
            var result = new ExecutionResult
            {
                ShellExitCode = 1,
                ShellOutput = "error CS0246: type not found"
            };

            var entries = JsonlTrainingLogger.ExtractToolResults(result);

            Assert.NotNull(entries);
            Assert.Single(entries);
            Assert.Equal("shell", entries[0].Type);
            Assert.False(entries[0].Success);
            Assert.Contains("CS0246", entries[0].Content);
        }

        [Fact]
        public void ExtractToolResults_ShellSuccess_CapturesOutputContent()
        {
            var result = new ExecutionResult
            {
                ShellExitCode = 0,
                ShellOutput = "Build succeeded."
            };

            var entries = JsonlTrainingLogger.ExtractToolResults(result);

            Assert.NotNull(entries);
            Assert.Single(entries);
            Assert.Equal("shell", entries[0].Type);
            Assert.True(entries[0].Success);
            Assert.Equal("Build succeeded.", entries[0].Content);
        }

        [Fact]
        public void ExtractToolResults_Errors_CaptureMessages()
        {
            var result = new ExecutionResult
            {
                Errors = new List<string> { "patch: FIND text not found in X.cs" }
            };

            var entries = JsonlTrainingLogger.ExtractToolResults(result);

            Assert.NotNull(entries);
            Assert.Single(entries);
            Assert.Equal("error", entries[0].Type);
            Assert.False(entries[0].Success);
            Assert.Contains("FIND text not found", entries[0].Content);
        }

        [Fact]
        public void ExtractToolResults_LongContent_TruncatedHeadAndTail()
        {
            string output = new string('a', 2400) + "MIDDLE" + new string('b', 2594);
            Assert.Equal(5000, output.Length);

            var result = new ExecutionResult
            {
                ShellExitCode = 0,
                ShellOutput = output
            };

            var entries = JsonlTrainingLogger.ExtractToolResults(result);

            Assert.NotNull(entries);
            Assert.Single(entries);
            string content = entries[0].Content;

            Assert.True(content.Length < 5000, $"Expected truncated content, got length {content.Length}");
            Assert.Contains("[...truncated, total 5000 chars]", content);
            Assert.StartsWith("a", content);
            Assert.EndsWith("b", content);
            Assert.DoesNotContain("MIDDLE", content);
        }

        [Fact]
        public void ExtractToolResults_NullResult_ReturnsNull()
        {
            var entries = JsonlTrainingLogger.ExtractToolResults(null);
            Assert.Null(entries);
        }

        [Fact]
        public void ExtractToolResults_EmptyResult_ReturnsNull()
        {
            var result = new ExecutionResult();
            var entries = JsonlTrainingLogger.ExtractToolResults(result);
            Assert.Null(entries);
        }

        #endregion
    }
}
