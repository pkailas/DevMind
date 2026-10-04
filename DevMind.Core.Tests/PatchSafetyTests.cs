// File: PatchSafetyTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// H-05: a merge reports HOW it got its result, and only a genuine fallback is labelled.
// H-04: a .cs write that adds a syntax error is refused before anything is written.
// H-67: a fuzzy apply that would duplicate a line is refused; structured files never fuzzy-apply
//       and say which candidate was closest; an exact match always wins.
//
// The replays are shaped after the jobs: job-1652 (an insert inside a test method, leaving it
// unterminated), job-2143 (")]]" in a stacked [LoggerMessage] attribute), job-2146 (the newest
// "// vX.Y.Z" header line duplicated).

using System.Text;
using Xunit;

namespace DevMind.Core.Tests
{
    public class PatchSafetyTests : IDisposable
    {
        private readonly string _dir;

        public PatchSafetyTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), $"devmind_patchsafety_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            ThreeWayMergeCheck.DiffEngineHookForTest = null;
            try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
        }

        private string Write(string name, string content)
        {
            string path = Path.Combine(_dir, name);
            File.WriteAllText(path, content);
            return path;
        }

        private static (PatchResolveResult? resolved, string report) Resolve(string path, string find, string replace)
        {
            var report = new StringBuilder();
            string content = File.ReadAllText(path);
            var resolved = PatchEngine.ResolvePairs(new List<(string, string)> { (find, replace) },
                path, Path.GetFileName(path), content, new UTF8Encoding(false), (t, _) => report.Append(t));
            return (resolved, report.ToString());
        }

        private string Backups => Path.Combine(_dir, "bak");

        // ── H-05: merge modes ────────────────────────────────────────────────────

        [Fact]
        public void Merge_NoBase_IsAFallback()
        {
            var r = ThreeWayMergeCheck.CheckAndMerge(null, "new", "old");
            Assert.Equal(MergeMode.NoBase, r.Mode);
            Assert.True(r.UsedFallback);
            Assert.Equal("new", r.MergedText);
            Assert.Equal(MergeReport.NoBaseLabel, MergeReport.TranscriptLabel(r));
        }

        [Fact]
        public void Merge_BaseEqualsCurrent_IsClean_NotAFallback_NoLabel()
        {
            var r = ThreeWayMergeCheck.CheckAndMerge("a\nb\n", "a\nB\n", "a\r\nb\r\n");
            Assert.Equal(MergeMode.CleanNoDivergence, r.Mode);
            Assert.False(r.UsedFallback);
            Assert.Equal("a\nB\n", r.MergedText);
            Assert.Equal("", MergeReport.TranscriptLabel(r));
        }

        [Fact]
        public void Merge_RealDivergence_IsAThreeWayMerge()
        {
            // H-69: CreateMerge used to get chunker: null, which DiffPlex 1.9.0 rejects, so every
            // real divergence took the proposed text unmerged. With the line chunker it merges.
            var r = ThreeWayMergeCheck.CheckAndMerge("a\nb\nc\n", "A\nb\nc\n", "a\nb\nC\n");
            Assert.Equal(MergeMode.ThreeWay, r.Mode);
            Assert.Null(r.DiffEngineError);
            Assert.Equal("A\nb\nC\n", r.MergedText);
            Assert.Equal("", MergeReport.TranscriptLabel(r));
        }

        [Fact]
        public void Merge_DiffEngineThrows_IsAFallback_WithTheExceptionType()
        {
            ThreeWayMergeCheck.DiffEngineHookForTest = () => throw new InvalidOperationException("boom");
            var r = ThreeWayMergeCheck.CheckAndMerge("a\nb\n", "A\nb\n", "a\nB\n");
            Assert.Equal(MergeMode.DiffEngineFailed, r.Mode);
            Assert.True(r.UsedFallback);
            Assert.Equal("InvalidOperationException", r.DiffEngineError);
            Assert.Equal("A\nb\n", r.MergedText);
            Assert.Equal(MergeReport.DiffEngineFailedLabel, MergeReport.TranscriptLabel(r));
        }

        [Fact]
        public void MergeReport_TheTraceEventNameIsAConstant()
        {
            Assert.Equal("merge_fallback", MergeReport.FallbackTraceEvent);
        }

        // ── H-05: what the host prints ───────────────────────────────────────────

        [Fact]
        public async Task Host_ACleanPatch_PrintsNoLabel()
        {
            var output = new StringBuilder();
            IAgenticHost host = new BufferedAgenticHost(_dir, outputSink: (t, _) => output.Append(t));
            Assert.NotNull(await host.SaveFileAsync("clean.txt", "one\ntwo\nthree\n", fromToolCall: true));
            string path = Path.Combine(_dir, "clean.txt");

            var (resolved, report) = Resolve(path, "two", "TWO");
            Assert.True(resolved != null, report);
            var (written, error) = await host.ApplyResolvedPatchAsync(resolved!);

            Assert.Null(error);
            Assert.Equal(path, written);
            Assert.Contains($"[PATCH] Applied to {path}\n", output.ToString());
            Assert.DoesNotContain("fallback", output.ToString());
            Assert.DoesNotContain("[no base", output.ToString());
            ((BufferedAgenticHost)host).DrainPatchBackups();
        }

        [Fact]
        public async Task Host_ASaveOverAFileItNeverRead_PrintsTheNoBaseLabel()
        {
            string path = Write("unread.txt", "old\n");
            var output = new StringBuilder();
            IAgenticHost host = new BufferedAgenticHost(_dir, outputSink: (t, _) => output.Append(t));

            Assert.NotNull(await host.SaveFileAsync("unread.txt", "new\n", fromToolCall: true));

            Assert.Contains("[FILE] Saved unread.txt (2 lines) [no base: overwrite check only]", output.ToString());
            Assert.Equal("new\n", File.ReadAllText(path));
        }

        [Fact]
        public async Task Host_ASecondCleanSave_PrintsNoLabel()
        {
            var output = new StringBuilder();
            IAgenticHost host = new BufferedAgenticHost(_dir, outputSink: (t, _) => output.Append(t));
            Assert.NotNull(await host.SaveFileAsync("twice.txt", "v1\n", fromToolCall: true));
            output.Clear();

            Assert.NotNull(await host.SaveFileAsync("twice.txt", "v2\n", fromToolCall: true));

            Assert.Contains("[FILE] Saved twice.txt (2 lines)\n", output.ToString());
        }

        [Fact]
        public async Task Host_AForcedDiffEngineFailure_PrintsItsLabel()
        {
            var output = new StringBuilder();
            IAgenticHost host = new BufferedAgenticHost(_dir, outputSink: (t, _) => output.Append(t));
            Assert.NotNull(await host.SaveFileAsync("diverged.txt", "a\nb\nc\n", fromToolCall: true));
            File.WriteAllText(Path.Combine(_dir, "diverged.txt"), "a\nb\nC\n");   // someone else changed it
            output.Clear();

            ThreeWayMergeCheck.DiffEngineHookForTest = () => throw new InvalidOperationException("boom");
            Assert.NotNull(await host.SaveFileAsync("diverged.txt", "A\nb\nc\n", fromToolCall: true));

            Assert.Contains("[merge engine failed: proposed text accepted]", output.ToString());
        }

        // ── H-04: the syntax gate ────────────────────────────────────────────────

        private const string AdminUiPagesTests =
            "using Xunit;\n" +
            "\n" +
            "public class AdminUiPagesTests\n" +
            "{\n" +
            "    [Fact]\n" +
            "    public async Task DetailPage_ContainsMetadataAndImage()\n" +
            "    {\n" +
            "        var html = await Get(\"/admin/detail/1\");\n" +
            "        Assert.Contains(\"metadata\", html);\n" +
            "        Assert.Contains(\"<img\", html);\n" +
            "    }\n" +
            "\n" +
            "    [Fact]\n" +
            "    public async Task ImagePage_HasAltText()\n" +
            "    {\n" +
            "        var html = await Get(\"/admin/image/1\");\n" +
            "        Assert.Contains(\"alt=\", html);\n" +
            "    }\n" +
            "}\n";

        [Fact]
        public void H04_Job1652Replay_AnInsertInsideAMethodThatLeavesItUnterminated_IsRefused_FileUnchanged()
        {
            string path = Write("AdminUiPagesTests.cs", AdminUiPagesTests);
            // The insert meant to go between the two tests lands after a statement INSIDE the first.
            var (resolved, report) = Resolve(path,
                "        Assert.Contains(\"metadata\", html);",
                "        Assert.Contains(\"metadata\", html);\n" +
                "\n" +
                "    [Fact]\n" +
                "    public async Task DetailPage_ShowsCaption()\n" +
                "    {\n" +
                "        var html = await Get(\"/admin/detail/2\");\n" +
                "        Assert.Contains(\"caption\", html);");
            Assert.True(resolved != null, report);

            var result = PatchEngine.ApplyPatch(resolved!, Backups);

            Assert.False(result.Success);
            Assert.True(result.Rejected);
            Assert.StartsWith(CSharpSyntaxGate.Marker, result.Error);
            // The parser reads the stray method as a local function: CS0106 ('public' is not valid
            // for this item) at the inserted declaration — the first NEW error, by position.
            Assert.Contains("new syntax error CS0106 at line 12:5", result.Error);                       // } expected
            Assert.Matches(@"at line \d+:\d+", result.Error);
            Assert.Contains("re-read the region and patch again", result.Error);
            Assert.Equal(AdminUiPagesTests, File.ReadAllText(path));
        }

        [Fact]
        public void H04_ACorrectInsertBetweenTheMethods_IsWritten()
        {
            string path = Write("AdminUiPagesTests.cs", AdminUiPagesTests);
            var (resolved, report) = Resolve(path,
                "    [Fact]\n    public async Task ImagePage_HasAltText()",
                "    [Fact]\n" +
                "    public async Task DetailPage_ShowsCaption()\n" +
                "    {\n" +
                "        var html = await Get(\"/admin/detail/2\");\n" +
                "        Assert.Contains(\"caption\", html);\n" +
                "    }\n" +
                "\n" +
                "    [Fact]\n    public async Task ImagePage_HasAltText()");
            Assert.True(resolved != null, report);

            var result = PatchEngine.ApplyPatch(resolved!, Backups);

            Assert.True(result.Success, result.Error);
            Assert.Contains("DetailPage_ShowsCaption", File.ReadAllText(path));
            Assert.Null(CSharpSyntaxGate.Check(path, null, File.ReadAllText(path)));
        }

        [Fact]
        public void H04_AFileAlreadyBroken_PatchedElsewhereWithoutNewErrors_IsWritten()
        {
            // Mid-edit: the second method is already missing its closing brace.
            string broken = AdminUiPagesTests.Replace("        Assert.Contains(\"alt=\", html);\n    }\n", "        Assert.Contains(\"alt=\", html);\n");
            Assert.NotNull(CSharpSyntaxGate.Check("x.cs", null, broken));     // it really is broken
            string path = Write("AdminUiPagesTests.cs", broken);

            var (resolved, report) = Resolve(path, "Assert.Contains(\"metadata\", html);", "Assert.Contains(\"meta\", html);");
            Assert.True(resolved != null, report);
            var result = PatchEngine.ApplyPatch(resolved!, Backups);

            Assert.True(result.Success, result.Error);
            Assert.Contains("\"meta\"", File.ReadAllText(path));
        }

        [Fact]
        public async Task H04_WriteFileOverAnExistingCsFile_WithASyntaxError_IsRefused_FileUnchanged()
        {
            string path = Write("Widget.cs", "public class Widget { public int X => 1; }\n");
            IAgenticHost host = new BufferedAgenticHost(_dir);

            var ex = await Assert.ThrowsAsync<CSharpSyntaxGateException>(() =>
                host.SaveFileAsync("Widget.cs", "public class Widget { public int X => 1;\n", fromToolCall: true));

            Assert.StartsWith(CSharpSyntaxGate.Marker, ex.Message);
            Assert.Equal("public class Widget { public int X => 1; }\n", File.ReadAllText(path));
        }

        [Fact]
        public async Task H04_CreateFile_WithASyntaxError_IsRefused_NothingCreated()
        {
            IAgenticHost host = new BufferedAgenticHost(_dir);
            await Assert.ThrowsAsync<CSharpSyntaxGateException>(() =>
                host.SaveFileAsync("Fresh.cs", "public class Fresh {\n", fromToolCall: true));
            Assert.False(File.Exists(Path.Combine(_dir, "Fresh.cs")));

            Assert.NotNull(await host.SaveFileAsync("Fine.cs", "public class Fine { }\n", fromToolCall: true));
        }

        [Fact]
        public async Task H04_AppendFile_ThatBreaksTheFile_IsRefused_FileUnchanged()
        {
            string original = "public class A { }\n";
            string path = Write("A.cs", original);
            IAgenticHost host = new BufferedAgenticHost(_dir);

            await Assert.ThrowsAsync<CSharpSyntaxGateException>(() => host.AppendFileAsync("A.cs", "public class B {\n"));
            Assert.Equal(original, File.ReadAllText(path));

            Assert.NotNull(await host.AppendFileAsync("A.cs", "public class B { }\n"));
        }

        [Fact]
        public void H04_TheToolResultTheModelReads_NamesTheRefusal_AndSaysTheFileDidNotChange()
        {
            var result = new ExecutionResult();
            result.Errors.Add(CSharpSyntaxGate.Check("Widget.cs", "class W { }", "class W {")!);
            var tc = new ToolCallResult { Name = "create_file", Arguments = new Dictionary<string, string> { ["filename"] = "Widget.cs" } };

            string content = LoopHelpers.BuildToolResultContent(tc, result, null);

            Assert.Contains("[CREATE_FILE FAILED:", content);
            Assert.Contains(CSharpSyntaxGate.Marker, content);
            Assert.Contains("The file on disk was NOT changed.", content);
            Assert.DoesNotContain("does NOT exist", content);
        }

        [Theory]
        [InlineData("DevMind.Core/BufferedAgenticHost.cs")]
        [InlineData("DevMind.TUI/TuiAgenticHost.cs")]
        public void H04_BothHosts_GateEveryWritePath_AndLetTheRefusalThrough(string relativePath)
        {
            // The gate is a Core helper the hosts call: save (new + existing) and append (new +
            // existing) in each host; patches go through PatchEngine.ApplyPatch, shared by both.
            var root = new DirectoryInfo(AppContext.BaseDirectory);
            while (root != null && !File.Exists(Path.Combine(root.FullName, "DevMind.slnx"))) root = root.Parent;
            Assert.True(root != null, "repo root (DevMind.slnx) not found above the test binaries");
            string source = File.ReadAllText(Path.Combine(root!.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar)));

            Assert.Equal(4, System.Text.RegularExpressions.Regex.Matches(source, @"CSharpSyntaxGate\.Enforce\(").Count);
            Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(source, @"catch \(CSharpSyntaxGateException\)").Count);
            Assert.Contains("result.NotLanded || result.Rejected", source);
        }

        [Theory]
        [InlineData("notes.txt")]
        [InlineData("app.js")]
        [InlineData("Page.cshtml")]
        [InlineData("Comp.razor")]
        public void H04_NonCsFilesAreNeverParsed(string name)
        {
            Assert.False(CSharpSyntaxGate.Applies(name));
            Assert.Null(CSharpSyntaxGate.Check(name, "ok", "class {{{ broken"));
        }

        [Fact]
        public async Task H04_ANonCsFileWithBrokenBraces_IsWrittenAsBefore()
        {
            IAgenticHost host = new BufferedAgenticHost(_dir);
            Assert.NotNull(await host.SaveFileAsync("app.js", "function f() {\n", fromToolCall: true));
        }

        // ── H-67 ─────────────────────────────────────────────────────────────────

        private const string LogMessages =
            "using Microsoft.Extensions.Logging;\n" +
            "\n" +
            "internal static partial class Log\n" +
            "{\n" +
            "    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = \"Sanitized {Path}\")]\n" +
            "    public static partial void Sanitized(ILogger logger, string path);\n" +
            "\n" +
            "    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = \"Skipped {Path}\")]\n" +
            "    public static partial void Skipped(ILogger logger, string path);\n" +
            "}\n";

        [Fact]
        public void H67_Job2143Replay_AnAttributeEditThatLeavesCloseParenBracketBracket_IsRefused()
        {
            string path = Write("Log.cs", LogMessages);
            // The FIND stops before the attribute's "]" and the REPLACE closes it again: ")]]".
            var (resolved, report) = Resolve(path,
                "[LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = \"Skipped {Path}\")",
                "[LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = \"Skipped {Path}: {Reason}\")]");
            Assert.True(resolved != null, report);
            string wouldBe = LogMessages.Substring(0, resolved!.ResolvedBlocks[0].origStart) + resolved.ResolvedBlocks[0].replaceText +
                             LogMessages.Substring(resolved.ResolvedBlocks[0].origEnd);
            Assert.Contains("{Reason}\")]]", wouldBe);                         // the job-2143 damage

            var result = PatchEngine.ApplyPatch(resolved, Backups);

            Assert.False(result.Success);
            Assert.True(result.Rejected);
            Assert.StartsWith(CSharpSyntaxGate.Marker, result.Error);
            Assert.Equal(LogMessages, File.ReadAllText(path));
        }

        private const string MainWindowHeader =
            "// File: MainWindow.xaml.cs\n" +
            "// v1.4.3 — added the export button\n" +
            "// v1.4.2 — fixed resize on high DPI\n" +
            "// v1.4.1 — initial window\n" +
            "\n" +
            "namespace App { public partial class MainWindow { } }\n";

        [Fact]
        public void H67_Job2146Replay_AFuzzyPatchThatDuplicatesTheNewestHeaderLine_IsRefused()
        {
            string path = Write("MainWindow.xaml.cs", MainWindowHeader);
            // A second patch with a stale, slightly-off FIND: the fuzzy window lands on v1.4.2 and the
            // REPLACE re-adds v1.4.3 above it — the newest header line twice in a row.
            var (resolved, report) = Resolve(path,
                "// v1.4.2 — fixed resize on high-DPI",
                "// v1.4.3 — added the export button\n// v1.4.2 — fixed resize on high-DPI");

            Assert.Null(resolved);
            Assert.Contains("refused", report);
            Assert.Contains("v1.4.3 — added the export button", report);
            Assert.Contains("twice in a row", report);
            Assert.Contains("Fuzzy candidate", report);
            Assert.Contains("exact FIND", report);
            Assert.Equal(MainWindowHeader, File.ReadAllText(path));
        }

        [Fact]
        public void H67_AFuzzyApplyThatOnlyDriftsInWhitespace_IsWritten()
        {
            string original =
                "public static class Totals\n" +
                "{\n" +
                "    public static int Compute(int a, int b) => Sum( a, b ) + Offset( a );\n" +
                "    private static int Offset(int a) => a;\n" +
                "}\n";
            string path = Write("Totals.cs", original);
            var (resolved, report) = Resolve(path,
                "public static int Compute(int a, int b) => Sum(a,b) + Offset(a);",
                "public static int Compute(int a, int b) => Sum(a,b) * 2 + Offset(a);");

            Assert.True(resolved != null, report);
            Assert.Equal(PatchConfidence.Fuzzy, resolved!.Confidence);
            var result = PatchEngine.ApplyPatch(resolved, Backups);
            Assert.True(result.Success, result.Error);
            Assert.Contains("Sum(a,b) * 2 + Offset(a);", File.ReadAllText(path));
        }

        [Fact]
        public void H67_AFuzzyFindEchoedWithItsTypoInReplace_IsRefused()
        {
            // The FIND's context line differs from the file in a real character; echoing it in the
            // REPLACE would rewrite that line with the model's wrong version.
            string original = "int width = Measure(item);\nint height = Measure(row);\nreturn width;\n";
            string path = Write("Layout.txt", original);
            var (resolved, report) = Resolve(path,
                "int width = Measure(itme);",
                "int width = Measure(itme);\nint depth = 0;");
            Assert.Null(resolved);
            Assert.Contains("refused", report);
            Assert.Contains("the FIND line \"int width = Measure(itme);\" is not in the file as written", report);
        }

        [Theory]
        [InlineData("MainWindow.xaml", "<Grid>\n    <Button Content=\"Export\" Click=\"OnExport\" />\n</Grid>\n", "<Button Content=\"Exprot\" Click=\"OnExport\" />")]
        [InlineData("App.csproj", "<Project>\n  <PropertyGroup>\n    <TargetFramework>net10.0</TargetFramework>\n  </PropertyGroup>\n</Project>\n", "<TargetFramework>net10.O</TargetFramework>")]
        [InlineData("appsettings.json", "{\n  \"Logging\": { \"LogLevel\": { \"Default\": \"Information\" } }\n}\n", "\"Logging\": { \"LogLevel\": { \"Default\": \"Informaton\" } }")]
        public void H67_ANonExactMatchOnAStructuredFile_IsAnErrorWithTheCandidate_FileUnchanged(string name, string content, string find)
        {
            string path = Write(name, content);
            var (resolved, report) = Resolve(path, find, "REPLACED");

            Assert.Null(resolved);
            Assert.Contains("Fuzzy matching is disabled for this file type", report);
            Assert.Matches(@"Closest candidate: line \d+ \(\d+% similarity\)", report);
            Assert.Contains("exact FIND", report);
            Assert.Equal(content, File.ReadAllText(path));
        }

        [Fact]
        public void H67_AnExactMatchWins_OverANearIdenticalRunnerUp()
        {
            // Sep 2: an exact match was refused because a 99% runner-up sat one line away. The exact
            // path runs first and never consults the fuzzy scorer, so the exact line is the one edited.
            string original = "    Log(\"v1.2.3 - stable\");\n    Log(\"v1.2.4 - stable\");\n";
            string path = Write("Versions.txt", original);
            var (resolved, report) = Resolve(path, "Log(\"v1.2.4 - stable\");", "Log(\"v1.2.5 - stable\");");

            Assert.True(resolved != null, report);
            Assert.Equal(PatchConfidence.Exact, resolved!.Confidence);
            Assert.True(PatchEngine.ApplyPatch(resolved, Backups).Success);
            Assert.Equal("    Log(\"v1.2.3 - stable\");\n    Log(\"v1.2.5 - stable\");\n", File.ReadAllText(path));
        }
    }
}
