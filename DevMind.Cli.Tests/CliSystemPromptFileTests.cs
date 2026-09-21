// File: CliSystemPromptFileTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// The CLI skin must honour the global system-prompt file
// (%APPDATA%\devmind\system-prompt.md) under exactly the same rule the TUI
// (DevMind.TUI/Program.cs) and the headless agent (DevMind.Core/HeadlessAgent.cs)
// already use:
//
//   * the file REPLACES options.SystemPrompt when it exists and is non-empty;
//   * absence / emptiness / whitespace-only falls back to options.SystemPrompt
//     unchanged;
//   * an explicit --system-prompt still wins, because it has already been written
//     into options.SystemPrompt by the time the prompt is assembled — so "wins"
//     here means the file must NOT be allowed to override a caller-supplied value
//     silently... which is why these tests pin the fallback direction too.
//
// The CLI previously went straight to options.SystemPrompt, so an operator who
// authored a global prompt got it in the TUI and in delegated jobs and silently
// did not get it in the CLI.
//
// DEVMIND_GLOBAL_DIR is DevMindPaths' documented test seam: it redirects
// SystemPromptFile.Path at a hermetic temp directory so these assertions never
// depend on the operator's real %APPDATA%.

using DevMind;
using Xunit;

namespace DevMind.Cli.Tests
{
    public sealed class CliSystemPromptFileTests : IDisposable
    {
        private const string HardcodedDefault = "HARDCODED-DEFAULT-PROMPT-MARKER";
        private const string FileContent      = "FILE-AUTHORED-PROMPT-MARKER";

        private readonly string _globalDir;
        private readonly string _workDir;
        private readonly string? _priorGlobalDir;

        public CliSystemPromptFileTests()
        {
            string root = Path.Combine(Path.GetTempPath(), "devmind-cli-sp-" + Guid.NewGuid().ToString("N"));
            _globalDir = Path.Combine(root, "global");
            _workDir   = Path.Combine(root, "work");
            Directory.CreateDirectory(_globalDir);
            Directory.CreateDirectory(_workDir);

            _priorGlobalDir = Environment.GetEnvironmentVariable("DEVMIND_GLOBAL_DIR");
            Environment.SetEnvironmentVariable("DEVMIND_GLOBAL_DIR", _globalDir);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("DEVMIND_GLOBAL_DIR", _priorGlobalDir);
            try { Directory.Delete(Directory.GetParent(_globalDir)!.FullName, recursive: true); } catch { }
        }

        private void WriteGlobalPrompt(string content)
            => File.WriteAllText(Path.Combine(_globalDir, SystemPromptFile.FileName), content);

        private CliOptions NewOptions() => new CliOptions
        {
            WorkingDirectory = _workDir,
            SystemPrompt     = HardcodedDefault,
            // Pinned so BuildCombinedSystemPrompt does not probe the temp directory
            // for a build command — irrelevant to the behaviour under test.
            BuildCommand     = "dotnet build",
        };

        [Fact]
        public void GlobalSystemPromptFile_ReplacesTheHardcodedPrompt()
        {
            WriteGlobalPrompt(FileContent);

            string combined = Program.BuildCombinedSystemPrompt(NewOptions(), devMindContext: null);

            Assert.Contains(FileContent, combined);
            Assert.DoesNotContain(HardcodedDefault, combined);
        }

        [Fact]
        public void GlobalSystemPromptFile_Absent_FallsBackToOptionsPrompt()
        {
            // No file written at all.
            string combined = Program.BuildCombinedSystemPrompt(NewOptions(), devMindContext: null);

            Assert.Contains(HardcodedDefault, combined);
        }

        [Fact]
        public void GlobalSystemPromptFile_WhitespaceOnly_FallsBackToOptionsPrompt()
        {
            WriteGlobalPrompt("   \r\n\t  \r\n");

            string combined = Program.BuildCombinedSystemPrompt(NewOptions(), devMindContext: null);

            Assert.Contains(HardcodedDefault, combined);
        }

        // The prompt is re-read on every build, not cached at startup: the CLI calls
        // BuildCombinedSystemPrompt once per turn through a closure, so editing the
        // file mid-session must take effect on the next turn (hot-reload), matching
        // SystemPromptFile's documented "read on EVERY call" design.
        [Fact]
        public void GlobalSystemPromptFile_IsRereadOnEachBuild()
        {
            var options = NewOptions();

            WriteGlobalPrompt(FileContent);
            Assert.Contains(FileContent, Program.BuildCombinedSystemPrompt(options, devMindContext: null));

            WriteGlobalPrompt("SECOND-REVISION-MARKER");
            string second = Program.BuildCombinedSystemPrompt(options, devMindContext: null);

            Assert.Contains("SECOND-REVISION-MARKER", second);
            Assert.DoesNotContain(FileContent, second);
        }
    }
}
