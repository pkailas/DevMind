// File: RepositoryContainerForwarderTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// The "folder of repositories, not a repository" check now lives in Core
// (WorkspaceRootResolver) so the LSP router can ask it too; AgentTaskTools keeps a
// forwarder for the devmind_task_start guard. This pins the two to one answer so the
// logic cannot quietly fork again.

using System;
using System.IO;
using DevMind;
using Xunit;

namespace DevMind.McpServer.Tests
{
    public sealed class RepositoryContainerForwarderTests : IDisposable
    {
        private readonly string _root;

        public RepositoryContainerForwarderTests()
        {
            _root = Path.Combine(Path.GetTempPath(), $"devmind_ctrfwd_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        }

        [Fact]
        public void ForwarderAndCoreHelper_AgreeOnEveryShape()
        {
            string container = Path.Combine(_root, "container");
            Directory.CreateDirectory(Path.Combine(container, "RepoA", ".git"));
            string repo = Path.Combine(container, "RepoA");
            string plain = Path.Combine(_root, "plain");
            Directory.CreateDirectory(plain);
            string missing = Path.Combine(_root, "missing");

            foreach (string dir in new[] { container, repo, plain, missing })
            {
                Assert.Equal(
                    WorkspaceRootResolver.LooksLikeRepositoryContainer(dir),
                    AgentTaskTools.LooksLikeRepositoryContainer(dir));
            }

            Assert.True(AgentTaskTools.LooksLikeRepositoryContainer(container));
            Assert.False(AgentTaskTools.LooksLikeRepositoryContainer(repo));
        }
    }
}
