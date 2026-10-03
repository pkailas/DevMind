// File: PromptProfilePrecedenceTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// The base-prompt chain when a saved profile is active. Extracted from
// BuildCombinedSystemPrompt as Program.ResolveBaseSystemPrompt precisely so this
// ordering is testable — the same seam argument PlanModePromptNoteTests makes for
// the plan-mode note.
//
// Precedence, top to bottom:
//   active /prompt profile   (the most RECENT explicit operator choice)
//   --system-prompt          (explicit for this invocation)
//   system-prompt.md         (authored, hot-reloaded)
//   options.SystemPrompt     (devmind.json, else the built-in default)
//
// The profile deliberately BEATS --system-prompt: an operator who starts with
// --system-prompt X and then types /prompt Y made the later choice, and the later
// choice is the live one. SystemPromptFile.Resolve itself is unchanged and still
// owns the bottom three tiers (SystemPromptPrecedenceTests keeps pinning those).
//
// Hot-reload in the other direction too: a profile file deleted or blanked
// mid-session must fall back to the chain on the next assembly, not blank the
// prompt and not crash it.
//
// Hermetic via DEVMIND_GLOBAL_DIR — the operator's real %APPDATA%\devmind is
// never read.

using System;
using System.IO;
using Xunit;

namespace DevMind.TUI.Tests
{
    public sealed class PromptProfilePrecedenceTests : IDisposable
    {
        private const string ProfileText   = "PROFILE-PROMPT";
        private const string ExplicitText  = "EXPLICIT-ARG-PROMPT";
        private const string AuthoredText  = "AUTHORED-FILE-PROMPT";
        private const string FallbackText  = "CONFIGURED-OR-DEFAULT-PROMPT";

        private readonly string _globalDir;
        private readonly string? _priorGlobalDir;

        public PromptProfilePrecedenceTests()
        {
            _globalDir = Path.Combine(Path.GetTempPath(), $"devmind_prompt_prec_{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path.Combine(_globalDir, "prompts"));
            _priorGlobalDir = Environment.GetEnvironmentVariable("DEVMIND_GLOBAL_DIR");
            Environment.SetEnvironmentVariable("DEVMIND_GLOBAL_DIR", _globalDir);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("DEVMIND_GLOBAL_DIR", _priorGlobalDir);
            try { Directory.Delete(_globalDir, recursive: true); } catch { }
        }

        private void WriteProfile(string content)
            => File.WriteAllText(Path.Combine(_globalDir, "prompts", "rigorous.md"), content);

        private void WriteAuthoredFile(string content)
            => File.WriteAllText(Path.Combine(_globalDir, "system-prompt.md"), content);

        private static TuiOptions Options(string explicitPrompt = null!)
        {
            var o = new TuiOptions { SystemPrompt = FallbackText };
            if (explicitPrompt != null) o.ExplicitSystemPrompt = explicitPrompt;
            return o;
        }

        // ── The profile beats everything ──────────────────────────────────────

        [Fact]
        public void AnActiveProfile_BeatsAnExplicitSystemPromptArgument()
        {
            WriteProfile(ProfileText);
            WriteAuthoredFile(AuthoredText);

            var o = Options(ExplicitText);
            o.ActivePromptProfile = "rigorous";

            Assert.Equal(ProfileText, Program.ResolveBaseSystemPrompt(o));
        }

        [Fact]
        public void AnActiveProfile_BeatsTheAuthoredSystemPromptFile()
        {
            WriteProfile(ProfileText);
            WriteAuthoredFile(AuthoredText);

            var o = Options();
            o.ActivePromptProfile = "rigorous";

            Assert.Equal(ProfileText, Program.ResolveBaseSystemPrompt(o));
        }

        // The profile is read on EVERY assembly — the hot-reload property the file
        // already has extends to profiles for free.
        [Fact]
        public void AnActiveProfile_IsReReadEveryAssembly()
        {
            WriteProfile("PROFILE-V1");
            var o = Options();
            o.ActivePromptProfile = "rigorous";

            Assert.Equal("PROFILE-V1", Program.ResolveBaseSystemPrompt(o));

            File.WriteAllText(Path.Combine(_globalDir, "prompts", "rigorous.md"), "PROFILE-V2");
            Assert.Equal("PROFILE-V2", Program.ResolveBaseSystemPrompt(o));
        }

        // ── A broken profile falls back; it never blanks or throws ────────────

        [Fact]
        public void AProfileFileDeletedMidSession_FallsBackToTheChain()
        {
            WriteProfile(ProfileText);
            WriteAuthoredFile(AuthoredText);

            var o = Options();
            o.ActivePromptProfile = "rigorous";
            Assert.Equal(ProfileText, Program.ResolveBaseSystemPrompt(o));

            File.Delete(Path.Combine(_globalDir, "prompts", "rigorous.md"));
            Assert.Equal(AuthoredText, Program.ResolveBaseSystemPrompt(o));
        }

        [Fact]
        public void AProfileFileBlankedMidSession_FallsBackToTheChain()
        {
            WriteProfile(ProfileText);

            var o = Options(ExplicitText);
            o.ActivePromptProfile = "rigorous";
            Assert.Equal(ProfileText, Program.ResolveBaseSystemPrompt(o));

            File.WriteAllText(Path.Combine(_globalDir, "prompts", "rigorous.md"), " \n ");
            Assert.Equal(ExplicitText, Program.ResolveBaseSystemPrompt(o));
        }

        // A name that never had a file (state from a --prompt typo, hypothetically):
        // same fallback, no exception out of prompt assembly.
        [Fact]
        public void AProfileThatDoesNotExistAtAll_FallsBackToTheChain()
        {
            WriteAuthoredFile(AuthoredText);

            var o = Options();
            o.ActivePromptProfile = "ghost";

            Assert.Equal(AuthoredText, Program.ResolveBaseSystemPrompt(o));
        }

        // ── No profile: the chain is byte-for-byte what it was ────────────────

        [Fact]
        public void NoActiveProfile_TheExplicitArgumentStillBeatsTheFile()
        {
            WriteAuthoredFile(AuthoredText);

            Assert.Equal(ExplicitText, Program.ResolveBaseSystemPrompt(Options(ExplicitText)));
        }

        [Fact]
        public void NoActiveProfile_NoArgument_TheFileStillBeatsTheFallback()
        {
            WriteAuthoredFile(AuthoredText);

            Assert.Equal(AuthoredText, Program.ResolveBaseSystemPrompt(Options()));
        }

        [Fact]
        public void NothingAtAll_TheConfiguredFallbackWins()
        {
            Assert.Equal(FallbackText, Program.ResolveBaseSystemPrompt(Options()));
        }
    }
}
