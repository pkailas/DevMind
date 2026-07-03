// File: BuildCommandResolverTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Regression tests for build-command detection. Live failure: Parsely's root held a
// dependencies-only package.json (stray npm-install debris), so run_build and the
// job runner's build verification resolved to "npm run build" — which fails with
// "Missing script" — instead of dotnet build against Parsely.slnx.

using Xunit;

namespace DevMind.Core.Tests
{
    public class BuildCommandResolverTests : IDisposable
    {
        private readonly string _dir;
        private readonly string? _priorOverride;

        public BuildCommandResolverTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), $"devmind_bcr_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_dir);
            // DEVMIND_BUILD_COMMAND short-circuits detection — must be unset for tests.
            _priorOverride = Environment.GetEnvironmentVariable("DEVMIND_BUILD_COMMAND");
            Environment.SetEnvironmentVariable("DEVMIND_BUILD_COMMAND", null);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("DEVMIND_BUILD_COMMAND", _priorOverride);
            Directory.Delete(_dir, recursive: true);
        }

        [Fact]
        public void DependenciesOnlyPackageJson_DoesNotHijack_DotnetSolutionDetection()
        {
            File.WriteAllText(Path.Combine(_dir, "package.json"),
                "{ \"dependencies\": { \"axios\": \"^1.0.0\" } }");
            string slnx = Path.Combine(_dir, "Thing.slnx");
            File.WriteAllText(slnx, "<Solution />");

            string command = BuildCommandResolver.Resolve(_dir);

            Assert.Equal($"dotnet build \"{slnx}\"", command);
        }

        [Fact]
        public void PackageJsonWithBuildScript_ResolvesToNpm_EvenBesideSolution()
        {
            File.WriteAllText(Path.Combine(_dir, "package.json"),
                "{ \"scripts\": { \"build\": \"vite build\" } }");
            File.WriteAllText(Path.Combine(_dir, "Thing.slnx"), "<Solution />");

            Assert.Equal("npm run build", BuildCommandResolver.Resolve(_dir));
        }

        [Theory]
        [InlineData("{ not json at all")]
        [InlineData("{ \"scripts\": { \"test\": \"jest\" } }")]
        [InlineData("{ \"scripts\": \"oops-not-an-object\" }")]
        public void HasNpmBuildScript_FalseForMissingOrMalformed(string content)
        {
            string path = Path.Combine(_dir, "package.json");
            File.WriteAllText(path, content);
            Assert.False(BuildCommandResolver.HasNpmBuildScript(path));
        }

        [Fact]
        public void HasNpmBuildScript_TrueWhenBuildScriptDefined()
        {
            string path = Path.Combine(_dir, "package.json");
            File.WriteAllText(path, "{ \"scripts\": { \"build\": \"tsc && vite build\" } }");
            Assert.True(BuildCommandResolver.HasNpmBuildScript(path));
        }
    }
}
