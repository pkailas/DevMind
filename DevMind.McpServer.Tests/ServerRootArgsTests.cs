// File: ServerRootArgsTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Guards the split between where the MCP server is rooted and what it may write.
//
// --root sets the working directory (shell cwd, memory, defaults, LSP solution scope)
// and grants nothing; --dir grants write. The historical shape — first --dir is both —
// has to survive untouched, because every existing launcher relies on it.

using DevMind;
using Xunit;

namespace DevMind.McpServer.Tests
{
    public class ServerRootArgsTests : IDisposable
    {
        private readonly string _baseDir;
        private readonly string _rootDir;
        private readonly string _writeDir;
        private readonly string _otherDir;
        private readonly string? _priorGlobalDir;

        public ServerRootArgsTests()
        {
            _baseDir  = Path.Combine(Path.GetTempPath(), $"devmind_rootargs_{Guid.NewGuid():N}");
            _rootDir  = Path.Combine(_baseDir, "root");
            _writeDir = Path.Combine(_baseDir, "write");
            _otherDir = Path.Combine(_baseDir, "other");
            Directory.CreateDirectory(_rootDir);
            Directory.CreateDirectory(_writeDir);
            Directory.CreateDirectory(_otherDir);

            // Keep the real %APPDATA%\devmind\devmind.json out of the write-root
            // computation: allowedWriteRoots there would otherwise leak into the
            // effective set these tests assert on.
            _priorGlobalDir = Environment.GetEnvironmentVariable("DEVMIND_GLOBAL_DIR");
            Environment.SetEnvironmentVariable("DEVMIND_GLOBAL_DIR", Path.Combine(_baseDir, "global"));
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("DEVMIND_GLOBAL_DIR", _priorGlobalDir);
            try { Directory.Delete(_baseDir, recursive: true); } catch { /* best effort */ }
        }

        // ── --root: location without permission ──────────────────────────────────

        [Fact]
        public void Root_SetsWorkingDirectory_AndIsNotAWriteRoot()
        {
            var parsed = ServerRootArgs.Parse(
                new[] { "--root", _rootDir, "--dir", _writeDir }, envWriteRootsRaw: null);

            Assert.Null(parsed.Error);
            Assert.Equal(_rootDir, parsed.WorkingDirectory);
            Assert.False(parsed.WorkingDirectoryIsWriteRoot);
            Assert.Equal(new[] { _writeDir }, parsed.AdditionalWriteRoots);
            Assert.DoesNotContain(_rootDir, parsed.EffectiveWriteRoots);
        }

        [Fact]
        public void Root_WorkingDirectoryIsAbsentFromAllowedWriteRoots()
        {
            var parsed = ServerRootArgs.Parse(
                new[] { "--root", _rootDir, "--dir", _writeDir }, envWriteRootsRaw: null);

            using var svc = new McpServices(parsed.WorkingDirectory, parsed.AdditionalWriteRoots,
                parsed.WorkingDirectoryIsWriteRoot);

            Assert.Equal(Path.GetFullPath(_rootDir), svc.WorkingDirectory);
            Assert.Contains(Path.GetFullPath(_writeDir), svc.AllowedWriteRoots);
            Assert.DoesNotContain(Path.GetFullPath(_rootDir), svc.AllowedWriteRoots);

            // And the guard actually refuses a write under the root.
            var ex = Assert.Throws<InvalidOperationException>(
                () => svc.WriteRoots.EnsureContained(Path.Combine(_rootDir, "x.txt")));
            Assert.Contains("outside the allowed write roots", ex.Message);
        }

        [Fact]
        public void Root_ConsumesNoDirAsPrimary_EveryDirStaysAWriteRoot()
        {
            var parsed = ServerRootArgs.Parse(
                new[] { "--root", _rootDir, "--dir", _writeDir, "--dir", _otherDir },
                envWriteRootsRaw: null);

            Assert.Equal(_rootDir, parsed.WorkingDirectory);
            Assert.Equal(new[] { _writeDir, _otherDir }, parsed.AdditionalWriteRoots);
        }

        [Fact]
        public void RootGivenTwice_IsAFatalError()
        {
            var parsed = ServerRootArgs.Parse(
                new[] { "--root", _rootDir, "--root", _otherDir }, envWriteRootsRaw: null);

            Assert.NotNull(parsed.Error);
            Assert.Contains("--root was given more than once", parsed.Error);
        }

        [Fact]
        public void RootWithNoDir_IsReadOnlyAndSaysSoAtStartup()
        {
            var parsed = ServerRootArgs.Parse(new[] { "--root", _rootDir }, envWriteRootsRaw: null);

            Assert.Null(parsed.Error);
            Assert.Empty(parsed.EffectiveWriteRoots);
            Assert.Contains(parsed.Messages, m => m.Contains("READ-ONLY"));
            Assert.Contains(parsed.Messages, m => m.Contains("--dir <absolute-path>"));
        }

        [Fact]
        public void RootWithNoDir_WriteRefusalExplainsTheReadOnlyStart()
        {
            var parsed = ServerRootArgs.Parse(new[] { "--root", _rootDir }, envWriteRootsRaw: null);

            using var svc = new McpServices(parsed.WorkingDirectory, parsed.AdditionalWriteRoots,
                parsed.WorkingDirectoryIsWriteRoot);

            Assert.Empty(svc.AllowedWriteRoots);
            var ex = Assert.Throws<InvalidOperationException>(
                () => svc.WriteRoots.EnsureContained(Path.Combine(_rootDir, "x.txt")));
            Assert.Contains("read-only", ex.Message);
            Assert.Contains("--root with no --dir", ex.Message);
        }

        [Fact]
        public void Root_MustBeAbsolute()
        {
            var parsed = ServerRootArgs.Parse(new[] { "--root", "relative/path" }, envWriteRootsRaw: null);

            Assert.NotNull(parsed.Error);
            Assert.Contains("--root must be an absolute path", parsed.Error);
        }

        // ── --dir only: byte-for-byte the historical behaviour ───────────────────

        [Fact]
        public void DirOnly_FirstDirIsWorkingDirectoryAndWritable()
        {
            var parsed = ServerRootArgs.Parse(new[] { "--dir", _rootDir }, envWriteRootsRaw: null);

            Assert.Null(parsed.Error);
            Assert.Equal(_rootDir, parsed.WorkingDirectory);
            Assert.True(parsed.WorkingDirectoryIsWriteRoot);
            Assert.Contains(_rootDir, parsed.EffectiveWriteRoots);
        }

        [Fact]
        public void DirOnly_WorkingDirectoryStaysInAllowedWriteRoots()
        {
            var parsed = ServerRootArgs.Parse(
                new[] { "--dir", _rootDir, "--dir", _writeDir }, envWriteRootsRaw: null);

            using var svc = new McpServices(parsed.WorkingDirectory, parsed.AdditionalWriteRoots,
                parsed.WorkingDirectoryIsWriteRoot);

            Assert.Contains(Path.GetFullPath(_rootDir), svc.AllowedWriteRoots);
            Assert.Contains(Path.GetFullPath(_writeDir), svc.AllowedWriteRoots);
            Assert.Equal(Path.Combine(_rootDir, "x.txt"),
                svc.WriteRoots.EnsureContained(Path.Combine(_rootDir, "x.txt")));
        }

        [Fact]
        public void DirOnly_SubsequentDirsAreAdditionalWriteRoots()
        {
            var parsed = ServerRootArgs.Parse(
                new[] { "--dir", _rootDir, "--dir", _writeDir, "--dir", _otherDir },
                envWriteRootsRaw: null);

            Assert.Equal(_rootDir, parsed.WorkingDirectory);
            Assert.Equal(new[] { _writeDir, _otherDir }, parsed.AdditionalWriteRoots);
        }

        [Fact]
        public void DirOnly_StartupLineWordingIsUnchanged()
        {
            var single = ServerRootArgs.Parse(new[] { "--dir", _rootDir }, envWriteRootsRaw: null);
            Assert.Equal($"[McpServer] Starting. Working directory: {_rootDir}",
                Assert.Single(single.Messages));

            var multi = ServerRootArgs.Parse(
                new[] { "--dir", _rootDir, "--dir", _writeDir }, envWriteRootsRaw: null);
            Assert.Equal(
                $"[McpServer] Starting. Working directory: {_rootDir} | Additional write roots: {_writeDir}",
                Assert.Single(multi.Messages));
        }

        [Fact]
        public void NoArgs_WorkingDirectoryIsTheProcessCurrentDirectory()
        {
            var parsed = ServerRootArgs.Parse(Array.Empty<string>(), envWriteRootsRaw: null);

            Assert.Equal(Environment.CurrentDirectory, parsed.WorkingDirectory);
            Assert.True(parsed.WorkingDirectoryIsWriteRoot);
        }

        [Fact]
        public void Dir_MustBeAbsolute()
        {
            var parsed = ServerRootArgs.Parse(new[] { "--dir", "relative/path" }, envWriteRootsRaw: null);

            Assert.NotNull(parsed.Error);
            Assert.Contains("--dir must be an absolute path", parsed.Error);
        }

        // ── DEVMIND_ALLOWED_WRITE_ROOTS ──────────────────────────────────────────

        [Fact]
        public void EnvWriteRoots_AreAddedAndValidated()
        {
            string missing = Path.Combine(_baseDir, "does-not-exist");
            var parsed = ServerRootArgs.Parse(
                new[] { "--root", _rootDir },
                envWriteRootsRaw: $"{_writeDir};relative;{missing}");

            Assert.Equal(new[] { _writeDir }, parsed.AdditionalWriteRoots);
            Assert.Contains(parsed.Messages, m => m.Contains("is not absolute, skipping: 'relative'"));
            Assert.Contains(parsed.Messages, m => m.Contains("does not exist, skipping"));
            // An env root is a real grant, so this is not the read-only start.
            Assert.DoesNotContain(parsed.Messages, m => m.Contains("READ-ONLY"));
        }
    }
}
