// File: FilePathResolverFixTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Tests for the three FilePathResolver fixes:
//   1. The Candidates.Count == 1 message must not claim a directory portion
//      failed to match when the hint carried no directory portion.
//   2. Building the not-found message from a caller's FileResolution must NOT
//      run a second Resolve (verified by ResolveCallCount, not message text —
//      the redundant second resolve re-runs git-root discovery plus the
//      AllDirectories scan, which a message assertion cannot see).
//   3. The re-resolving fallback overload must tolerate empty and
//      invalid-character filenames (Path.GetFileName throws on both) and
//      return a clean "not found" message instead of throwing — matching the
//      behaviour of the first-resolve helpers (IsNullOrWhiteSpace guard /
//      SafeGetFileName), so the two calls can no longer diverge by throwing.

using Xunit;

namespace DevMind.Core.Tests
{
    public class FilePathResolverFixTests : IDisposable
    {
        private readonly string _repo;

        public FilePathResolverFixTests()
        {
            // Scratch git repo:
            //   _repo/.git/
            //   _repo/QuantEval/ByteSizeParser.cs
            _repo = Path.Combine(Path.GetTempPath(), $"devmind_fprfix_{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path.Combine(_repo, ".git"));
            Directory.CreateDirectory(Path.Combine(_repo, "QuantEval"));
            File.WriteAllText(Path.Combine(_repo, "QuantEval", "ByteSizeParser.cs"), "// source");
        }

        public void Dispose()
        {
            try { Directory.Delete(_repo, recursive: true); } catch { }
        }

        private static int Baseline() => FilePathResolver.ResolveCallCount;

        // ── Fix 1: the Count == 1 message ────────────────────────────────────

        [Fact]
        public void UniqueCandidate_BareBasenameHint_DoesNotClaimDirectoryPortionFailed()
        {
            // A single candidate exists (QuantEval/ByteSizeParser.cs) and the hint is
            // the bare basename — there was no directory portion to match. The old
            // wording claimed "The directory portion of the hint did not match it".
            var resolution = new FileResolution(
                null,
                new[] { Path.Combine(_repo, "QuantEval", "ByteSizeParser.cs") },
                new[] { _repo });
            string msg = FilePathResolver.BuildFileNotFoundMessage("read_file", "ByteSizeParser.cs", resolution);

            Assert.Contains("exists at:", msg);
            Assert.DoesNotContain("directory portion", msg);
            Assert.Contains("use that path", msg);
        }

        [Fact]
        public void UniqueCandidate_HintWithDirectoryPortion_KeepsDirectoryPortionWording()
        {
            // Same single candidate, but the hint carries a directory portion that
            // matched nothing — the existing wording must be preserved.
            var resolution = new FileResolution(
                null,
                new[] { Path.Combine(_repo, "QuantEval", "ByteSizeParser.cs") },
                new[] { _repo });
            string msg = FilePathResolver.BuildFileNotFoundMessage("read_file", "WrongDir/ByteSizeParser.cs", resolution);

            Assert.Contains("exists at:", msg);
            Assert.Contains("The directory portion of the hint did not match it", msg);
            Assert.DoesNotContain("use that path", msg);
        }

        [Fact]
        public void UniqueCandidate_BackslashDirectoryHint_KeepsDirectoryPortionWording()
        {
            // Windows-style hint with a directory portion — same expected wording.
            var resolution = new FileResolution(
                null,
                new[] { Path.Combine(_repo, "QuantEval", "ByteSizeParser.cs") },
                new[] { _repo });
            string msg = FilePathResolver.BuildFileNotFoundMessage("read_file", "WrongDir\\ByteSizeParser.cs", resolution);

            Assert.Contains("The directory portion of the hint did not match it", msg);
            Assert.DoesNotContain("use that path", msg);
        }

        // ── Fix 2: no second resolve when the message is built from the first resolution ──

        [Fact]
        public void BuildMessageFromFirstResolution_DoesNotCallResolveAgain()
        {
            // The pattern the hosts now use: the caller resolves ONCE, keeps the
            // FileResolution, and builds the not-found message from it. That must
            // cost exactly one Resolve — the second scan is the whole point of the
            // fix, and a message-text assertion cannot observe it.
            int before = Baseline();

            var first = FilePathResolver.Resolve("Nope.cs", "Nope.cs", _repo);
            int afterFirst = Baseline();

            string msg = FilePathResolver.BuildFileNotFoundMessage("read_file", "Nope.cs", first);
            int afterMessage = Baseline();

            Assert.Equal(1, afterFirst - before);
            Assert.Equal(0, afterMessage - afterFirst);
            // (If the second resolve crept back, the first assert passes but this
            //  one fails: the message build itself would increment the counter.)
            Assert.Contains("absent", msg);
        }

        [Fact]
        public void ReResolvingFallback_StillPerformsItsOwnResolve()
        {
            // Control for the counter: the shared re-resolving overload (the path the
            // MCP tool surface still uses) performs exactly one additional Resolve —
            // proof the counter actually measures resolutions, so the zero-delta
            // assertion above is meaningful.
            int before = Baseline();

            string msg = FilePathResolver.BuildFileNotFoundMessage("read_file", "Nope.cs", _repo);

            Assert.Equal(1, Baseline() - before);
            Assert.Contains("absent", msg);
        }

        [Fact]
        public void ResolveCallCount_IncrementsOnEveryResolve()
        {
            int before = Baseline();
            FilePathResolver.Resolve("Nope.cs", "Nope.cs", _repo);
            FilePathResolver.Resolve("AlsoNope.cs", "AlsoNope.cs", _repo);
            Assert.Equal(2, Baseline() - before);
        }

        // ── Fix 3: empty / invalid-character filenames must not throw ─────────

        [Fact]
        public void ReResolvingFallback_EmptyFilename_ReturnsCleanNotFound_WithoutThrowing()
        {
            // Path.GetFileName("") throws ArgumentException. The first-resolve helpers
            // tolerate empty input (IsNullOrWhiteSpace guard / SafeGetFileName); the
            // re-resolving fallback must too, or the second call throws where the
            // first returned null gracefully.
            string? msg = null;
            Exception ex = Record.Exception(() =>
                msg = FilePathResolver.BuildFileNotFoundMessage("read_file", "", _repo));
            Assert.Null(ex);
            Assert.NotNull(msg);

            Assert.StartsWith("read_file: file not found", msg);
            Assert.Contains("Searched recursively", msg);
        }

        [Fact]
        public void ReResolvingFallback_InvalidCharacterFilename_ReturnsCleanNotFound_WithoutThrowing()
        {
            // Path.GetFileName("C:\\") throws ArgumentException (invalid character).
            // A hallucinated drive root is a realistic model output — it must reach
            // the clean not-found path, not an exception.
            string? msg = null;
            Exception ex = Record.Exception(() =>
                msg = FilePathResolver.BuildFileNotFoundMessage("read_file", "C:\\\\", _repo));
            Assert.Null(ex);
            Assert.NotNull(msg);

            Assert.StartsWith("read_file: file not found", msg);
            Assert.Contains("Searched recursively", msg);
        }

        [Fact]
        public void BuildMessageFromResolution_InvalidCharacterFilenameInMessage_DoesNotThrow()
        {
            // The message builder itself (all three candidate branches) must survive
            // a filename that Path.GetFileName rejects, since the display name is
            // derived from it.
            var absent = new FileResolution(null, Array.Empty<string>(), new[] { _repo });
            var unique = new FileResolution(null, new[] { Path.Combine(_repo, "QuantEval", "ByteSizeParser.cs") }, new[] { _repo });
            var ambiguous = new FileResolution(null,
                new[] { Path.Combine(_repo, "A.cs"), Path.Combine(_repo, "B.cs") }, new[] { _repo });

            Assert.Null(Record.Exception(() => FilePathResolver.BuildFileNotFoundMessage("read_file", "C:\\\\", absent)));
            Assert.Null(Record.Exception(() => FilePathResolver.BuildFileNotFoundMessage("read_file", "C:\\\\", unique)));
            Assert.Null(Record.Exception(() => FilePathResolver.BuildFileNotFoundMessage("read_file", "C:\\\\", ambiguous)));
        }
    }
}
