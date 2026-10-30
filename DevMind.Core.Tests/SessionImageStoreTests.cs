// File: SessionImageStoreTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Tests for SessionImageStore — the per-session on-disk copies of user-attached images:
//   * Save decodes the data URI to the exact bytes, picks the extension from the MIME,
//     numbers files in order, sanitizes the label, and never throws (null on failure).
//   * DeleteSession removes the folder; later saves recreate it and restart numbering.
//   * SweepOrphans deletes dead-pid and >7-day folders, keeps the current session's
//     folder and a live process's folder.
//
// Hermetic: every test gets its own temp root through the constructor seam, and a fixed
// session id through the provider seam — nothing touches %LOCALAPPDATA%.

using Xunit;

namespace DevMind.Core.Tests
{
    public sealed class SessionImageStoreTests : IDisposable
    {
        private readonly string _root;
        private string _sessionId = "2026-10-30T120000Z-pid" + Environment.ProcessId;

        public SessionImageStoreTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "devmind-imgstore-" + Guid.NewGuid().ToString("N"));
        }

        public void Dispose()
        {
            try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
        }

        private SessionImageStore NewStore(string? root = null) => new SessionImageStore(root ?? _root, () => _sessionId);

        private static string DataUri(string mime, byte[] bytes) => $"data:{mime};base64,{Convert.ToBase64String(bytes)}";

        private static readonly byte[] PngBytes = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3 };

        // ── Save ──────────────────────────────────────────────────────────────

        [Fact]
        public void Save_WritesDecodedBytes_IntoTheSessionFolder()
        {
            var store = NewStore();

            string? path = store.Save(DataUri("image/png", PngBytes), "logo.png");

            Assert.NotNull(path);
            Assert.True(Path.IsPathRooted(path));
            Assert.Equal(Path.Combine(_root, _sessionId), Path.GetDirectoryName(path));
            Assert.Equal(PngBytes, File.ReadAllBytes(path!));
            Assert.Equal("001-logo.png", Path.GetFileName(path));
        }

        [Theory]
        [InlineData("image/png", ".png")]
        [InlineData("image/jpeg", ".jpg")]
        [InlineData("image/gif", ".gif")]
        [InlineData("image/webp", ".webp")]
        [InlineData("image/bmp", ".bmp")]
        public void Save_PicksExtensionFromMime(string mime, string ext)
        {
            string? path = NewStore().Save(DataUri(mime, new byte[] { 1, 2, 3, 4 }), "shot");
            Assert.Equal(ext, Path.GetExtension(path));
        }

        [Fact]
        public void Save_NumbersFilesInOrder()
        {
            var store = NewStore();
            string? a = store.Save(DataUri("image/png", PngBytes), "a.png");
            string? b = store.Save(DataUri("image/png", PngBytes), "b.png");
            string? c = store.Save(DataUri("image/png", PngBytes), "report.pdf p3");

            Assert.Equal("001-a.png", Path.GetFileName(a));
            Assert.Equal("002-b.png", Path.GetFileName(b));
            Assert.Equal("003-report.pdf_p3.png", Path.GetFileName(c));
        }

        [Fact]
        public void Save_ContinuesNumbering_PastFilesAlreadyInTheFolder()
        {
            NewStore().Save(DataUri("image/png", PngBytes), "first.png");
            // A second store instance (e.g. same session id re-adopted) must not overwrite 001.
            string? next = NewStore().Save(DataUri("image/png", PngBytes), "second.png");
            Assert.Equal("002-second.png", Path.GetFileName(next));
        }

        [Theory]
        [InlineData("my photo (1).png", "my_photo_1")]
        [InlineData("..\\..\\evil:name?.jpg", "evil_name")]
        [InlineData("", "image")]
        [InlineData("   ", "image")]
        [InlineData("report.pdf p3", "report.pdf_p3")]
        public void SanitizeLabel_KeepsOnlySafeFileNameChars(string label, string expected)
        {
            Assert.Equal(expected, SessionImageStore.SanitizeLabel(label));
        }

        [Fact]
        public void SanitizeLabel_CapsLength()
        {
            string s = SessionImageStore.SanitizeLabel(new string('x', 500) + ".png");
            Assert.Equal(SessionImageStore.MaxLabelLength, s.Length);
        }

        [Fact]
        public void Save_ReturnsNull_WhenRootIsUnwritable()
        {
            // A FILE where the root directory should be: CreateDirectory beneath it fails.
            Directory.CreateDirectory(_root);
            string blocker = Path.Combine(_root, "blocker");
            File.WriteAllText(blocker, "not a directory");

            string? path = NewStore(Path.Combine(blocker, "images")).Save(DataUri("image/png", PngBytes), "x.png");

            Assert.Null(path);
        }

        [Fact]
        public void Save_ReturnsNull_ForUndecodablePayload()
        {
            Assert.Null(NewStore().Save("data:image/png;base64,@@not-base64@@", "x.png"));
        }

        // ── DeleteSession ─────────────────────────────────────────────────────

        [Fact]
        public void DeleteSession_RemovesTheFolder_AndNumberingRestartsAfter()
        {
            var store = NewStore();
            string? first = store.Save(DataUri("image/png", PngBytes), "a.png");
            string dir = Path.GetDirectoryName(first)!;

            store.DeleteSession(_sessionId);

            Assert.False(Directory.Exists(dir));
            string? again = store.Save(DataUri("image/png", PngBytes), "b.png");
            Assert.Equal("001-b.png", Path.GetFileName(again));
        }

        [Fact]
        public void DeleteSession_MissingFolder_DoesNotThrow()
        {
            NewStore().DeleteSession("no-such-session");
        }

        // ── SweepOrphans ──────────────────────────────────────────────────────

        // Windows pids are multiples of 4 and far below this — never a running process.
        private const int DeadPid = 2147483644;

        private string MakeFolder(string name, DateTime? lastWriteUtc = null)
        {
            string dir = Path.Combine(_root, name);
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, "001-x.png"), PngBytes);
            if (lastWriteUtc.HasValue)
                Directory.SetLastWriteTimeUtc(dir, lastWriteUtc.Value);
            return dir;
        }

        [Fact]
        public void SweepOrphans_DeletesDeadPidFolder()
        {
            string dead = MakeFolder($"2026-10-01T000000Z-pid{DeadPid}");
            NewStore().SweepOrphans();
            Assert.False(Directory.Exists(dead));
        }

        [Fact]
        public void SweepOrphans_DeletesFolderOlderThanSevenDays_EvenWithLivePid()
        {
            string old = MakeFolder($"2026-10-01T000000Z-pid{Environment.ProcessId}-forkabc",
                DateTime.UtcNow.AddDays(-8));
            NewStore().SweepOrphans();
            Assert.False(Directory.Exists(old));
        }

        [Fact]
        public void SweepOrphans_KeepsLivePidFolder()
        {
            // Another session of a live process (here: this test process).
            string live = MakeFolder($"2026-10-29T000000Z-pid{Environment.ProcessId}");
            NewStore().SweepOrphans();
            Assert.True(Directory.Exists(live));
        }

        [Fact]
        public void SweepOrphans_KeepsCurrentSession_EvenWhenOld()
        {
            _sessionId = $"2026-01-01T000000Z-pid{DeadPid}"; // a resumed session's adopted id
            string current = MakeFolder(_sessionId, DateTime.UtcNow.AddDays(-30));
            NewStore().SweepOrphans();
            Assert.True(Directory.Exists(current));
        }

        [Fact]
        public void SweepOrphans_OwnerMarker_OverridesTheNamePid()
        {
            // A resumed session: folder named after a dead process, but saved into by a live one.
            _sessionId = $"2026-10-01T000000Z-pid{DeadPid}";
            string adopted = Path.GetDirectoryName(NewStore().Save(DataUri("image/png", PngBytes), "a.png"))!;

            _sessionId = "2026-10-30T130000Z-pid" + Environment.ProcessId; // another TUI sweeping
            NewStore().SweepOrphans();

            Assert.True(Directory.Exists(adopted));
        }

        [Fact]
        public void SweepOrphans_FolderWithoutPid_UsesAgeRuleOnly()
        {
            string fresh = MakeFolder("trace-run-id-fresh");
            string stale = MakeFolder("trace-run-id-stale", DateTime.UtcNow.AddDays(-8));

            NewStore().SweepOrphans();

            Assert.True(Directory.Exists(fresh));
            Assert.False(Directory.Exists(stale));
        }

        [Fact]
        public void SweepOrphans_MissingRoot_DoesNotThrow()
        {
            NewStore(Path.Combine(_root, "absent")).SweepOrphans();
        }
    }
}
