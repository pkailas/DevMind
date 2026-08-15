// File: FilePathResolverTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Unit tests for the shared FilePathResolver (DevMind.Core/FilePathResolver.cs) —
// the single resolution path used by the MCP server, the headless host and the TUI
// skin. The headline regression: a repo-root-relative hint ("QuantEval/ByteSizeParser.cs")
// cited while the working directory is a subdirectory of the repo (live failures,
// job-469 / job-471 transcripts) must resolve via the git-root fallback, and an
// ambiguous same-named file must be REFUSED rather than guessed.

using Xunit;

namespace DevMind.Core.Tests
{
    public class FilePathResolverTests : IDisposable
    {
        private readonly string _repo;
        private readonly string _subdir;

        public FilePathResolverTests()
        {
            // Scratch layout:
            //   _repo/                     (git root — .git marker dir)
            //     .git/
            //     QuantEval/
            //       ByteSizeParser.cs
            //     A/Dup.cs                   ┐ same-named, in DEEPER dirs so the
            //     B/Dup.cs                   ┘ bare-name top-level join never fires
            //     Program.cs                 (top-level — for absolute-path test)
            //     _subdir/
            //       InnerFile.cs
            //       Bin/ByteSizeParser.cs    (noise path — must be ignored)
            _repo = Path.Combine(Path.GetTempPath(), $"devmind_fpr_{Guid.NewGuid():N}");
            _subdir = Path.Combine(_repo, "_subdir");

            Directory.CreateDirectory(Path.Combine(_repo, ".git"));
            Directory.CreateDirectory(Path.Combine(_repo, "QuantEval"));
            Directory.CreateDirectory(Path.Combine(_repo, "A"));
            Directory.CreateDirectory(Path.Combine(_repo, "B"));
            Directory.CreateDirectory(_subdir);
            Directory.CreateDirectory(Path.Combine(_subdir, "Bin"));

            File.WriteAllText(Path.Combine(_repo, "QuantEval", "ByteSizeParser.cs"), "// source");
            File.WriteAllText(Path.Combine(_repo, "A", "Dup.cs"), "// dup A");
            File.WriteAllText(Path.Combine(_repo, "B", "Dup.cs"), "// dup B");
            File.WriteAllText(Path.Combine(_repo, "Program.cs"), "// root program");
            File.WriteAllText(Path.Combine(_subdir, "InnerFile.cs"), "// inner");
            File.WriteAllText(Path.Combine(_subdir, "Bin", "ByteSizeParser.cs"), "// noise");
        }

        public void Dispose()
        {
            try { Directory.Delete(_repo, recursive: true); } catch { }
        }

        // ── The live regression ──────────────────────────────────────────────

        [Fact]
        public void RepoRootRelativeHint_FromSubdirWorkingDir_ResolvesViaGitRoot()
        {
            // wd = <repo>/_subdir, hint = "QuantEval/ByteSizeParser.cs" (repo-root-relative).
            var r = FilePathResolver.Resolve("ByteSizeParser.cs", "QuantEval/ByteSizeParser.cs", _subdir);

            string expected = Path.GetFullPath(Path.Combine(_repo, "QuantEval", "ByteSizeParser.cs"));
            Assert.Equal(expected, r.Path);
        }

        [Fact]
        public void NameWithRealPlusNoiseMatch_OnlyCleanMatchCounts()
        {
            // ByteSizeParser.cs exists at <repo>/QuantEval (real) and <repo>/_subdir/Bin
            // (the noise list contains "bin" case-insensitively). Noise is filtered BEFORE
            // uniqueness is judged, so exactly one clean match resolves.
            var r = FilePathResolver.Resolve("ByteSizeParser.cs", "ByteSizeParser.cs", _repo);

            string expected = Path.GetFullPath(Path.Combine(_repo, "QuantEval", "ByteSizeParser.cs"));
            Assert.Equal(expected, r.Path);
        }

        // ── Ambiguity: refuse to guess ────────────────────────────────────────

        [Fact]
        public void AmbiguousSameNamedFile_NoDirectoryHint_RefusesToGuess()
        {
            // Dup.cs exists at <repo>/A and <repo>/B, wd = repo root, bare hint.
            var r = FilePathResolver.Resolve("Dup.cs", "Dup.cs", _repo);

            Assert.Null(r.Path);
            Assert.Equal(2, r.Candidates.Count);
            Assert.Contains(r.SearchedRoots, d => Path.GetFullPath(d) == Path.GetFullPath(_repo));
        }

        [Fact]
        public void AmbiguousSameNamedFile_HintDirectoryDisambiguates()
        {
            // Same two Dup.cs files, but the hint's directory portion picks one.
            var r = FilePathResolver.Resolve("Dup.cs", "A/Dup.cs", _repo);

            string expected = Path.GetFullPath(Path.Combine(_repo, "A", "Dup.cs"));
            Assert.Equal(expected, r.Path);
        }

        [Fact]
        public void AmbiguousSameNamedFile_HintDirectoryMatchesNothing_Refuses()
        {
            // Directory portion matches no candidate → still ambiguous → refuse.
            var r = FilePathResolver.Resolve("Dup.cs", "QuantEval/Dup.cs", _repo);

            Assert.Null(r.Path);
            Assert.Equal(2, r.Candidates.Count);
        }

        // ── Genuinely absent ─────────────────────────────────────────────────

        [Fact]
        public void GenuinelyAbsentFile_NotFoundWithScope()
        {
            var r = FilePathResolver.Resolve("Nope.cs", "Nope.cs", _subdir);

            Assert.Null(r.Path);
            Assert.Empty(r.Candidates);
            // Scope must cover BOTH the subdirectory wd AND the git root.
            Assert.Equal(2, r.SearchedRoots.Count);
            Assert.Contains(r.SearchedRoots, d => Path.GetFullPath(d) == Path.GetFullPath(_subdir));
            Assert.Contains(r.SearchedRoots, d => Path.GetFullPath(d) == Path.GetFullPath(_repo));
        }

        [Fact]
        public void UniqueMatchUnderWorkingDir_NotRefusedBySameNameInGitRoot()
        {
            // Roots are searched in order: a unique match under the working directory is
            // authoritative — a same-named file elsewhere in the git root must NOT widen
            // the resolution into a refusal.
            File.WriteAllText(Path.Combine(_subdir, "Solo.cs"), "// wd copy");
            File.WriteAllText(Path.Combine(_repo, "QuantEval", "Solo.cs"), "// git-root copy");

            var r = FilePathResolver.Resolve("Solo.cs", "Solo.cs", _subdir);

            Assert.Equal(Path.GetFullPath(Path.Combine(_subdir, "Solo.cs")),
                Path.GetFullPath(r.Path));
        }

        [Fact]
        public void BareNameFoundOnlyInGitRoot_ResolvesViaRecursiveFallback()
        {
            // wd = _subdir, bare name, file exists only in <repo>/QuantEval (outside the wd)
            // → git-root recursive search resolves it (the job-469 recovery path without
            // needing the model to re-issue an absolute path).
            var r = FilePathResolver.Resolve("ByteSizeParser.cs", "ByteSizeParser.cs", _subdir);

            string expected = Path.GetFullPath(Path.Combine(_repo, "QuantEval", "ByteSizeParser.cs"));
            Assert.Equal(expected, Path.GetFullPath(r.Path));
        }

        // ── Not-found message states scope, not a "project" listing ──────────

        [Fact]
        public void NotFoundMessage_AbsentFile_StatesScope_AndSaysAbsent()
        {
            var r = FilePathResolver.Resolve("Nope.cs", "Nope.cs", _subdir);
            string msg = FilePathResolver.BuildFileNotFoundMessage("read_file", "Nope.cs", r);

            Assert.StartsWith("read_file: file not found — Nope.cs", msg);
            Assert.Contains("Searched recursively", msg);
            Assert.Contains(_subdir.Replace('\\', '/'), msg.Replace('\\', '/'));
            Assert.Contains(_repo.Replace('\\', '/'), msg.Replace('\\', '/'));
            Assert.Contains("absent", msg);
            // The old amplifier — top-level files presented as "the project" — is gone.
            Assert.DoesNotContain("Project files", msg);
            Assert.DoesNotContain("C# files in project root", msg);
        }

        [Fact]
        public void NotFoundMessage_AmbiguousFile_ListsCandidates()
        {
            var r = FilePathResolver.Resolve("Dup.cs", "Dup.cs", _repo);
            string msg = FilePathResolver.BuildFileNotFoundMessage("READ", "Dup.cs", r);

            Assert.Contains("exist at:", msg);
            string normalized = msg.Replace('\\', '/');
            Assert.Contains(Path.Combine(_repo, "A", "Dup.cs").Replace('\\', '/'), normalized);
            Assert.Contains(Path.Combine(_repo, "B", "Dup.cs").Replace('\\', '/'), normalized);
            Assert.Contains("did not uniquely identify", msg);
        }

        // ── Baseline resolution paths (no behaviour change) ──────────────────

        [Fact]
        public void AbsoluteExistingPath_ResolvesDirectly()
        {
            string abs = Path.Combine(_repo, "Program.cs");
            var r = FilePathResolver.Resolve("Program.cs", abs, _repo);
            Assert.Equal(Path.GetFullPath(abs), Path.GetFullPath(r.Path));
        }

        [Fact]
        public void HintRelativeToWorkingDir_Resolves()
        {
            // Hint relative to the wd (not the git root) — resolves before any git-root logic.
            var r = FilePathResolver.Resolve("InnerFile.cs", "InnerFile.cs", _subdir);
            Assert.Equal(Path.GetFullPath(Path.Combine(_subdir, "InnerFile.cs")),
                Path.GetFullPath(r.Path));
        }

        [Fact]
        public void UniqueBasenameUnderWorkingDir_Resolves()
        {
            var r = FilePathResolver.Resolve("InnerFile.cs", "InnerFile.cs", _subdir);
            Assert.NotNull(r.Path);
            Assert.EndsWith("InnerFile.cs", r.Path);
        }

        [Fact]
        public void BackslashHint_WithLeadingDotSlash_Resolves()
        {
            // "./QuantEval/ByteSizeParser.cs" with backslashes, from the subdir wd.
            var r = FilePathResolver.Resolve("ByteSizeParser.cs",
                "./QuantEval\\ByteSizeParser.cs", _subdir);
            string expected = Path.GetFullPath(Path.Combine(_repo, "QuantEval", "ByteSizeParser.cs"));
            Assert.Equal(expected, Path.GetFullPath(r.Path));
        }

        [Fact]
        public void NonGitWorkingDir_WdOnlyScope()
        {
            // No .git anywhere above — git-root fallback must be a no-op.
            string plain = Path.Combine(Path.GetTempPath(), $"devmind_fpr_nogit_{Guid.NewGuid():N}");
            try
            {
                Directory.CreateDirectory(plain);
                File.WriteAllText(Path.Combine(plain, "Only.cs"), "x");

                var r = FilePathResolver.Resolve("Only.cs", "Only.cs", plain);
                Assert.Equal(Path.GetFullPath(Path.Combine(plain, "Only.cs")),
                    Path.GetFullPath(r.Path));
                Assert.Single(r.SearchedRoots);
            }
            finally
            {
                try { Directory.Delete(plain, recursive: true); } catch { }
            }
        }
    }
}
