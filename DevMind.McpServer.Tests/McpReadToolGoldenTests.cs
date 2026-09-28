// File: McpReadToolGoldenTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// The shared read-side golden cases (DevMind.Core.Tests/Shared/ReadToolGolden.cs, linked)
// through the MCP tools. Same cases as BufferedReadToolGoldenTests and TuiReadToolGoldenTests;
// the expected text differs only in the documented tool vocabulary (read_file, grep_file, …).

using DevMind.ReadToolGolden;
using Xunit;

namespace DevMind.McpServer.Tests
{
    public sealed class McpReadToolGoldenTests
    {
        public static IEnumerable<object[]> Cases => ReadToolGoldenCases.Names;

        [Theory]
        [MemberData(nameof(Cases))]
        public Task Golden(string name) => ReadToolGoldenCases.RunAsync(name, root => new McpAdapter(root));

        private sealed class McpAdapter : IReadToolAdapter, IDisposable
        {
            private readonly McpServices _svc;
            private readonly DevMindTools _tools;

            public McpAdapter(string root)
            {
                _svc = new McpServices(root);
                _tools = new DevMindTools(_svc);
            }

            public bool IsMcp => true;
            public Task<string> Read(string filename, int? startLine = null, int? endLine = null, bool forceFull = false)
                => _tools.ReadFile(filename, startLine, endLine, forceFull ? true : null);
            public Task<string> Grep(string pattern, string filename, int? startLine = null, int? endLine = null)
                => _tools.GrepFile(pattern, filename, startLine, endLine);
            public Task<string> Find(string pattern, string glob, int? startLine = null, int? endLine = null)
                => _tools.FindInFiles(pattern, glob, root: null, startLine, endLine);
            public Task<string> List(string glob, bool recursive = true) => _tools.ListFiles(glob, recursive);
            public Task<string> Diff(string filename) => _tools.DiffFile(filename);
            public void Dispose() => _svc.Dispose();
        }
    }
}
