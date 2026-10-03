// File: PromptProfilesTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// PromptProfiles: named .md files under %APPDATA%\devmind\prompts\, loaded
// through SystemPromptFile.LoadFrom.
//
// What these pin:
//   * IsValidName is the security boundary — a profile name becomes a file name
//     inside one folder, and nothing in it may be reinterpreted by the
//     filesystem ("..", "\", "/", ":") or the shell.
//   * Listing and loading are total functions: absent folder, absent file,
//     empty file and IO errors all return empty/null, never throw. The prompt
//     chain treats "no profile" as a normal state and falls back.
//   * The ...In(dir) overloads are the hermetic test seams; nothing here can
//     touch the operator's real %APPDATA%.

using System;
using System.IO;
using DevMind;
using Xunit;

namespace DevMind.Core.Tests
{
    public sealed class PromptProfilesTests : IDisposable
    {
        private readonly string _tempDir;   // stands in for the profiles folder
        private readonly string _globalDir; // stands in for %APPDATA%\devmind
        private readonly string? _priorGlobalDir;

        public PromptProfilesTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "prompt-profiles-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);

            // DevMindPaths' documented test seam, so ProfilesDir() assertions are
            // about the class, not about this machine.
            _globalDir = Path.Combine(Path.GetTempPath(), "prompt-profiles-global-" + Guid.NewGuid().ToString("N"));
            _priorGlobalDir = Environment.GetEnvironmentVariable("DEVMIND_GLOBAL_DIR");
            Environment.SetEnvironmentVariable("DEVMIND_GLOBAL_DIR", _globalDir);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("DEVMIND_GLOBAL_DIR", _priorGlobalDir);
            try { Directory.Delete(_tempDir, recursive: true); } catch { }
            try { Directory.Delete(_globalDir, recursive: true); } catch { }
        }

        private void Write(string name, string content)
            => File.WriteAllText(Path.Combine(_tempDir, name + PromptProfiles.Extension), content);

        // ── IsValidName: the traversal boundary ───────────────────────────────

        [Theory]
        [InlineData("a")]
        [InlineData("A")]
        [InlineData("rigorous")]
        [InlineData("Rigorous-H3")]
        [InlineData("my_prompt")]
        [InlineData("123")]
        [InlineData("default")] // valid as a name; RESERVED by the TUI command, not by the validator
        public void ValidNames_AreLettersDigitsDashAndUnderscore(string name)
        {
            Assert.True(PromptProfiles.IsValidName(name));
        }

        [Theory]
        // Traversal and separator attempts — the whole point of the validator.
        [InlineData("..")]
        [InlineData("...")]
        [InlineData("../evil")]
        [InlineData("..\\evil")]
        [InlineData("..\\..\\windows\\system32\\evil")]
        [InlineData("sub/evil")]
        [InlineData("sub\\evil")]
        // Drive- and stream-qualified paths (Windows alternate data streams).
        [InlineData("C:evil")]
        [InlineData("evil.txt:secret")]
        // Everything else that cannot be a safe single file name.
        [InlineData(null)]
        [InlineData("")]
        [InlineData(" ")]
        [InlineData("a b")]
        [InlineData("a.b")]      // an extension is not part of the name
        [InlineData("a*b")]
        [InlineData("a$b")]
        [InlineData("a|b")]
        [InlineData("a?b")]
        [InlineData("pör")]      // non-ASCII rejected deliberately: encoding-safe listing
        public void InvalidNames_EverythingElse(string? name)
        {
            Assert.False(PromptProfiles.IsValidName(name!));
        }

        // The validator is checked BEFORE any path is built: an invalid name must
        // return null without ever consulting the filesystem, so a hostile name
        // cannot even be probed. Verified by pointing at a file that DOES exist and
        // reaching it through an invalid spelling.
        [Fact]
        public void LoadProfileIn_AnInvalidName_NeverTouchesTheFilesystem()
        {
            Write("evil", "SHOULD NEVER BE LOADED");

            // "evil" exists at <dir>\evil.md; "sub\..\evil" resolves to the same file
            // on Windows if a path is ever built from it. It must return null instead.
            Assert.Null(PromptProfiles.LoadProfileIn(_tempDir, "sub\\..\\evil"));
            Assert.Null(PromptProfiles.LoadProfileIn(_tempDir, "evil.md"));
            Assert.Null(PromptProfiles.LoadProfileIn(_tempDir, null));
            Assert.Null(PromptProfiles.LoadProfileIn(_tempDir, ""));
        }

        // ── ProfilesDir: derived, never created ───────────────────────────────

        [Fact]
        public void ProfilesDir_FollowsTheGlobalDirSeam_AndIsNotCreated()
        {
            Assert.Equal(Path.Combine(_globalDir, "prompts"), PromptProfiles.ProfilesDir);
            Assert.False(Directory.Exists(PromptProfiles.ProfilesDir),
                "Reading the profile locations must not create them — absence is a normal state.");
        }

        // ── ListProfilesIn: total, sorted, extension-filtered ─────────────────

        [Fact]
        public void ListProfilesIn_AFolderThatDoesNotExist_IsEmpty()
        {
            string missing = Path.Combine(_tempDir, "no-such-folder");
            Assert.Empty(PromptProfiles.ListProfilesIn(missing));
        }

        [Fact]
        public void ListProfilesIn_AnEmptyFolder_IsEmpty()
        {
            Assert.Empty(PromptProfiles.ListProfilesIn(_tempDir));
        }

        [Fact]
        public void ListProfilesIn_NamesAreSortedCaseInsensitively()
        {
            Write("zulu", "z");
            Write("Bravo", "b");
            Write("alpha", "a");

            Assert.Equal(new[] { "alpha", "Bravo", "zulu" }, PromptProfiles.ListProfilesIn(_tempDir));
        }

        [Fact]
        public void ListProfilesIn_OnlyMdFilesWithSelectableNames()
        {
            Write("keep", "k");
            File.WriteAllText(Path.Combine(_tempDir, "notmd.txt"), "x");
            File.WriteAllText(Path.Combine(_tempDir, "weird name.md"), "x"); // unselectable name

            Assert.Equal(new[] { "keep" }, PromptProfiles.ListProfilesIn(_tempDir));
        }

        // The listing and the setter must agree: a name listed can be loaded.
        [Fact]
        public void ListProfilesIn_EveryListedNameIsLoadable()
        {
            Write("keep", "content");
            foreach (string name in PromptProfiles.ListProfilesIn(_tempDir))
                Assert.NotNull(PromptProfiles.LoadProfileIn(_tempDir, name));
        }

        // ── LoadProfileIn: SystemPromptFile semantics on the profile path ─────

        [Fact]
        public void LoadProfileIn_AContentsIsReturnedTrimmed()
        {
            Write("rigorous", "\n  PROFILE-TEXT\ninner   spaced   line\n  ");

            Assert.Equal("PROFILE-TEXT\ninner   spaced   line", PromptProfiles.LoadProfileIn(_tempDir, "rigorous"));
        }

        [Fact]
        public void LoadProfileIn_AMissingFile_IsNull()
        {
            Assert.Null(PromptProfiles.LoadProfileIn(_tempDir, "absent"));
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("\n\t \n")]
        public void LoadProfileIn_EmptyOrWhitespace_IsNull(string content)
        {
            Write("blank", content);
            Assert.Null(PromptProfiles.LoadProfileIn(_tempDir, "blank"));
        }

        // The default-path pair of each seam, exercised through DEVMIND_GLOBAL_DIR
        // so the operator's real %APPDATA% is never read or written.
        [Fact]
        public void TheDefaultOverloads_ReadThroughGlobalDir_NotTheOperatorsFolder()
        {
            string dir = PromptProfiles.ProfilesDir;
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "via-default.md"), "DEFAULT-PATH-PROFILE");

            Assert.Contains("via-default", PromptProfiles.ListProfiles());
            Assert.Equal("DEFAULT-PATH-PROFILE", PromptProfiles.LoadProfile("via-default"));
        }

        [Fact]
        public void TheDefaultOverloads_WhenNoFolderExists_AreEmptyAndNull()
        {
            Assert.Empty(PromptProfiles.ListProfiles());
            Assert.Null(PromptProfiles.LoadProfile("anything"));
        }
    }
}
