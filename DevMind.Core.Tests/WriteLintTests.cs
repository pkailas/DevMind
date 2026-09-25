// File: WriteLintTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// H-19: the xUnit "message argument" trap, caught where it is written and where it breaks.
//
// xUnit v2/v3 have no Assert.Equal(expected, actual, "message") overload. The string binds to
// a comparer or other overload and the build fails with CS1503 / CS1929; jobs 1660, 1668 and
// 1683 each spent iterations on it, some misdiagnosing a "stale build". The write-time lint
// names it on the create/append/patch result; the build hint names it on the build output.
//
// Negatives matter as much as positives: a lint that fires on correct code teaches the agent
// to skip [LINT] lines, which is worse than the trap it was meant to catch.

using Xunit;

namespace DevMind.Core.Tests
{
    public class WriteLintTests : IDisposable
    {
        private readonly string _dir;

        public WriteLintTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), $"devmind_lint_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch { }
        }

        private static string TestFile(string body) =>
            "using Xunit;\n\npublic class FooTests\n{\n    [Fact]\n    public void T()\n    {\n" +
            body + "\n    }\n}\n";

        private static IReadOnlyList<string> Lint(string path, string content)
            => WriteLint.Check(path, content, null);

        // ── Layer 1: which calls fire ────────────────────────────────────────────

        [Theory]
        [InlineData("        Assert.Equal(1, x, \"count\");", "Assert.Equal(..., \"count\")")]
        [InlineData("        Assert.Contains(\"a\", s, $\"ctx {y}\");", "Assert.Contains(..., $\"ctx {y}\")")]
        [InlineData("        Assert.NotEqual(a, b, \"must differ\");", "Assert.NotEqual(..., \"must differ\")")]
        [InlineData("        Assert.Empty(list, \"should be empty\");", "Assert.Empty(..., \"should be empty\")")]
        [InlineData("        Assert.Equal<int>(1, x, @\"verbatim note\");", "Assert.Equal(..., @\"verbatim note\")")]
        [InlineData("        Assert.StartsWith(\"ab\", s, \"prefix\");", "Assert.StartsWith(..., \"prefix\")")]
        public void AMessageArgumentIsFlaggedWithItsLine(string call, string snippet)
        {
            IReadOnlyList<string> notes = Lint(@"C:\repo\tests\FooTests.cs", TestFile(call));

            string note = Assert.Single(notes);
            Assert.StartsWith("[LINT] xUnit: " + snippet + " at line 8 - xUnit has no message overload", note);
            Assert.EndsWith("use Assert.True(<condition>, \"message\") or put the note in a comment.", note);
        }

        [Theory]
        [InlineData("        Assert.True(ok, \"msg\");")]
        [InlineData("        Assert.False(bad, \"msg with spaces\");")]
        [InlineData("        Assert.Equal(\"expected text\", actual);")]
        [InlineData("        Assert.Equal(a, b, StringComparer.Ordinal);")]
        [InlineData("        Assert.Equal(expected, actual, ignoreCase: true);")]
        [InlineData("        Assert.Contains(\"needle, with a comma\", haystack);")]
        [InlineData("        Assert.Contains(\"a\", s, StringComparison.Ordinal);")]
        [InlineData("        Assert.Equal(1.0, x, 3);")]
        [InlineData("        Assert.Equal(f(\"x\", \"y\"), g(1, \"z\"));")]
        [InlineData("        Assert.Empty(list);")]
        [InlineData("        Assert.Equal(1, x, \"a\" + suffix);")]
        [InlineData("        // Assert.Equal(1, x, \"commented out\");")]
        [InlineData("        /* Assert.Equal(1, x, \"block comment\"); */")]
        [InlineData("        string code = \"Assert.Equal(1, x, \\\"quoted in a string\\\");\";")]
        [InlineData("        string code = @\"Assert.Equal(1, x, \"\"verbatim\"\");\";")]
        [InlineData("        MyAssert.Equal(1, x, \"a custom helper may take one\");")]
        public void CorrectCallsAreLeftAlone(string call)
        {
            Assert.Empty(Lint(@"C:\repo\tests\FooTests.cs", TestFile(call)));
        }

        [Fact]
        public void ANonTestFileWithTheSameTextIsLeftAlone()
        {
            // Not *Test*.cs and no `using Xunit;` — someone else's Assert class, or plain text.
            string content = "public class Calc\n{\n    void M() { Assert.Equal(1, x, \"count\"); }\n}\n";

            Assert.Empty(Lint(@"C:\repo\src\Calc.cs", content));
            Assert.Empty(Lint(@"C:\repo\docs\FooTests.md", content));
        }

        [Fact]
        public void AFileThatUsesXunitIsLintedWhateverItsName()
        {
            string content = TestFile("        Assert.Equal(1, x, \"count\");");

            Assert.Single(Lint(@"C:\repo\tests\Helpers.cs", content));
        }

        [Fact]
        public void AMultiLineCallIsReadToItsClosingParen()
        {
            string content = TestFile("        Assert.Equal(\n            expected,\n            actual,\n            \"spans lines\");");

            string note = Assert.Single(Lint(@"C:\repo\tests\FooTests.cs", content));
            Assert.Contains("at line 8", note);
        }

        [Fact]
        public void SeveralHitsInOneFileMakeOneLine()
        {
            string content = TestFile(
                "        Assert.Equal(1, x, \"one\");\n        Assert.NotEqual(2, y, \"two\");\n        Assert.Contains(\"c\", z, \"three\");");

            string note = Assert.Single(Lint(@"C:\repo\tests\FooTests.cs", content));
            Assert.Contains("Assert.Equal(..., \"one\") at line 8 (also lines 9, 10)", note);
        }

        [Fact]
        public void OnlyChangedLinesAreLinted()
        {
            // A trap already in the file is not this write's doing, and re-reporting it on every
            // patch would bury the one that is.
            string before = TestFile("        Assert.Equal(1, x, \"old\");");
            string after = TestFile("        Assert.Equal(1, x, \"old\");\n        Assert.Equal(2, y, \"new\");");

            ISet<int> changed = WriteLint.ChangedLines(before, after);
            string note = Assert.Single(WriteLint.Check(@"C:\repo\tests\FooTests.cs", after, changed));

            Assert.Contains("\"new\") at line 9", note);
            Assert.DoesNotContain("old", note);
            Assert.Empty(WriteLint.Check(@"C:\repo\tests\FooTests.cs", before, WriteLint.ChangedLines(before, before)));
        }

        [Fact]
        public void TheRuleTableCarriesTheXunitRule()
        {
            LintRule rule = Assert.Single(WriteLint.Rules);
            Assert.Equal("xunit-message-argument", rule.Id);
            Assert.NotNull(rule.FileFilter);
            Assert.NotNull(rule.Detector);
            Assert.NotNull(rule.Message);
        }

        // ── Layer 1 end to end: the tool result the model receives ───────────────

        [Fact]
        public async Task CreateFile_ToolResultCarriesTheLintLine()
        {
            string content = TestFile("        Assert.Equal(1, x, \"count\");");

            string toolResult = await RunWriteAsync("create_file", new ResponseBlock
            {
                Type = BlockType.File, FileName = "FooTests.cs", Content = content, FromToolCall = true,
            });

            Assert.StartsWith("[File created: ", toolResult);
            Assert.Contains("\n[LINT] xUnit: Assert.Equal(..., \"count\") at line 8", toolResult);
            // Advisory only: the write happened.
            Assert.Equal(content, File.ReadAllText(Path.Combine(_dir, "FooTests.cs")));
        }

        [Fact]
        public async Task CreateFile_CleanTestFile_ToolResultIsUnchanged()
        {
            string toolResult = await RunWriteAsync("create_file", new ResponseBlock
            {
                Type = BlockType.File, FileName = "FooTests.cs",
                Content = TestFile("        Assert.True(ok, \"msg\");"), FromToolCall = true,
            });

            Assert.Equal($"[File created: {Path.Combine(_dir, "FooTests.cs")}]", toolResult);
        }

        [Fact]
        public async Task AppendFile_LintsTheAppendedLinesAtTheirFileLineNumbers()
        {
            string path = Path.Combine(_dir, "BarTests.cs");
            File.WriteAllText(path, "using Xunit;\n// line 2\n// line 3 has Assert.Equal(1, x, \"in a comment\")\n");

            string toolResult = await RunWriteAsync("append_file", new ResponseBlock
            {
                Type = BlockType.AppendFile, FileName = "BarTests.cs",
                Content = "public class B { void M() {\n    Assert.Equal(1, x, \"appended\");\n} }\n", FromToolCall = true,
            });

            Assert.StartsWith("[Content appended to ", toolResult);
            Assert.Contains("[LINT] xUnit: Assert.Equal(..., \"appended\") at line 5", toolResult);
        }

        [Fact]
        public async Task PatchFile_LintsOnlyTheReplacedLines()
        {
            string path = Path.Combine(_dir, "BazTests.cs");
            File.WriteAllText(path, TestFile("        Assert.Equal(1, x, \"pre-existing\");\n        int marker = 0;"));

            string toolResult = await RunWriteAsync("patch_file", new ResponseBlock
            {
                Type = BlockType.Patch, FileName = "BazTests.cs", FromToolCall = true,
                Content = "PATCH BazTests.cs\nFIND:\n        int marker = 0;\nREPLACE:\n        int marker = 0;\n        Assert.NotEqual(a, b, \"patched in\");\nEND_PATCH",
            });

            Assert.StartsWith("[PATCH applied to ", toolResult);
            Assert.Contains("[LINT] xUnit: Assert.NotEqual(..., \"patched in\") at line 10", toolResult);
            Assert.DoesNotContain("pre-existing", toolResult.Substring(toolResult.IndexOf("[LINT]", StringComparison.Ordinal)));
        }

        private async Task<string> RunWriteAsync(string toolName, ResponseBlock block)
        {
            var host = new BufferedAgenticHost(_dir) { RestrictWritesToWorkingDirectory = true };
            var executor = new AgenticExecutor(host, new FakeLlmOptions());
            executor.SetCancellationToken(CancellationToken.None);

            ExecutionResult result = await executor.ExecuteAsync(
                new AgenticAction { Type = ActionType.ApplyAndBuild },
                new ResponseOutcome(new List<ResponseBlock> { block }));

            return LoopHelpers.BuildToolResultContent(
                new ToolCallResult
                {
                    Id = "call-lint", Name = toolName,
                    Arguments = new Dictionary<string, string> { ["filename"] = block.FileName },
                },
                result,
                new List<ResponseBlock> { block });
        }

        // ── Layer 2: build-error hint ────────────────────────────────────────────

        private const string Hint =
            "[HINT] CS1503/CS1929 on an Assert.* call usually means an xUnit message argument - xUnit has no " +
            "message overload; use Assert.True(cond, \"message\").";

        private string WriteSource()
        {
            string path = Path.Combine(_dir, "QuxTests.cs");
            File.WriteAllText(path,
                "using Xunit;\npublic class QuxTests {\n    [Fact] public void T() {\n" +
                "        Assert.Equal(1, x, \"count\");\n" +      // line 4
                "        Frobnicate(1, \"two\");\n" +             // line 5
                "    }\n}\n");
            return path;
        }

        [Fact]
        public void CS1503OnAnAssertLine_AppendsTheHintOnce()
        {
            string path = WriteSource();
            string output =
                "  Determining projects to restore...\n" +
                $"{path}(4,28): error CS1503: Argument 3: cannot convert from 'string' to 'System.Collections.Generic.IEqualityComparer<int>' [C:\\repo\\tests\\Qux.csproj]\n" +
                $"{path}(4,28): error CS1503: Argument 3: cannot convert from 'string' to 'System.Collections.Generic.IEqualityComparer<int>' [C:\\repo\\tests\\Qux.csproj]\n" +
                $"{path}(4,9): error CS1929: 'int' does not contain a definition for ... [C:\\repo\\tests\\Qux.csproj]\n" +
                "Build FAILED.\n";

            string annotated = BuildErrorHints.Annotate(output, _dir);

            Assert.StartsWith(output, annotated);
            Assert.Equal(output + Hint + "\n", annotated);
        }

        [Fact]
        public void CS1503OnANonAssertLine_AddsNoHint()
        {
            string path = WriteSource();
            string output = $"{path}(5,23): error CS1503: Argument 2: cannot convert from 'string' to 'int' [C:\\repo\\tests\\Qux.csproj]\n";

            Assert.Equal(output, BuildErrorHints.Annotate(output, _dir));
        }

        [Fact]
        public void ARelativePathResolvesAgainstTheWorkingDirectory()
        {
            WriteSource();
            string output = "QuxTests.cs(4,28): error CS1503: Argument 3: cannot convert from 'string' to 'IEqualityComparer<int>'\n";

            Assert.EndsWith(Hint + "\n", BuildErrorHints.Annotate(output, _dir));
        }

        [Fact]
        public void OtherCodesAndMissingFilesAddNoHint()
        {
            string path = WriteSource();
            string otherCode = $"{path}(4,28): error CS0103: The name 'x' does not exist in the current context\n";
            string missing = $"{Path.Combine(_dir, "Gone.cs")}(4,28): error CS1503: Argument 3: cannot convert\n";

            Assert.Equal(otherCode, BuildErrorHints.Annotate(otherCode, _dir));
            Assert.Equal(missing, BuildErrorHints.Annotate(missing, _dir));
            Assert.Equal("Build succeeded.\n", BuildErrorHints.Annotate("Build succeeded.\n", _dir));
        }

        [Fact]
        public async Task RunBuild_ToolResultCarriesTheHint()
        {
            // End to end through the executor's shell path: the output the model reads for
            // run_build is ShellOutput, and that is where the hint must land.
            string path = WriteSource();
            string buildOutput = $"{path}(4,28): error CS1503: Argument 3: cannot convert from 'string' to 'IEqualityComparer<int>'\nBuild FAILED.\n";
            var host = new FakeHost(_dir);
            host.ShellResults["dotnet build"] = (1, buildOutput);
            var executor = new AgenticExecutor(host, new FakeLlmOptions());
            executor.SetCancellationToken(CancellationToken.None);

            ExecutionResult result = await executor.ExecuteAsync(
                new AgenticAction { Type = ActionType.ApplyAndBuild },
                new ResponseOutcome(new List<ResponseBlock> { new ResponseBlock { Type = BlockType.Shell, Command = "dotnet build" } }));

            string toolResult = LoopHelpers.BuildToolResultContent(
                new ToolCallResult { Id = "call-build", Name = "run_build", Arguments = new Dictionary<string, string>() },
                result, null);
            Assert.Equal(buildOutput + Hint + "\n", toolResult);
        }
    }
}
