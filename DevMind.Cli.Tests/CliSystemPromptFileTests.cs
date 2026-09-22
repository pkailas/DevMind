// File: CliSystemPromptFileTests.cs  v2.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// The CLI skin must resolve its base system prompt on the same three-tier precedence the
// TUI uses (SystemPromptFile.Resolve):
//
//   * an explicit --system-prompt wins outright — a per-invocation decision;
//   * otherwise the authored global file (%APPDATA%\devmind\system-prompt.md) wins,
//     re-read on every build so an edit takes effect next turn;
//   * otherwise options.SystemPrompt — devmind.json's systemPrompt, else the built-in
//     DefaultPrompts.System.
//
// This file's earlier header claimed the explicit argument "has already been written into
// options.SystemPrompt by the time the prompt is assembled" and therefore won. It did not.
// Both skins resolved `filePrompt ?? options.SystemPrompt`, so the authored file beat the
// typed argument every time it existed, while the help text advertised "Override system
// prompt". The argument now has its own field, because options.SystemPrompt is always
// populated and cannot distinguish a typed prompt from a default.
//
// The fallback-direction tests below did NOT change with that fix and are not asserting the
// old defect: they set SystemPrompt directly on the options object, which is the configured
// tier, and the file is still correct to outrank it.
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

        // The defect this file's header used to describe: with an authored file present, a
        // typed --system-prompt was silently dropped. The argument is the operator deciding
        // about THIS run, so it outranks a file they authored at some earlier point.
        [Fact]
        public void ExplicitArgument_BeatsTheAuthoredFile()
        {
            WriteGlobalPrompt(FileContent);

            var options = NewOptions();
            options.ExplicitSystemPrompt = "EXPLICIT-ARG-MARKER";

            string combined = Program.BuildCombinedSystemPrompt(options, devMindContext: null);

            Assert.Contains("EXPLICIT-ARG-MARKER", combined);
            Assert.DoesNotContain(FileContent, combined);
            Assert.DoesNotContain(HardcodedDefault, combined);
        }

        // A blank argument is not a decision. Treating it as one would let an empty
        // --system-prompt blank the session's prompt, which is a way to break a run by
        // accident rather than a capability.
        [Fact]
        public void WhitespaceOnlyExplicitArgument_LeavesTheFileInCharge()
        {
            WriteGlobalPrompt(FileContent);

            var options = NewOptions();
            options.ExplicitSystemPrompt = "   ";

            Assert.Contains(FileContent, Program.BuildCombinedSystemPrompt(options, devMindContext: null));
        }

        // devmind.json's systemPrompt is standing configuration, not a decision about this
        // run, so it sits BELOW the authored file — unlike the argument, which sits above it.
        // Parsed through FromArgs so the real pass order (dir -> devmind.json -> flags) is
        // what is under test, not a hand-built options object.
        [Fact]
        public void ConfiguredPrompt_LosesToTheAuthoredFile()
        {
            File.WriteAllText(Path.Combine(_workDir, "devmind.json"),
                "{ \"systemPrompt\": \"CONFIGURED-JSON-MARKER\" }");
            WriteGlobalPrompt(FileContent);

            var options = CliOptions.FromArgs(new[] { "--dir", _workDir });
            options.BuildCommand = "dotnet build";

            Assert.Equal("CONFIGURED-JSON-MARKER", options.SystemPrompt);
            Assert.Null(options.ExplicitSystemPrompt);

            string combined = Program.BuildCombinedSystemPrompt(options, devMindContext: null);

            Assert.Contains(FileContent, combined);
            Assert.DoesNotContain("CONFIGURED-JSON-MARKER", combined);
        }

        // ...but the argument still beats it, which is the pass-3-overrides-pass-2 rule
        // surviving the change.
        [Fact]
        public void ExplicitArgument_BeatsBothTheFileAndTheConfiguredPrompt()
        {
            File.WriteAllText(Path.Combine(_workDir, "devmind.json"),
                "{ \"systemPrompt\": \"CONFIGURED-JSON-MARKER\" }");
            WriteGlobalPrompt(FileContent);

            var options = CliOptions.FromArgs(new[] { "--dir", _workDir, "--system-prompt", "EXPLICIT-ARG-MARKER" });
            options.BuildCommand = "dotnet build";

            string combined = Program.BuildCombinedSystemPrompt(options, devMindContext: null);

            Assert.Contains("EXPLICIT-ARG-MARKER", combined);
            Assert.DoesNotContain(FileContent, combined);
            Assert.DoesNotContain("CONFIGURED-JSON-MARKER", combined);
        }
    }
}
