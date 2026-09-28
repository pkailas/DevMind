// File: BufferedReadToolGoldenTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// The shared read-side golden cases (Shared/ReadToolGolden.cs) through BufferedAgenticHost —
// the CLI and headless-job host. The same cases run through TuiAgenticHost (TUI.Tests) and
// the MCP tools (McpServer.Tests).

using DevMind.ReadToolGolden;
using Xunit;

namespace DevMind.Core.Tests
{
    public sealed class BufferedReadToolGoldenTests
    {
        public static IEnumerable<object[]> Cases => ReadToolGoldenCases.Names;

        [Theory]
        [MemberData(nameof(Cases))]
        public Task Golden(string name) =>
            ReadToolGoldenCases.RunAsync(name, root => new AgentHostAdapter(new BufferedAgenticHost(root)));
    }
}
