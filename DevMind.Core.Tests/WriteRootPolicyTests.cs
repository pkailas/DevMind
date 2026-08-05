// File: WriteRootPolicyTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Covers WriteRootPolicy (reloadable allowed-write-roots) and the
// allowedWriteRoots array in TuiConfig:
//   * config file absent / empty → no config roots (startup behavior unchanged)
//   * config with roots → parsed and merged on reload
//   * reload adds a newly configured root without restart
//   * reload can never remove the working directory or a --dir startup root
//   * containment check rejects paths outside all roots

using DevMind;
using Xunit;

namespace DevMind.Core.Tests
{
    public sealed class WriteRootPolicyTests : IDisposable
    {
        private readonly string _tempRoot;
        private readonly string _workDir;
        private readonly string _dirRoot;   // simulates a --dir startup root
        private readonly string _extraDir;  // granted later via config

        public WriteRootPolicyTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), "wrp-tests-" + Guid.NewGuid().ToString("N"));
            _workDir  = Path.Combine(_tempRoot, "work");
            _dirRoot  = Path.Combine(_tempRoot, "dirroot");
            _extraDir = Path.Combine(_tempRoot, "extra");
            Directory.CreateDirectory(_workDir);
            Directory.CreateDirectory(_dirRoot);
            Directory.CreateDirectory(_extraDir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        private string WriteConfig(string json)
        {
            string path = Path.Combine(_tempRoot, "devmind.json");
            File.WriteAllText(path, json);
            return path;
        }

        // ── TuiConfig parsing ────────────────────────────────────────────────

        [Fact]
        public void ConfigAbsent_YieldsNoConfigRoots()
        {
            var config = TuiConfig.LoadFrom(Path.Combine(_tempRoot, "does-not-exist.json"));
            Assert.Empty(config.AllowedWriteRoots);
        }

        [Fact]
        public void ConfigWithoutProperty_YieldsNoConfigRoots()
        {
            var config = TuiConfig.LoadFrom(WriteConfig("{}"));
            Assert.Empty(config.AllowedWriteRoots);
        }

        [Fact]
        public void ConfigWithEmptyArray_YieldsNoConfigRoots()
        {
            var config = TuiConfig.LoadFrom(WriteConfig("{\"allowedWriteRoots\":[]}"));
            Assert.Empty(config.AllowedWriteRoots);
        }

        [Fact]
        public void ConfigWithRoots_ParsesEntries()
        {
            string json = "{\"allowedWriteRoots\":[" +
                          System.Text.Json.JsonSerializer.Serialize(_extraDir) + "," +
                          System.Text.Json.JsonSerializer.Serialize(_dirRoot) + "]}";
            var config = TuiConfig.LoadFrom(WriteConfig(json));
            Assert.Equal(new[] { _extraDir, _dirRoot }, config.AllowedWriteRoots);
        }

        // ── WriteRootPolicy reload semantics ─────────────────────────────────

        [Fact]
        public void Baseline_ContainsWorkingDirectoryAndStartupRoots()
        {
            var policy = new WriteRootPolicy(_workDir, new[] { _dirRoot });
            Assert.Contains(_workDir, policy.Current, StringComparer.OrdinalIgnoreCase);
            Assert.Contains(_dirRoot, policy.Current, StringComparer.OrdinalIgnoreCase);
            Assert.Equal(2, policy.Current.Count);
        }

        [Fact]
        public void Reload_AddsConfiguredRoot()
        {
            var policy = new WriteRootPolicy(_workDir);
            Assert.DoesNotContain(_extraDir, policy.Current, StringComparer.OrdinalIgnoreCase);

            var roots = policy.Reload(new[] { _extraDir });

            Assert.Contains(_extraDir, roots, StringComparer.OrdinalIgnoreCase);
            Assert.Contains(_extraDir, policy.Current, StringComparer.OrdinalIgnoreCase);
        }

        [Fact]
        public void Reload_NeverRemovesWorkingDirectoryOrStartupRoot()
        {
            var policy = new WriteRootPolicy(_workDir, new[] { _dirRoot });

            // First grant an extra root, then reload with an empty config: the extra
            // root goes away, the baseline stays.
            policy.Reload(new[] { _extraDir });
            var roots = policy.Reload(Array.Empty<string>());

            Assert.Contains(_workDir, roots, StringComparer.OrdinalIgnoreCase);
            Assert.Contains(_dirRoot, roots, StringComparer.OrdinalIgnoreCase);
            Assert.DoesNotContain(_extraDir, roots, StringComparer.OrdinalIgnoreCase);

            // Null config behaves the same as empty.
            roots = policy.Reload(null);
            Assert.Contains(_workDir, roots, StringComparer.OrdinalIgnoreCase);
            Assert.Contains(_dirRoot, roots, StringComparer.OrdinalIgnoreCase);
        }

        [Fact]
        public void Reload_SkipsRelativeAndNonExistentEntries()
        {
            var warnings = new List<string>();
            var policy = new WriteRootPolicy(_workDir);

            var roots = policy.Reload(
                new[] { "relative/path", Path.Combine(_tempRoot, "missing"), _extraDir },
                warnings.Add);

            Assert.Contains(_extraDir, roots, StringComparer.OrdinalIgnoreCase);
            Assert.Equal(2, roots.Count); // working dir + extra only
            Assert.Equal(2, warnings.Count);
        }

        // ── Containment ──────────────────────────────────────────────────────

        [Fact]
        public void EnsureContained_AcceptsPathsUnderAnyRoot()
        {
            var policy = new WriteRootPolicy(_workDir, new[] { _dirRoot });

            string inWork = Path.Combine(_workDir, "sub", "file.txt");
            string inDir  = Path.Combine(_dirRoot, "other.txt");
            Assert.Equal(Path.GetFullPath(inWork), policy.EnsureContained(inWork));
            Assert.Equal(Path.GetFullPath(inDir),  policy.EnsureContained(inDir));
        }

        [Fact]
        public void EnsureContained_RejectsPathOutsideAllRoots()
        {
            var policy = new WriteRootPolicy(_workDir, new[] { _dirRoot });

            string outside = Path.Combine(_extraDir, "file.txt");
            var ex = Assert.Throws<InvalidOperationException>(() => policy.EnsureContained(outside));
            Assert.Contains("Path containment violation", ex.Message);
            Assert.Contains(_workDir, ex.Message);
            Assert.Contains("reload_write_roots", ex.Message);
        }

        [Fact]
        public void EnsureContained_RejectsTraversalEscapingRoot()
        {
            var policy = new WriteRootPolicy(_workDir);

            string sneaky = Path.Combine(_workDir, "..", "escape.txt");
            Assert.Throws<InvalidOperationException>(() => policy.EnsureContained(sneaky));
        }

        [Fact]
        public void EnsureContained_RejectsSiblingWithRootAsPrefix()
        {
            // "C:\...\work" must not match "C:\...\work2".
            string sibling = _workDir + "2";
            Directory.CreateDirectory(sibling);
            var policy = new WriteRootPolicy(_workDir);

            Assert.Throws<InvalidOperationException>(
                () => policy.EnsureContained(Path.Combine(sibling, "file.txt")));
        }

        [Fact]
        public void EnsureContained_AcceptsRootGrantedByReload()
        {
            var policy = new WriteRootPolicy(_workDir);
            string target = Path.Combine(_extraDir, "file.txt");

            Assert.Throws<InvalidOperationException>(() => policy.EnsureContained(target));
            policy.Reload(new[] { _extraDir });
            Assert.Equal(Path.GetFullPath(target), policy.EnsureContained(target));
        }
    }
}
