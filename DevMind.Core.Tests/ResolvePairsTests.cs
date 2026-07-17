// File: ResolvePairsTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Regression tests for the two field defects fixed in PatchEngine v1.1:
//   A. A FIND matching mid-line swallowed the text before it (origStart walked
//      back to line start unconditionally) — e.g. a suffix match on a one-line
//      enum replaced the entire line, deleting its prefix.
//   B. Structured tool-call edits were serialized into the text PATCH format and
//      re-parsed, so literal directive markers inside content (the word "find:",
//      "SHELL:", "---") truncated or corrupted the edit. Structured edits now go
//      through ResolvePairs and must pass content through verbatim.

using System.Text;
using Xunit;

namespace DevMind.Core.Tests;

public sealed class ResolvePairsTests
{
    private static string ApplyResolved(PatchResolveResult resolved)
    {
        // Mirror ApplyPatch's splice without touching the filesystem.
        resolved.ResolvedBlocks.Sort((a, b) => b.origStart.CompareTo(a.origStart));
        var updated = resolved.OriginalContent;
        foreach (var (origStart, origEnd, finalReplace) in resolved.ResolvedBlocks)
            updated = updated.Substring(0, origStart) + finalReplace + updated.Substring(origEnd);
        return updated;
    }

    private static PatchResolveResult? Resolve(string content, string find, string replace)
    {
        var pairs = new List<(string findText, string replaceText)> { (find, replace) };
        return PatchEngine.ResolvePairs(
            pairs, @"C:\fake\File.cs", "File.cs", content, new UTF8Encoding(false),
            (_, _) => { });
    }

    // ── Defect A: mid-line suffix match must preserve the line's prefix ──────

    [Fact]
    public void MidLineSuffixMatch_PreservesLinePrefix()
    {
        // Field case: single-line enum, FIND matched only the tail of the line.
        string content =
            "namespace X\n" +
            "{\n" +
            " public enum BlockType { Text, File, Patch, QueryLibrary }\n" +
            "}\n";

        var resolved = Resolve(content, "Patch, QueryLibrary }", "Patch, QueryLibrary, NeedsInput }");

        Assert.NotNull(resolved);
        string updated = ApplyResolved(resolved!);
        Assert.Contains("public enum BlockType { Text, File, Patch, QueryLibrary, NeedsInput }", updated);
        // The prefix of the line must survive — the pre-fix engine deleted it.
        Assert.Contains("public enum BlockType {", updated);
    }

    [Fact]
    public void MidLineMatch_AfterCode_DoesNotSwallowPrefix()
    {
        string content = "int a = Compute(1) + Compute(2);\n";

        var resolved = Resolve(content, "Compute(2);", "Compute(3);");

        Assert.NotNull(resolved);
        Assert.Equal("int a = Compute(1) + Compute(3);\n", ApplyResolved(resolved!));
    }

    // ── Defect A guard-rail: indentation is still included on line-start matches ──

    [Fact]
    public void LineStartMatch_StillIncludesLeadingIndentation()
    {
        string content =
            "class C\n" +
            "{\n" +
            "    void M()\n" +
            "    {\n" +
            "        Console.WriteLine(\"Hi\");\n" +
            "    }\n" +
            "}\n";

        // Replacement carries its own indentation, as models copy full lines.
        var resolved = Resolve(content, "Console.WriteLine(\"Hi\");", "        Console.WriteLine(\"Bye\");");

        Assert.NotNull(resolved);
        var (origStart, _, _) = resolved!.ResolvedBlocks[0];
        // origStart must sit at the start of the line (indentation included in the
        // replaced span), exactly as the pre-fix engine behaved for this case.
        Assert.True(origStart == 0 || resolved.OriginalContent[origStart - 1] == '\n');
        Assert.Contains("        Console.WriteLine(\"Bye\");", ApplyResolved(resolved));
        Assert.DoesNotContain("                Console.WriteLine(\"Bye\");", ApplyResolved(resolved));
    }

    // ── Defect B: literal directive markers inside content pass through verbatim ──

    [Fact]
    public void ReplaceText_ContainingDirectiveMarkers_IsPreservedVerbatim()
    {
        // Field case: a replace string containing the literal word "find: " was
        // truncated at it by the text-format round-trip. ResolvePairs must pass
        // content through untouched — including SHELL:, REPLACE:, and "---" lines
        // that the text parser treats as terminators.
        string content = "string prompt = OLD;\n";
        string replace =
            "string prompt = \"and cite what you find: \" +\n" +
            "    \"SHELL: is not a directive here\" +\n" +
            "    \"REPLACE: neither is this\" +\n" +
            "    \"---\";";

        var resolved = Resolve(content, "string prompt = OLD;", replace);

        Assert.NotNull(resolved);
        string updated = ApplyResolved(resolved!);
        Assert.Contains("and cite what you find: ", updated);
        Assert.Contains("SHELL: is not a directive here", updated);
        Assert.Contains("REPLACE: neither is this", updated);
        Assert.Contains("\"---\";", updated);
    }

    [Fact]
    public void FindText_ContainingDirectiveMarkers_MatchesVerbatim()
    {
        string content =
            "var s = \"FIND: literal\";\n" +
            "var t = 1;\n";

        var resolved = Resolve(content, "var s = \"FIND: literal\";", "var s = \"kept\";");

        Assert.NotNull(resolved);
        string updated = ApplyResolved(resolved!);
        Assert.Contains("var s = \"kept\";", updated);
        Assert.DoesNotContain("FIND: literal", updated);
        Assert.Contains("var t = 1;", updated);
    }

    // ── Multi-pair atomicity through ResolvePairs ────────────────────────────

    [Fact]
    public void MultiplePairs_OneUnresolvable_FailsWhole()
    {
        string content = "int a = 1;\nint b = 2;\n";
        var pairs = new List<(string findText, string replaceText)>
        {
            ("int a = 1;", "int a = 10;"),
            ("int z = 9;", "int z = 90;"), // not present
        };

        var resolved = PatchEngine.ResolvePairs(
            pairs, @"C:\fake\File.cs", "File.cs", content, new UTF8Encoding(false),
            (_, _) => { });

        Assert.Null(resolved);
    }
}
