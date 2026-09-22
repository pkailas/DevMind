// File: FindSymbolNoSolutionTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// find_symbol used to fall back to the search start directory when no *.sln/*.slnx (or
// TypeScript marker) enclosed it. The language server then started with nothing opened,
// and the empty result said "no symbols matching X in the C# solution at <dir>" — a
// confident false negative naming a solution that never existed. Seen live with the MCP
// server rooted at the repository CONTAINER.
//
// These tests drive the real LanguageServerRouter.FindSymbolAsync: with nothing enclosing
// the start directory it must answer without creating a host, so the tests run without
// any language server installed and finish instantly. A resolved solution still takes
// the old path (guarded here through TryFindEnclosingProjectRoot, since exercising it
// end-to-end would spawn a server).

using System;
using System.IO;
using System.Threading.Tasks;
using DevMind;
using Xunit;

namespace DevMind.Core.Tests
{
    public sealed class FindSymbolNoSolutionTests : IDisposable
    {
        private readonly string _baseDir;

        public FindSymbolNoSolutionTests()
        {
            _baseDir = Path.Combine(Path.GetTempPath(), $"devmind_findsym_nosln_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_baseDir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_baseDir, recursive: true); } catch { /* best effort */ }
        }

        private string Dir(params string[] parts)
        {
            string p = Path.Combine(_baseDir, Path.Combine(parts));
            Directory.CreateDirectory(p);
            return p;
        }

        private string Container(params string[] repoNames)
        {
            string container = Dir("repos");
            foreach (string name in repoNames)
                Directory.CreateDirectory(Path.Combine(container, name, ".git"));
            return container;
        }

        // ── (a) container root, no path ──────────────────────────────────────────

        [Fact]
        public async Task ContainerRoot_NoPath_SaysPickARepository_AndNamesTheDirectory()
        {
            string container = Container("Alpha", "Beta");
            using var router = new LanguageServerRouter(container);

            string result = await router.FindSymbolAsync("Widget", 50, "csharp");

            Assert.StartsWith("find_symbol: ", result);
            Assert.Contains(container, result);
            Assert.Contains("folder of repositories", result);
            Assert.Contains("Pass path=", result);
            Assert.Contains("Alpha", result);
            Assert.Contains("Beta", result);
            Assert.DoesNotContain("no symbols matching", result);
        }

        [Fact]
        public async Task ContainerRoot_RepositoryListingIsCappedAtFive()
        {
            string container = Container("R1", "R2", "R3", "R4", "R5", "R6", "R7");
            using var router = new LanguageServerRouter(container);

            string result = await router.FindSymbolAsync("Widget", 50, "csharp");

            int named = 0;
            for (int i = 1; i <= 7; i++)
                if (result.Contains("R" + i)) named++;
            Assert.Equal(5, named);
        }

        // ── (b) plain directory, nothing above it, no path ───────────────────────

        [Fact]
        public async Task PlainDirectory_NoPath_SaysNoSolutionWasLoaded()
        {
            string plain = Dir("plain", "src");
            using var router = new LanguageServerRouter(plain);

            string result = await router.FindSymbolAsync("Widget", 50, "csharp");

            Assert.StartsWith("find_symbol: ", result);
            Assert.Contains(plain, result);
            Assert.Contains("not inside a C# solution", result);
            Assert.Contains("no *.sln or *.slnx at or above it", result);
            Assert.Contains("nothing was searched", result);
            Assert.Contains("Pass path=", result);
            Assert.DoesNotContain("folder of repositories", result);
            Assert.DoesNotContain("no symbols matching", result);
        }

        // ── (c) path supplied, resolves outside any solution ─────────────────────

        [Fact]
        public async Task PathHint_OutsideAnySolution_NamesTheHintAndWhereItResolved()
        {
            string session = Dir("session");
            string elsewhere = Dir("elsewhere", "lib");
            using var router = new LanguageServerRouter(session);

            string result = await router.FindSymbolAsync("Widget", 50, "csharp", default, elsewhere);

            Assert.StartsWith("find_symbol: path '" + elsewhere + "'", result);
            Assert.Contains("resolved to " + elsewhere, result);
            Assert.Contains("not inside a C# solution", result);
            Assert.Contains("nothing was searched", result);
            Assert.Contains("Pass a path inside the solution you mean", result);
            Assert.DoesNotContain("session working directory", result);
        }

        // ── (d) a real solution keeps the old path ───────────────────────────────

        [Fact]
        public void InsideARealSolution_EnclosingRootIsFound_SoNoRefusalIsBuilt()
        {
            string solution = Dir("Repo");
            File.WriteAllText(Path.Combine(solution, "Repo.slnx"), "");
            string inside = Dir("Repo", "src", "Lib");

            string root = LanguageServerRouter.TryFindEnclosingProjectRoot(LanguageServerKind.CSharp, inside);

            Assert.Equal(Path.GetFullPath(solution), Path.GetFullPath(root));
            // And the public resolver's answer for the same input is unchanged.
            Assert.Equal(root, LanguageServerRouter.ResolveSymbolSearchRoot(LanguageServerKind.CSharp, null, inside));
        }

        [Fact]
        public void NoEnclosingSolution_ReturnsNull_RatherThanTheStartDirectory()
        {
            string plain = Dir("nowhere");

            Assert.Null(LanguageServerRouter.TryFindEnclosingProjectRoot(LanguageServerKind.CSharp, plain));
            // The public resolver keeps its documented fallback for its other callers.
            Assert.Equal(plain, LanguageServerRouter.ResolveSymbolSearchRoot(LanguageServerKind.CSharp, null, plain));
        }

        // ── (e) TypeScript wording ───────────────────────────────────────────────

        [Fact]
        public async Task TypeScript_NoProjectMarker_DoesNotSayCSharpSolution()
        {
            string plain = Dir("web");
            using var router = new LanguageServerRouter(plain);

            string result = await router.FindSymbolAsync("Widget", 50, "typescript");

            Assert.StartsWith("find_symbol: ", result);
            Assert.DoesNotContain("C#", result);
            Assert.DoesNotContain("*.sln", result);
            Assert.Contains("TS project", result);
            Assert.Contains("tsconfig.json", result);
        }

        [Fact]
        public void TypeScript_PathHintVariant_UsesProjectWording()
        {
            string message = LanguageServerRouter.BuildNoEnclosingProjectMessage(
                LanguageServerKind.TypeScript, "web/app", Path.Combine(_baseDir, "web", "app"), _baseDir);

            Assert.DoesNotContain("C#", message);
            Assert.DoesNotContain("solution", message);
            Assert.Contains("Pass a path inside the project you mean", message);
        }
    }
}
