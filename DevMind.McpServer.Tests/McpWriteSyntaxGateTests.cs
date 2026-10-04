// File: McpWriteSyntaxGateTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// H-04 on the MCP write path: write_file / create_file (DevMindTools' own write code, not the agent
// hosts') refuse a .cs file that would add a syntax error — the same CSharpSyntaxGate and the same
// refusal text as the hosts' save/append and patch_file. Non-.cs files are never parsed.

using DevMind.McpServer;
using Xunit;

namespace DevMind.McpServer.Tests
{
    public sealed class McpWriteSyntaxGateTests : IDisposable
    {
        private readonly string _dir;
        private readonly DevMindTools _tools;

        public McpWriteSyntaxGateTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), $"devmind_mcpgate_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_dir);
            _tools = new DevMindTools(new McpServices(_dir));
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
        }

        [Fact]
        public async Task CreateFile_ABrokenCsFile_IsRefused_NothingCreated()
        {
            string path = Path.Combine(_dir, "Broken.cs");
            string r = await _tools.CreateFile(path, "public class Broken {\n");

            Assert.StartsWith("create_file: failed — " + CSharpSyntaxGate.Marker, r);
            Assert.Contains("re-read the region and patch again", r);
            Assert.False(File.Exists(path));
        }

        [Fact]
        public async Task WriteFile_OverAValidCsFile_WithASyntaxError_IsRefused_FileUnchanged()
        {
            string path = Path.Combine(_dir, "Widget.cs");
            File.WriteAllText(path, "public class Widget { }\n");

            string r = await _tools.WriteFile(path, "public class Widget { public int X => 1;\n");

            Assert.StartsWith("write_file: failed — " + CSharpSyntaxGate.Marker, r);
            Assert.Equal("public class Widget { }\n", File.ReadAllText(path));
        }

        [Fact]
        public async Task WriteFile_AValidCsFile_IsWritten()
        {
            string path = Path.Combine(_dir, "Widget.cs");
            string r = await _tools.WriteFile(path, "public class Widget { public int X => 1; }\n");

            Assert.StartsWith("write_file: created", r);
            Assert.Contains("public int X", File.ReadAllText(path));
        }

        [Fact]
        public async Task WriteFile_AFileAlreadyBroken_StillWritableWithoutNewErrors()
        {
            string path = Path.Combine(_dir, "Half.cs");
            File.WriteAllText(path, "public class Half {\n    public void A() { }\n");     // missing its closing brace

            string r = await _tools.WriteFile(path, "public class Half {\n    public void B() { }\n");

            Assert.StartsWith("write_file: overwrote", r);
        }

        [Theory]
        [InlineData("notes.txt")]
        [InlineData("app.js")]
        [InlineData("Page.cshtml")]
        public async Task NonCsFiles_AreNeverGated(string name)
        {
            string r = await _tools.WriteFile(Path.Combine(_dir, name), "function f() {\n");
            Assert.StartsWith("write_file: created", r);
        }
    }
}
