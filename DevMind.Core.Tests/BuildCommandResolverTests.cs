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

        // ── Classic .NET Framework detection tests ──────────────────────────

        [Fact]
        public void IsClassicFramework_ClassicVbprojWithTfv_ReturnsTrue()
        {
            string vbproj = Path.Combine(_dir, "Connector.vbproj");
            File.WriteAllText(vbproj,
                @"<?xml version=""1.0"" encoding=""utf-8""?>
<Project DefaultTargets=""Build"" ToolsVersion=""4.0"" xmlns=""http://schemas.microsoft.com/developer/msbuild/2003"">
  <PropertyGroup>
    <TargetFrameworkVersion>v4.8</TargetFrameworkVersion>
  </PropertyGroup>
</Project>");

            Assert.True(BuildCommandResolver.IsClassicFramework(vbproj));
        }

        [Fact]
        public void IsClassicFramework_ClassicCsprojNoSdk_ReturnsTrue()
        {
            string csproj = Path.Combine(_dir, "Legacy.csproj");
            File.WriteAllText(csproj,
                @"<?xml version=""1.0"" encoding=""utf-8""?>
<Project ToolsVersion=""15.0"" xmlns=""http://schemas.microsoft.com/developer/msbuild/2003"">
  <PropertyGroup>
    <TargetFramework>net472</TargetFramework>
  </PropertyGroup>
</Project>");

            Assert.True(BuildCommandResolver.IsClassicFramework(csproj));
        }

        [Fact]
        public void IsClassicFramework_SdkStyleCsproj_ReturnsFalse()
        {
            string csproj = Path.Combine(_dir, "Modern.csproj");
            File.WriteAllText(csproj,
                @"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
</Project>");

            Assert.False(BuildCommandResolver.IsClassicFramework(csproj));
        }

        [Fact]
        public void IsClassicFramework_MissingFile_ReturnsFalse()
        {
            Assert.False(BuildCommandResolver.IsClassicFramework(Path.Combine(_dir, "nonexistent.csproj")));
        }

        [Fact]
        public void IsClassicFramework_SlnWithClassicVbproj_ReturnsTrue()
        {
            string vbproj = Path.Combine(_dir, "Connector.vbproj");
            File.WriteAllText(vbproj,
                @"<?xml version=""1.0"" encoding=""utf-8""?>
<Project DefaultTargets=""Build"" ToolsVersion=""4.0"" xmlns=""http://schemas.microsoft.com/developer/msbuild/2003"">
  <PropertyGroup>
    <TargetFrameworkVersion>v4.8</TargetFrameworkVersion>
  </PropertyGroup>
</Project>");

            string sln = Path.Combine(_dir, "Solution.sln");
            File.WriteAllText(sln,
                @"Microsoft Visual Studio Solution File, Format Version 12.00
# Visual Studio Version 17
VisualStudioVersion = 17.0.31903.59
MinimumVisualStudioVersion = 10.0.40219.1
Project(""{F184B08F-C81C-45F6-A57F-5ABD9991F2EE}"") = ""Connector"", ""Connector.vbproj"", ""{A1B2C3D4-E5F6-7890-ABCD-EF1234567890}""
EndProject
Global
    GlobalSection(SolutionConfigurationPlatforms) = preSolution
        Debug|Any CPU = Debug|Any CPU
        Release|Any CPU = Release|Any CPU
    EndGlobalSection
EndGlobal
");

            Assert.True(BuildCommandResolver.IsClassicFramework(sln));
        }

        [Fact]
        public void IsClassicFramework_SlnWithOnlySdkProjects_ReturnsFalse()
        {
            string csproj = Path.Combine(_dir, "Modern.csproj");
            File.WriteAllText(csproj,
                @"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
</Project>");

            string sln = Path.Combine(_dir, "Solution.sln");
            File.WriteAllText(sln,
                @"Microsoft Visual Studio Solution File, Format Version 12.00
# Visual Studio Version 17
VisualStudioVersion = 17.0.31903.59
MinimumVisualStudioVersion = 10.0.40219.1
Project(""{{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}}""), ""Modern"", ""Modern.csproj"", ""{{A1B2C3D4-E5F6-7890-ABCD-EF1234567890}}""
EndProject
Global
    GlobalSection(SolutionConfigurationPlatforms) = preSolution
        Debug|Any CPU = Debug|Any CPU
        Release|Any CPU = Release|Any CPU
    EndGlobalSection
EndGlobal
");

            Assert.False(BuildCommandResolver.IsClassicFramework(sln));
        }

        [Fact]
        public void IsClassicFramework_SlnxWithNoClassicProjects_ReturnsFalse()
        {
            string csproj = Path.Combine(_dir, "Modern.csproj");
            File.WriteAllText(csproj,
                @"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
</Project>");

            string slnx = Path.Combine(_dir, "Solution.slnx");
            File.WriteAllText(slnx,
                @"<Solution>
  <Projects>
    <Project Path=""Modern.csproj"" />
  </Projects>
</Solution>");

            Assert.False(BuildCommandResolver.IsClassicFramework(slnx));
        }

        [Fact]
        public void IsClassicFramework_SlnxWithPropsFileSolutionItem_ReturnsFalse()
        {
            // Regression: .slnx files contain solution items like
            // <File Path="Directory.Build.props" /> — the props file's root
            // <Project> element has no Sdk= attribute, so IsProjectClassic
            // returns true. Without filtering by project extension, an
            // all-SDK solution is misclassified as classic .NET Framework.
            string csproj = Path.Combine(_dir, "Modern.csproj");
            File.WriteAllText(csproj,
                @"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
</Project>");

            string propsFile = Path.Combine(_dir, "Directory.Build.props");
            File.WriteAllText(propsFile,
                @"<Project>
  <PropertyGroup>
    <LangVersion>latest</LangVersion>
  </PropertyGroup>
</Project>");

            string slnx = Path.Combine(_dir, "Solution.slnx");
            File.WriteAllText(slnx,
                @"<Solution>
  <Projects>
    <Project Path=""Modern.csproj"" />
  </Projects>
  <Folder Include=""."">
    <File Path=""Directory.Build.props"" />
  </Folder>
</Solution>");

            Assert.False(BuildCommandResolver.IsClassicFramework(slnx));
        }

        [Fact]
        public void Resolve_SdkStyleSlnx_StillDotnetBuild()
        {
            // SDK-style .slnx with no classic projects → dotnet build (unchanged behavior).
            string slnx = Path.Combine(_dir, "App.slnx");
            File.WriteAllText(slnx,
                @"<Solution>
  <Projects>
    <Project Path=""Modern.csproj"" />
  </Projects>
</Solution>");

            string command = BuildCommandResolver.Resolve(_dir);
            Assert.Equal($"dotnet build \"{slnx}\"", command);
        }

        [Fact]
        public void Resolve_ClassicCsproj_UsesMsbuildOrWarnsOnFallback()
        {
            // Bare classic .csproj in working dir — environment-independent invariant:
            // either MSBuild is used (command contains "/t:Build") or dotnet build
            // fallback is used with a warning.
            string csproj = Path.Combine(_dir, "Legacy.csproj");
            File.WriteAllText(csproj,
                @"<?xml version=""1.0"" encoding=""utf-8""?>
<Project ToolsVersion=""15.0"" xmlns=""http://schemas.microsoft.com/developer/msbuild/2003"">
  <PropertyGroup>
    <TargetFrameworkVersion>v4.8</TargetFrameworkVersion>
  </PropertyGroup>
</Project>");

            List<string> warnings = new();
            string command = BuildCommandResolver.Resolve(_dir, w => warnings.Add(w));

            // Environment-independent invariant: MSBuild command (contains "/t:Build")
            // OR dotnet build fallback (starts with "dotnet build" AND warning emitted).
            bool usesMsbuild = command.Contains("/t:Build");
            bool usesFallback = command.StartsWith("dotnet build")
                && warnings.Any(w => w.Contains("Classic .NET Framework"));
            Assert.True(usesMsbuild || usesFallback, $"Expected MSBuild or dotnet build fallback, got: {command}");
        }

        [Fact]
        public void Resolve_ClassicSlnWithVbproj_UsesMsbuildOrWarnsOnFallback()
        {
            // .sln referencing a classic .vbproj — environment-independent invariant:
            // either MSBuild is used (command contains "/t:Build") or dotnet build
            // fallback is used with a warning.
            string vbproj = Path.Combine(_dir, "Connector.vbproj");
            File.WriteAllText(vbproj,
                @"<?xml version=""1.0"" encoding=""utf-8""?>
<Project DefaultTargets=""Build"" ToolsVersion=""4.0"" xmlns=""http://schemas.microsoft.com/developer/msbuild/2003"">
  <PropertyGroup>
    <TargetFrameworkVersion>v4.8</TargetFrameworkVersion>
  </PropertyGroup>
</Project>");

            string sln = Path.Combine(_dir, "Solution.sln");
            File.WriteAllText(sln,
                @"Microsoft Visual Studio Solution File, Format Version 12.00
Project(""{F184B08F-C81C-45F6-A57F-5ABD9991F2EE}"") = ""Connector"", ""Connector.vbproj"", ""{A1B2C3D4-E5F6-7890-ABCD-EF1234567890}""
EndProject
Global
    GlobalSection(SolutionConfigurationPlatforms) = preSolution
        Debug|Any CPU = Debug|Any CPU
    EndGlobalSection
EndGlobal
");

            List<string> warnings = new();
            string command = BuildCommandResolver.Resolve(_dir, w => warnings.Add(w));

            // Environment-independent invariant: MSBuild command (contains "/t:Build")
            // OR dotnet build fallback (starts with "dotnet build" AND warning emitted).
            bool usesMsbuild = command.Contains("/t:Build");
            bool usesFallback = command.StartsWith("dotnet build")
                && warnings.Any(w => w.Contains("Classic .NET Framework"));
            Assert.True(usesMsbuild || usesFallback, $"Expected MSBuild or dotnet build fallback, got: {command}");
        }
    }
}
