// File: PatchEngineIndentationTests.cs  v1.0
//
// Regression tests for silent indentation loss in ResolvePairs (found 2026-08-01
// via DevMindTestBed experiment job-398): when the model sends find/replace text
// without leading whitespace, normalized matching still hits, the walk-back
// swallows the line's real indentation into the replaced span, and the
// unindented replacement deletes it -- reported as success.

using System;
using System.Collections.Generic;
using System.Text;
using Xunit;

namespace DevMind.Core.Tests
{
    public class PatchEngineIndentationTests
    {
        private static string Apply(string content, List<(string find, string repl)> pairs)
        {
            var result = PatchEngine.ResolvePairs(
                pairs, @"C:\fake\Target.cs", "Target.cs", content,
                new UTF8Encoding(false), (_, _) => { });
            Assert.NotNull(result);
            // Apply spans back-to-front so offsets stay valid.
            var blocks = result.ResolvedBlocks;
            blocks.Sort((a, b) => b.origStart.CompareTo(a.origStart));
            string updated = content;
            foreach (var (start, end, repl) in blocks)
                updated = updated.Remove(start, end - start).Insert(start, repl);
            return updated;
        }

        [Fact]
        public void Exact_UnindentedFindReplace_PreservesIndentation()
        {
            string content =
                "namespace X\n" +
                "{\n" +
                "        Console.WriteLine($\"[GREP] no matches for \\\"{pattern}\\\"\");\n" +
                "}\n";

            string updated = Apply(content, new List<(string, string)>
            {
                // Model omits the leading indentation on both sides -- the shape
                // observed in job-398.
                ("Console.WriteLine($\"[GREP] no matches for \\\"{pattern}\\\"\");",
                 "Console.WriteLine($\"[GREP-MISS] no matches for \\\"{pattern}\\\"\");"),
            });

            Assert.Contains(
                "        Console.WriteLine($\"[GREP-MISS] no matches for \\\"{pattern}\\\"\");",
                updated);
        }

        [Fact]
        public void Exact_ReplacementWithOwnIndent_NotDoublePrefixed()
        {
            string content =
                "namespace X\n" +
                "{\n" +
                "    int a = 1;\n" +
                "}\n";

            string updated = Apply(content, new List<(string, string)>
            {
                ("    int a = 1;", "    int a = 2;"),
            });

            Assert.Contains("\n    int a = 2;\n", updated);
            Assert.DoesNotContain("        int a = 2;", updated);
        }

        [Fact]
        public void Exact_MidLineMatch_PrefixUntouched()
        {
            string content =
                "namespace X\n" +
                "{\n" +
                "    var label = prefix + \"OLD\";\n" +
                "}\n";

            string updated = Apply(content, new List<(string, string)>
            {
                ("+ \"OLD\";", "+ \"NEW\";"),
            });

            Assert.Contains("    var label = prefix + \"NEW\";", updated);
        }

        [Fact]
        public void Fuzzy_UnindentedReplacement_PreservesIndentation()
        {
            string content =
                "namespace X\n" +
                "{\n" +
                "        var total = alpha + beta + gamma + delta;\n" +
                "}\n";

            // One character off forces the fuzzy path; replacement is unindented.
            string updated = Apply(content, new List<(string, string)>
            {
                ("var total = alpha + beta + gamma + deltaa;",
                 "var total = alpha + beta + gamma + delta + epsilon;"),
            });

            Assert.Contains(
                "        var total = alpha + beta + gamma + delta + epsilon;",
                updated);
        }
    }
}
