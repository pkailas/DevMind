// File: FindSymbolScopeTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// find_symbol is the one LSP tool with no file to route by, so it used to search
// whatever solution enclosed the session working directory. On the MCP surface that is
// fixed at startup, so a question about another repository was answered "no symbols
// matching X" — a false negative that read as a true one.
//
// Two guards here: the path hint must actually re-target the search (the positive case
// carries the weight — asserting a miss in the wrong solution would pass trivially),
// and the empty result must name the solution it searched.

using System;
using System.IO;
using DevMind;
using Xunit;

namespace DevMind.Core.Tests
{
    public sealed class FindSymbolScopeTests : IDisposable
    {
        private readonly string _baseDir;
        private readonly string _solutionA;   // the session working directory
        private readonly string _solutionB;   // a different solution, the one we ask about
        private readonly string _fileInB;

        public FindSymbolScopeTests()
        {
            _baseDir   = Path.Combine(Path.GetTempPath(), $"devmind_findsym_{Guid.NewGuid():N}");
            _solutionA = Path.Combine(_baseDir, "RepoA");
            _solutionB = Path.Combine(_baseDir, "RepoB");
            string srcInB = Path.Combine(_solutionB, "src", "Lib");
            Directory.CreateDirectory(Path.Combine(_solutionA, "src"));
            Directory.CreateDirectory(srcInB);
            File.WriteAllText(Path.Combine(_solutionA, "RepoA.sln"), "");
            File.WriteAllText(Path.Combine(_solutionB, "RepoB.slnx"), "");
            _fileInB = Path.Combine(srcInB, "Widget.cs");
            File.WriteAllText(_fileInB, "class Widget { }");
        }

        public void Dispose()
        {
            try { Directory.Delete(_baseDir, recursive: true); } catch { /* best effort */ }
        }

        // ── Routing ──────────────────────────────────────────────────────────────

        [Fact]
        public void PathHintFile_RoutesToThatFilesSolution_NotTheWorkingDirectorys()
        {
            string root = LanguageServerRouter.ResolveSymbolSearchRoot(
                LanguageServerKind.CSharp, _fileInB, _solutionA);

            Assert.Equal(Path.GetFullPath(_solutionB), Path.GetFullPath(root));
        }

        [Fact]
        public void PathHintDirectory_RoutesToTheEnclosingSolution()
        {
            string root = LanguageServerRouter.ResolveSymbolSearchRoot(
                LanguageServerKind.CSharp, Path.GetDirectoryName(_fileInB), _solutionA);

            Assert.Equal(Path.GetFullPath(_solutionB), Path.GetFullPath(root));
        }

        [Fact]
        public void PathHintRelativeToWorkingDirectory_IsResolved()
        {
            string root = LanguageServerRouter.ResolveSymbolSearchRoot(
                LanguageServerKind.CSharp, Path.Combine("..", "RepoB", "src"), _solutionA);

            Assert.Equal(Path.GetFullPath(_solutionB), Path.GetFullPath(root));
        }

        [Fact]
        public void NoPathHint_KeepsTheWorkingDirectorySolution()
        {
            string root = LanguageServerRouter.ResolveSymbolSearchRoot(
                LanguageServerKind.CSharp, null, Path.Combine(_solutionA, "src"));

            Assert.Equal(Path.GetFullPath(_solutionA), Path.GetFullPath(root));
        }

        [Fact]
        public void PathHintThatDoesNotExist_Throws_RatherThanSearchingTheWrongSolution()
        {
            string missing = Path.Combine(_baseDir, "RepoC", "Nope.cs");

            var ex = Assert.Throws<InvalidOperationException>(() =>
                LanguageServerRouter.ResolveSymbolSearchRoot(
                    LanguageServerKind.CSharp, missing, _solutionA));

            Assert.Contains("does not exist", ex.Message);
        }

        [Fact]
        public void TypeScriptPathHint_RoutesByProjectMarker()
        {
            string tsDir = Path.Combine(_solutionB, "web", "app");
            Directory.CreateDirectory(tsDir);
            File.WriteAllText(Path.Combine(_solutionB, "web", "tsconfig.json"), "{}");

            string root = LanguageServerRouter.ResolveSymbolSearchRoot(
                LanguageServerKind.TypeScript, tsDir, _solutionA);

            Assert.Equal(Path.GetFullPath(Path.Combine(_solutionB, "web")), Path.GetFullPath(root));
        }

        // ── The empty result ─────────────────────────────────────────────────────

        [Fact]
        public void EmptyResultMessage_NamesTheSolutionItSearched()
        {
            string message = LanguageServerHost.BuildFindSymbolEmptyMessage(
                "NearlineCache", "C#", _solutionB);

            Assert.Contains(_solutionB, message);
            Assert.Contains("NearlineCache", message);
            Assert.Contains("C#", message);
            // And it must point at the way out, or the reader is no better off.
            Assert.Contains("path=", message);
        }

        // ── The advertised schema ────────────────────────────────────────────────

        [Fact]
        public void ToolRegistry_FindSymbol_DeclaresThePathParameter()
        {
            var findSymbol = System.Linq.Enumerable.Single(
                ToolRegistry.BuildToolsArray(),
                t => (string?)t["function"]?["name"] == "find_symbol");

            Assert.NotNull(findSymbol["function"]?["parameters"]?["properties"]?["path"]);
        }

        [Fact]
        public void ToolCallMapper_FindSymbol_CarriesPathIntoTheBlock()
        {
            var call = new ToolCallResult
            {
                Name = "find_symbol",
                Arguments = new System.Collections.Generic.Dictionary<string, string>
                {
                    ["query"] = "Widget",
                    ["path"] = _fileInB
                }
            };

            var block = ToolCallMapper.Map(
                new System.Collections.Generic.List<ToolCallResult> { call }, buildCommand: "")[0];

            Assert.Equal(BlockType.FindSymbol, block.Type);
            Assert.Equal("Widget", block.Pattern);
            Assert.Equal(_fileInB, block.FileName);
        }
    }
}
