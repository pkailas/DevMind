// File: PromptProfileSwitchTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// /prompt <name>, /prompt default — the session's system-prompt switch.
//
// Exercised through the REAL dispatcher with a hand-built CommandContext (the
// PromptCommandWindowTests pattern): the handler's contract is what Dispatch
// delivers, not what PromptHandler's body looks like.
//
// The invariants these pin:
//   * Setting a profile writes through ctx.SetActivePromptProfile — session state
//     only. Nothing here has a persistence path: the setter Program wires assigns
//     TuiOptions.ActivePromptProfile and never touches TuiConfig/Save.
//   * Failure leaves the state UNCHANGED and says why, naming the profiles that
//     DO exist. A typo at the prompt must not silently change what the model is
//     told.
//   * Names resolve case-insensitively to the on-disk spelling.
//
// DEVMIND_GLOBAL_DIR (DevMindPaths' documented seam) points the profiles folder
// at a temp directory, so the operator's real %APPDATA%\devmind\prompts is never
// read or written.

using System;
using System.IO;
using Xunit;

namespace DevMind.TUI.Tests
{
    public sealed class PromptProfileSwitchTests : IDisposable
    {
        private readonly string _globalDir;
        private readonly string? _priorGlobalDir;

        public PromptProfileSwitchTests()
        {
            _globalDir = Path.Combine(Path.GetTempPath(), $"devmind_prompt_switch_{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path.Combine(_globalDir, "prompts"));
            _priorGlobalDir = Environment.GetEnvironmentVariable("DEVMIND_GLOBAL_DIR");
            Environment.SetEnvironmentVariable("DEVMIND_GLOBAL_DIR", _globalDir);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("DEVMIND_GLOBAL_DIR", _priorGlobalDir);
            try { Directory.Delete(_globalDir, recursive: true); } catch { }
        }

        private void WriteProfile(string name, string content = "PROFILE TEXT")
            => File.WriteAllText(Path.Combine(_globalDir, "prompts", name + ".md"), content);

        // The session state a TUI run keeps: a plain field the setter writes, mirroring
        // the wiring Program does (options.ActivePromptProfile = name). Rebuilds counted
        // so a test can see the handler pushed the rebuild, not just the field.
        private static (CommandContext ctx, Func<string> state) MakeCtx()
        {
            string current = null!;
            int rebuilds = 0;
            var ctx = new CommandContext
            {
                ActivePromptProfile = null,
                SetActivePromptProfile = name => current = name,
                RebuildSystemPrompt = () => { rebuilds++; return ""; },
            };
            return (ctx, () => current);
        }

        // ── Selecting a saved profile ─────────────────────────────────────────

        [Fact]
        public async Task PromptWithAName_SelectsTheProfile_AndSaysItIsSessionOnly()
        {
            WriteProfile("rigorous");
            var (ctx, state) = MakeCtx();

            var result = await SlashCommand.Dispatch("/prompt rigorous", ctx);

            Assert.False(result.IsError, result.Message);
            Assert.Equal("rigorous", state()); // the setter is the write path
            Assert.Contains("System prompt: rigorous", result.Message, StringComparison.Ordinal);
            Assert.Contains("session only", result.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("history kept", result.Message, StringComparison.OrdinalIgnoreCase);
        }

        // The prompt is rebuilt per turn from options, but the handler still asks for a
        // rebuild — pinning that the switch takes visible effect immediately rather than
        // relying on the next assembly being remembered by hand.
        [Fact]
        public async Task PromptWithAName_TriggersARebuild()
        {
            WriteProfile("rigorous");
            int rebuilds = 0;
            var ctx = new CommandContext
            {
                SetActivePromptProfile = _ => { },
                RebuildSystemPrompt = () => { rebuilds++; return ""; },
            };

            await SlashCommand.Dispatch("/prompt rigorous", ctx);

            Assert.Equal(1, rebuilds);
        }

        // Case-insensitive matching, canonical spelling stored: the state and the
        // listing must show the file's name, not whatever the operator typed.
        [Fact]
        public async Task PromptName_ResolvesCaseInsensitively_ToTheOnDiskSpelling()
        {
            WriteProfile("Rigorous-H3");
            var (ctx, state) = MakeCtx();

            var result = await SlashCommand.Dispatch("/prompt rigorous-h3", ctx);

            Assert.False(result.IsError, result.Message);
            Assert.Equal("Rigorous-H3", state());
        }

        // ── Clearing ──────────────────────────────────────────────────────────

        [Fact]
        public async Task PromptDefault_ClearsTheProfile()
        {
            WriteProfile("rigorous");
            var (ctx, state) = MakeCtx();

            await SlashCommand.Dispatch("/prompt rigorous", ctx);
            var result = await SlashCommand.Dispatch("/prompt default", ctx);

            Assert.False(result.IsError, result.Message);
            Assert.Null(state());
            Assert.Contains("cleared", result.Message, StringComparison.OrdinalIgnoreCase);
        }

        // "default" is reserved: its word is the clear-word, so default.md can never be
        // selected — and the state output says so when the file exists, rather than
        // leaving the operator to wonder why it never appears as active.
        [Fact]
        public async Task ADefaultProfileFile_CannotBeSelected_AndTheListingSaysSo()
        {
            WriteProfile("default", "SHOULD NOT BE SELECTABLE");
            var (ctx, state) = MakeCtx();

            var attempted = await SlashCommand.Dispatch("/prompt default", ctx);
            Assert.False(attempted.IsError, attempted.Message); // it is the clear-word
            Assert.Null(state());

            var listing = await SlashCommand.Dispatch("/prompt", ctx);
            Assert.Contains("default.md is ignored", listing.Message, StringComparison.Ordinal);
            Assert.Contains("reserved", listing.Message, StringComparison.OrdinalIgnoreCase);
        }

        // ── Failure: error + state unchanged ──────────────────────────────────

        [Fact]
        public async Task AnUnknownName_Errors_ListsTheSavedNames_AndLeavesStateUnchanged()
        {
            WriteProfile("rigorous");
            WriteProfile("terse");
            var (ctx, state) = MakeCtx();
            await SlashCommand.Dispatch("/prompt rigorous", ctx);

            var result = await SlashCommand.Dispatch("/prompt nonsense", ctx);

            Assert.True(result.IsError);
            Assert.Equal("rigorous", state()); // unchanged
            Assert.Contains("nonsense", result.Message, StringComparison.Ordinal);
            Assert.Contains("rigorous", result.Message, StringComparison.Ordinal);
            Assert.Contains("terse", result.Message, StringComparison.Ordinal);
        }

        [Theory]
        [InlineData("..")]
        [InlineData("..\\evil")]
        [InlineData("sub/evil")]
        [InlineData("C:evil")]
        public async Task AnInvalidName_Errors_LeavesStateUnchanged_AndNeverTouchesTheFolder(string name)
        {
            WriteProfile("rigorous");
            var (ctx, state) = MakeCtx();
            await SlashCommand.Dispatch("/prompt rigorous", ctx);

            var result = await SlashCommand.Dispatch("/prompt " + name, ctx);

            Assert.True(result.IsError);
            Assert.Equal("rigorous", state());
            Assert.Contains("Invalid profile name", result.Message, StringComparison.Ordinal);
        }

        // Dispatch splits on whitespace (ParseInput), so a name with a space arrives as
        // only its first token — "bad name" reaches the handler as "bad", a VALID name
        // with no such file. The pinned behaviour is the unknown-name error, not a
        // validator error: state stays unchanged and the listing names what exists.
        [Fact]
        public async Task ANameWithASpace_SplitsAtTheSpace_AndErrorsAsUnknown_LeavingStateUnchanged()
        {
            WriteProfile("rigorous");
            var (ctx, state) = MakeCtx();
            await SlashCommand.Dispatch("/prompt rigorous", ctx);

            var result = await SlashCommand.Dispatch("/prompt bad name", ctx);

            Assert.True(result.IsError);
            Assert.Equal("rigorous", state());
            Assert.Contains("No saved prompt \"bad\"", result.Message, StringComparison.Ordinal);
        }

        // A file that exists but is empty: switching to an empty system prompt is not a
        // capability anyone asked for — refuse, and name the file to fill in.
        [Fact]
        public async Task AnEmptyProfileFile_Errors_AndLeavesStateUnchanged()
        {
            WriteProfile("blank", "   \n  ");
            var (ctx, state) = MakeCtx();

            var result = await SlashCommand.Dispatch("/prompt blank", ctx);

            Assert.True(result.IsError);
            Assert.Null(state());
            Assert.Contains("empty", result.Message, StringComparison.OrdinalIgnoreCase);
        }

        // ── The argless state view ────────────────────────────────────────────

        [Fact]
        public async Task Argless_ShowsThePromptsFolder_TheActiveProfile_AndMarksItInTheListing()
        {
            WriteProfile("rigorous");
            WriteProfile("terse");
            var (ctx, _) = MakeCtx();
            await SlashCommand.Dispatch("/prompt rigorous", ctx);
            ctx.ActivePromptProfile = "rigorous"; // what Program's wiring makes true between dispatches

            var listing = await SlashCommand.Dispatch("/prompt", ctx);

            Assert.False(listing.IsError, listing.Message);
            Assert.Contains(Path.Combine(_globalDir, "prompts"), listing.Message, StringComparison.Ordinal);
            Assert.Contains("Active prompt profile: rigorous", listing.Message, StringComparison.Ordinal);
            Assert.Contains("[rigorous]", listing.Message, StringComparison.Ordinal); // marked
            Assert.Contains("terse", listing.Message, StringComparison.Ordinal);       // listed, unmarked
            // The authoring guidance is kept — the argless view still teaches.
            Assert.Contains("Guidance for authoring the file", listing.Message, StringComparison.Ordinal);
        }

        [Fact]
        public async Task Argless_WithNoProfile_SaysNone()
        {
            var (ctx, _) = MakeCtx();

            var listing = await SlashCommand.Dispatch("/prompt", ctx);

            Assert.False(listing.IsError, listing.Message);
            Assert.Contains("Active prompt profile: none", listing.Message, StringComparison.Ordinal);
        }

        // A profile deleted mid-session: the next turn silently falls back to the
        // default chain (precedence), so the state view has to say so out loud.
        [Fact]
        public async Task Argless_WhenTheActiveProfileFileVanished_SaysTheFallback()
        {
            WriteProfile("rigorous");
            var (ctx, _) = MakeCtx();
            await SlashCommand.Dispatch("/prompt rigorous", ctx);
            ctx.ActivePromptProfile = "rigorous";
            File.Delete(Path.Combine(_globalDir, "prompts", "rigorous.md"));

            var listing = await SlashCommand.Dispatch("/prompt", ctx);

            Assert.False(listing.IsError, listing.Message);
            Assert.Contains("rigorous (file missing, using default chain)", listing.Message, StringComparison.Ordinal);
        }

        // ── Registration surface ──────────────────────────────────────────────

        [Fact]
        public void TheUsageAndDescription_TeachTheNewForm()
        {
            var cmd = Assert.Single(SlashCommand.ListCommands(), c => c.Name == "/prompt");

            Assert.Equal("/prompt [name|default]", cmd.Usage);
            Assert.Contains("saved prompt profile", cmd.Description, StringComparison.OrdinalIgnoreCase);
        }
    }
}
