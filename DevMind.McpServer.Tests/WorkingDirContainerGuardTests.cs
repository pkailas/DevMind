// File: WorkingDirContainerGuardTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// devmind_task_start validated that working_dir was absolute and existed, and
// nothing more. Passing the folder that CONTAINS the repositories rather than a
// repository therefore started a real job: the agent had no project to build, no
// solution to search, and every relative path it wrote resolved against a tree
// holding none of its work. It burned iterations before failing for reasons that
// looked unrelated to the mistake.
//
// The guard keys on .git rather than on *.sln, deliberately. A project-file marker
// would refuse a Python or TypeScript repository rooted the same way - refusing
// legitimate work to prevent a typo is a worse trade than the typo. Worktrees and
// submodules carry .git as a FILE, so both forms count.
//
// It is a convenience guard, not a security boundary: an unreadable directory lets
// the job proceed. The write-root policy is the boundary.

using System;
using System.IO;
using System.Linq;
using Xunit;

namespace DevMind.McpServer.Tests
{
    public sealed class WorkingDirContainerGuardTests : IDisposable
    {
        private readonly string _root;

        public WorkingDirContainerGuardTests()
        {
            _root = Path.Combine(Path.GetTempPath(), $"devmind_wdguard_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        }

        private string Dir(params string[] parts)
        {
            string p = Path.Combine(new[] { _root }.Concat(parts).ToArray());
            Directory.CreateDirectory(p);
            return p;
        }

        private static void MakeRepo(string dir) => Directory.CreateDirectory(Path.Combine(dir, ".git"));

        private static void MakeWorktreeRepo(string dir) => File.WriteAllText(Path.Combine(dir, ".git"), "gitdir: ../elsewhere/.git");

        // ── The mistake this exists to catch ────────────────────────────────────

        [Fact]
        public void AFolderWhoseChildrenAreRepositories_IsAContainer()
        {
            string container = Dir("source", "repos");
            MakeRepo(Dir("source", "repos", "RepoA"));
            MakeRepo(Dir("source", "repos", "RepoB"));

            Assert.True(AgentTaskTools.LooksLikeRepositoryContainer(container),
                "a folder holding repositories must be recognised as a container, not accepted as a working_dir");
        }

        [Fact]
        public void OneRepositoryChildIsEnough()
        {
            string container = Dir("mixed");
            Dir("mixed", "just-a-folder");
            MakeRepo(Dir("mixed", "TheOnlyRepo"));

            Assert.True(AgentTaskTools.LooksLikeRepositoryContainer(container));
        }

        // ── What must NOT be refused ────────────────────────────────────────────

        [Fact]
        public void ARepositoryItself_IsNotAContainer_EvenWhenItNestsRepositories()
        {
            // A repo with a submodule has .git AND a child with .git. The repo's own
            // marker decides: this is where work belongs.
            string repo = Dir("RepoWithSubmodule");
            MakeRepo(repo);
            MakeRepo(Dir("RepoWithSubmodule", "vendored"));

            Assert.False(AgentTaskTools.LooksLikeRepositoryContainer(repo));
        }

        [Fact]
        public void AWorktreeOrSubmodule_CountsAsARepository_BecauseItsMarkerIsAFile()
        {
            string worktree = Dir("SomeWorktree");
            MakeWorktreeRepo(worktree);
            MakeRepo(Dir("SomeWorktree", "nested"));

            Assert.False(AgentTaskTools.LooksLikeRepositoryContainer(worktree));
        }

        [Fact]
        public void ASubdirectoryOfARepository_IsNotAContainer()
        {
            // Scoping a job to one project inside a solution is legitimate: no .git of
            // its own, and no repository children either.
            MakeRepo(Dir("Repo"));
            string project = Dir("Repo", "src", "Project");

            Assert.False(AgentTaskTools.LooksLikeRepositoryContainer(project));
        }

        [Fact]
        public void AnEmptyDirectory_IsNotAContainer()
        {
            Assert.False(AgentTaskTools.LooksLikeRepositoryContainer(Dir("empty")));
        }

        [Fact]
        public void ADirectoryOfPlainFolders_IsNotAContainer()
        {
            string plain = Dir("plain");
            Dir("plain", "one");
            Dir("plain", "two");

            Assert.False(AgentTaskTools.LooksLikeRepositoryContainer(plain));
        }

        // ── It is a convenience guard, not a boundary ───────────────────────────

        [Fact]
        public void APathThatDoesNotExist_DoesNotThrow_AndDoesNotBlock()
        {
            string missing = Path.Combine(_root, "no-such-directory");

            Assert.False(AgentTaskTools.LooksLikeRepositoryContainer(missing));
        }
    }
}
