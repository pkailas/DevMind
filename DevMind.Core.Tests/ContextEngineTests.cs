// File: ContextEngineTests.cs  v1.0
//
// Covers ContextEngine.FindGitRoot — the walk-up git-root locator.

using Xunit;

namespace DevMind.Core.Tests
{
    public class ContextEngineTests : IDisposable
    {
        private readonly string _tmpRoot;

        public ContextEngineTests()
        {
            _tmpRoot = Path.Combine(Path.GetTempPath(), "devmind-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tmpRoot);
        }

        public void Dispose()
        {
            try { Directory.Delete(_tmpRoot, recursive: true); }
            catch { /* best-effort cleanup */ }
        }

        [Fact]
        public void FindGitRoot_FindsGitDirectory()
        {
            // Arrange: nested structure with .git as a DIRECTORY at the root.
            string repoRoot = Path.Combine(_tmpRoot, "repo");
            string subDir = Path.Combine(repoRoot, "src", "MyProject");
            Directory.CreateDirectory(subDir);
            Directory.CreateDirectory(Path.Combine(repoRoot, ".git"));

            // Act
            string result = ContextEngine.FindGitRoot(subDir);

            // Assert
            Assert.Equal(repoRoot.Replace('\\', Path.DirectorySeparatorChar),
                         result!.Replace('\\', Path.DirectorySeparatorChar));
        }

        [Fact]
        public void FindGitRoot_FindsGitFile_WorktreeOrSubmodule()
        {
            // Arrange: nested structure with .git as a FILE (worktree/submodule pointer).
            string repoRoot = Path.Combine(_tmpRoot, "worktree");
            string subDir = Path.Combine(repoRoot, "src", "MyProject");
            Directory.CreateDirectory(subDir);
            File.WriteAllText(Path.Combine(repoRoot, ".git"), "gitdir: /real/repo/.git/worktrees/wt\n");

            // Act
            string result = ContextEngine.FindGitRoot(subDir);

            // Assert
            Assert.Equal(repoRoot.Replace('\\', Path.DirectorySeparatorChar),
                         result!.Replace('\\', Path.DirectorySeparatorChar));
        }

        [Fact]
        public void FindGitRoot_ReturnsNull_WhenNoGit()
        {
            // Arrange: a directory tree with no .git anywhere.
            string baseDir = Path.Combine(_tmpRoot, "nogit", "a", "b");
            Directory.CreateDirectory(baseDir);

            // Act
            string result = ContextEngine.FindGitRoot(baseDir);

            // Assert: walks all the way to the filesystem root (or C:\) without finding .git.
            Assert.Null(result);
        }
    }
}
