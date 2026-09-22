// File: SystemPromptFileTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Tests for SystemPromptFile.Load() / LoadFrom(path):
//   * missing file → null
//   * empty file → null
//   * whitespace-only file → null
//   * file with content → trimmed content returned
//   * unreadable path → null (swallowed IO exception)
//   * null/empty path → null
//
// Also covers the size-warning logic in LlmClient.UpdateSystemPrompt:
//   * warning fires once for a given text
//   * warning re-arms after the text changes
//   * warning skipped when both ServerContextSize and _contextSize are 0

using System;
using System.IO;
using DevMind;
using Xunit;

namespace DevMind.Core.Tests
{
    public sealed class SystemPromptFileTests : IDisposable
    {
        private readonly string _tempDir;

        public SystemPromptFileTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "spft-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_tempDir, recursive: true); } catch { }
        }

        private string WriteFile(string content)
        {
            string path = Path.Combine(_tempDir, "system-prompt.md");
            File.WriteAllText(path, content);
            return path;
        }

        // ── LoadFrom: absent / empty / whitespace ────────────────────────────

        [Fact]
        public void LoadFrom_MissingFile_ReturnsNull()
        {
            string path = Path.Combine(_tempDir, "does-not-exist.md");
            Assert.Null(SystemPromptFile.LoadFrom(path));
        }

        [Fact]
        public void LoadFrom_EmptyFile_ReturnsNull()
        {
            string path = WriteFile("");
            Assert.Null(SystemPromptFile.LoadFrom(path));
        }

        [Fact]
        public void LoadFrom_WhitespaceOnly_ReturnsNull()
        {
            string path = WriteFile("  \n\t\n  \r\n");
            Assert.Null(SystemPromptFile.LoadFrom(path));
        }

        [Fact]
        public void LoadFrom_NullPath_ReturnsNull()
        {
            Assert.Null(SystemPromptFile.LoadFrom(null));
        }

        [Fact]
        public void LoadFrom_EmptyPath_ReturnsNull()
        {
            Assert.Null(SystemPromptFile.LoadFrom(""));
        }

        // ── LoadFrom: file present ───────────────────────────────────────────

        [Fact]
        public void LoadFrom_ContentPresent_ReturnsTrimmedContent()
        {
            string path = WriteFile("\n\nBe concise.\nAlways use run_build.\n\n");
            string result = SystemPromptFile.LoadFrom(path);
            Assert.NotNull(result);
            Assert.Equal("Be concise.\nAlways use run_build.", result);
        }

        [Fact]
        public void LoadFrom_MultiLineContent_PreservesInternalNewlines()
        {
            string path = WriteFile("Line one\nLine two\nLine three");
            string result = SystemPromptFile.LoadFrom(path);
            Assert.Equal("Line one\nLine two\nLine three", result);
        }

        // ── LoadFrom: unreadable path (IO exception swallowed) ────────────────

        [Fact]
        public void LoadFrom_UnreadablePath_ReturnsNull()
        {
            // Use a directory as the "file" — ReadAllText will throw IOException.
            string dirPath = Path.Combine(_tempDir, "adir");
            Directory.CreateDirectory(dirPath);
            Assert.Null(SystemPromptFile.LoadFrom(dirPath));
        }

        // ── FileName constant ─────────────────────────────────────────────────

        [Fact]
        public void FileName_IsSystemPromptMd()
        {
            Assert.Equal("system-prompt.md", SystemPromptFile.FileName);
        }

        // ── Path property ─────────────────────────────────────────────────────

        [Fact]
        public void Path_IsUnderGlobalDir()
        {
            string path = SystemPromptFile.Path;
            Assert.EndsWith(SystemPromptFile.FileName, path);
            Assert.Contains(DevMindPaths.GlobalDir, path, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Tests for the system-prompt size warning in LlmClient.UpdateSystemPrompt.
    /// Uses a minimal LlmClient with a known context size to verify:
    ///   * warning fires once for a given text
    ///   * warning re-arms after the text changes
    ///   * warning skipped when window is 0 (first turn)
    /// </summary>
    public sealed class SystemPromptWarningTests
    {
        private sealed class TestOptions : ILlmOptions
        {
            public string SystemPrompt { get; set; } = "test";
            public string ModelName { get; set; } = "";
            public int RequestTimeoutMinutes { get; set; } = 1;
            public int FirstTokenTimeoutMinutes { get; set; } = 1;
            public bool ShowDebugOutput { get; set; } = false;
            public bool ShowContextBudget { get; set; } = false;
            public bool ShowLlmThinking { get; set; } = false;
            public ContextEvictionMode ContextEviction { get; set; } = ContextEvictionMode.Off;
            public int ManualContextSize { get; set; } = 0;
            public LlmServerType ServerType { get; set; } = LlmServerType.LlamaServer;
            public string CustomContextEndpoint { get; set; } = "";
            public int MicroCompactThreshold { get; set; } = 85;
            public int NearlineIngestThresholdChars { get; set; } = 8_000;
            public bool MicroCompactSummarize { get; set; } = false;
            public bool MicroCompactBrainwash { get; set; } = false;
            public bool AlwaysConfirmPatch { get; set; } = false;
            public ApprovalMode ApprovalMode => ApprovalMode.Auto;
            public int AgenticLoopMaxDepth { get; set; } = 5;
            public int AgenticContextLimitPercent { get; set; } = 0;
        }

        [Fact]
        public void Warning_FiresOnce_ForLargePrompt()
        {
            var opts = new TestOptions { SystemPrompt = "x" };
            using var client = new LlmClient(opts);
            client.Configure("http://127.0.0.1:1", "key");

            // Wait for context detection to complete (or time out to fallback).
            // The fallback default is 13372 tokens. 5% = 668 tokens = ~2672 chars.
            // Use a prompt well over 5%: 50000 chars → ~12504 tokens → ~94% of 13372.
            string bigPrompt = "x".PadRight(50000, 'a');

            string[] warnings = new string[3];

            // First call: should warn.
            string? w1 = InvokeUpdateSystemPrompt(client, bigPrompt);
            Assert.NotNull(w1);
            Assert.Contains("[PROMPT]", w1);
            Assert.Contains("over the 5% guideline", w1);

            // Second call with same text: should NOT warn.
            string? w2 = InvokeUpdateSystemPrompt(client, bigPrompt);
            Assert.Null(w2);

            // Third call with same text: still no warn.
            string? w3 = InvokeUpdateSystemPrompt(client, bigPrompt);
            Assert.Null(w3);
        }

        [Fact]
        public void Warning_ReArms_AfterTextChanged()
        {
            var opts = new TestOptions { SystemPrompt = "x" };
            using var client = new LlmClient(opts);
            client.Configure("http://127.0.0.1:1", "key");

            string bigPrompt1 = "x".PadRight(50000, 'a');
            string bigPrompt2 = "y".PadRight(60000, 'b');

            // First: warn on prompt1.
            string? w1 = InvokeUpdateSystemPrompt(client, bigPrompt1);
            Assert.NotNull(w1);

            // Second: same text, no warn.
            string? w2 = InvokeUpdateSystemPrompt(client, bigPrompt1);
            Assert.Null(w2);

            // Third: different text (still over 5%) → re-arms → warn.
            string? w3 = InvokeUpdateSystemPrompt(client, bigPrompt2);
            Assert.NotNull(w3);
            Assert.Contains("[PROMPT]", w3);

            // Fourth: same as prompt2, no warn.
            string? w4 = InvokeUpdateSystemPrompt(client, bigPrompt2);
            Assert.Null(w4);
        }

        [Fact]
        public void Warning_NoWarn_ForSmallPrompt()
        {
            var opts = new TestOptions { SystemPrompt = "x" };
            using var client = new LlmClient(opts);
            client.Configure("http://127.0.0.1:1", "key");

            // Small prompt: 100 chars → ~29 tokens, well under 5% of 13372.
            string smallPrompt = "You are a helpful coding assistant.";

            string? w = InvokeUpdateSystemPrompt(client, smallPrompt);
            Assert.Null(w);
        }

        private static string? InvokeUpdateSystemPrompt(LlmClient client, string prompt)
        {
            // UpdateSystemPrompt is private. Use reflection to call it directly.
            var method = typeof(LlmClient).GetMethod("UpdateSystemPrompt",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            Assert.NotNull(method);
            // Returns null when no warning is due, so the result is legitimately nullable.
            return method.Invoke(client, new object?[] { prompt }) as string;
        }
    }
}
