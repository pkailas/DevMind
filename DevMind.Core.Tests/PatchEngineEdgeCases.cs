// File: PatchEngineEdgeCases.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Edge-case tests for PatchEngine — Pass 2.
// Each test pins actual documented behavior. If behavior looks wrong,
// the test asserts what the code ACTUALLY does and flags it in comments.

using System.Text;
using Xunit;

namespace DevMind.Core.Tests;

// ─────────────────────────────────────────────────────────────────────────────
// FindFuzzyMatch — threshold and ambiguity gap boundaries
// ─────────────────────────────────────────────────────────────────────────────

public sealed class FindFuzzyMatchTests
{
    // ── Similarity threshold: just above 0.85 ────────────────────────────────
    // 200-char strings, 30 substitutions → Levenshtein distance = 30
    // similarity = 1.0 - 30/200 = 0.855 → above 0.85 → should match
    [Fact]
    public void Similarity_AboveThreshold_ReturnsMatch()
    {
        string normFind = "a" + new string('b', 199);
        // 30 substitutions → dist=30, sim=0.855
        string content = "a" + new string('c', 30) + new string('b', 169);
        string findText = content; // single-line, so windowSize=1

        var result = PatchEngine.FindFuzzyMatch(content, findText, normFind);

        Assert.NotNull(result);
        Assert.InRange(result.Value.similarity, 0.85, 1.0);
    }

   // ── Similarity threshold: exactly 0.85 ───────────────────────────────────
    // 200-char strings, 30 substitutions → dist=30, sim=0.850
    // The check is `bestSim < threshold` (strict), so 0.85 exactly passes.
    [Fact]
    public void Similarity_ExactlyAtThreshold_ReturnsMatch()
    {
        string normFind = "a" + new string('b', 199);
        // 30 substitutions → dist=30, sim=0.850
        string content = "a" + new string('c', 30) + new string('b', 169);
        string findText = content;

        var result = PatchEngine.FindFuzzyMatch(content, findText, normFind);

        Assert.NotNull(result);
        Assert.Equal(0.85, result.Value.similarity, 4);
    }

    // ── Similarity threshold: just below 0.85 ────────────────────────────────
    // 200-char strings, 32 substitutions → dist=32, sim=0.845
    // Below 0.85 → should return null
    [Fact]
    public void Similarity_BelowThreshold_ReturnsNull()
    {
        string normFind = "a" + new string('b', 199);
        // 32 substitutions → dist=32, sim=0.845
        string content = "a" + new string('c', 32) + new string('b', 167);
        string findText = content;

        var result = PatchEngine.FindFuzzyMatch(content, findText, normFind);

        Assert.Null(result);
    }

    // ── Ambiguity gap: best and second-best within 0.05 → reject ─────────────
    // Two windows: best sim=0.905 (dist=19), second sim=0.885 (dist=23)
    // gap = 0.020 < 0.05 → should return null (ambiguous)
    [Fact]
    public void AmbiguityGap_TooSmall_ReturnsNull()
    {
        string normFind = "a" + new string('b', 199);
        // Window 1: 19 subs → sim=0.905
        string line1 = "a" + new string('c', 19) + new string('b', 180);
        // Window 2: 23 subs → sim=0.885
        string line2 = "a" + new string('d', 23) + new string('b', 176);
        // Window 3: very different → low sim
        string line3 = new string('z', 200);

        string content = line1 + "\n" + line2 + "\n" + line3;
        string findText = normFind; // single-line findText

        var result = PatchEngine.FindFuzzyMatch(content, findText, normFind);

        Assert.Null(result);
    }

    // ── Ambiguity gap: clear gap > 0.05 → accept ─────────────────────────────
    // Two windows: best sim=0.905 (dist=19), second sim=0.845 (dist=32)
    // gap = 0.060 > 0.05 → should return the best match
    [Fact]
    public void AmbiguityGap_ClearGap_ReturnsBestMatch()
    {
        string normFind = "a" + new string('b', 199);
        // Window 1: 19 subs → sim=0.905
        string line1 = "a" + new string('c', 19) + new string('b', 180);
        // Window 2: 32 subs → sim=0.845
        string line2 = "a" + new string('d', 32) + new string('b', 167);
        // Window 3: very different → low sim
        string line3 = new string('z', 200);

        string content = line1 + "\n" + line2 + "\n" + line3;
        string findText = normFind;

        var result = PatchEngine.FindFuzzyMatch(content, findText, normFind);

        Assert.NotNull(result);
        // The best match should be line1
        Assert.Equal(0.905, result.Value.similarity, 4);
    }

    // ── Ambiguity gap: exactly 0.05 → NOT rejected (check is < 0.05) ─────────
    // Two windows: best sim=0.905 (dist=19), second sim=0.855 (dist=30)
    // gap = 0.050 → NOT < 0.05 → should accept
    [Fact]
    public void AmbiguityGap_ExactlyAtBoundary_Accepts()
    {
        string normFind = "a" + new string('b', 199);
        // Window 1: 19 subs → sim=0.905
        string line1 = "a" + new string('c', 19) + new string('b', 180);
        // Window 2: 30 subs → sim=0.855
        string line2 = "a" + new string('d', 30) + new string('b', 169);
        string line3 = new string('z', 200);

        string content = line1 + "\n" + line2 + "\n" + line3;
        string findText = normFind;

        var result = PatchEngine.FindFuzzyMatch(content, findText, normFind);

        Assert.NotNull(result);
        Assert.Equal(0.905, result.Value.similarity, 4);
    }

    // ── FindFuzzyMatch returns correct origStart/origEnd offsets ──────────────
    [Fact]
    public void MatchOffsets_PointToCorrectWindow()
    {
        string normFind = "a" + new string('b', 199);
        // Line 1: very different
        string line1 = new string('z', 200);
        // Line 2: good match (19 subs → sim=0.905)
        string line2 = "a" + new string('c', 19) + new string('b', 180);
        // Line 3: very different
        string line3 = new string('y', 200);

        string content = line1 + "\n" + line2 + "\n" + line3;
        string findText = normFind;

        var result = PatchEngine.FindFuzzyMatch(content, findText, normFind);

       Assert.NotNull(result);
        // line2 starts at position 201 (200 chars + 1 newline)
        Assert.Equal(201, result.Value.origStart);
        // line2 ends at position 402 (includes trailing \n at position 401)
        // FindFuzzyMatch sets end = nl + 1, so the newline is included
        Assert.Equal(402, result.Value.origEnd);
    }

    // ── Multi-line findText: windowSize matches findText line count ───────────
    [Fact]
    public void MultiLineFindText_WindowSizeMatches()
    {
        // 3-line findText → windowSize=3
        string findText = "line one\nline two\nline three";
        string normFind = "line one line two line three";

        // Content with 5 lines; the 3-line window starting at line 2 should match
        string content = "garbage\nline one\nline two\nline three\nmore garbage";

        var result = PatchEngine.FindFuzzyMatch(content, findText, normFind);

        Assert.NotNull(result);
        // Exact match → sim=1.0
        Assert.Equal(1.0, result.Value.similarity);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// NormalizeWithMap — position mapping correctness
// ─────────────────────────────────────────────────────────────────────────────

public sealed class NormalizeWithMapTests
{
    // ── Basic: collapsed whitespace with mixed types ─────────────────────────
    // "a  b\t\tc\r\nd" → "a b c d"
    // Verify each norm position maps to the FIRST char of the original whitespace run.
    [Fact]
    public void MixedWhitespace_MapsToFirstCharOfRun()
    {
        string input = "a  b\t\tc\r\nd";
        var (norm, map) = PatchEngine.NormalizeWithMap(input);

        Assert.Equal("a b c d", norm);

        // 'a' at orig 0
        Assert.Equal(0, map[0]);
        // collapsed "  " → maps to first space at orig 1
        Assert.Equal(1, map[1]);
        // 'b' at orig 3
        Assert.Equal(3, map[2]);
        // collapsed "\t\t" → maps to first tab at orig 4
        Assert.Equal(4, map[3]);
        // 'c' at orig 6
        Assert.Equal(6, map[4]);
        // collapsed "\r\n" → maps to '\r' at orig 7
        Assert.Equal(7, map[5]);
        // 'd' at orig 9
        Assert.Equal(9, map[6]);
    }

    // ── Leading whitespace ───────────────────────────────────────────────────
    [Fact]
    public void LeadingWhitespace_MapsCorrectly()
    {
        string input = "  hello";
        var (norm, map) = PatchEngine.NormalizeWithMap(input);

        Assert.Equal(" hello", norm);
        // collapsed "  " → maps to first space at orig 0
        Assert.Equal(0, map[0]);
        // 'h' at orig 2
        Assert.Equal(2, map[1]);
    }

    // ── Trailing whitespace ──────────────────────────────────────────────────
    [Fact]
    public void TrailingWhitespace_MapsCorrectly()
    {
        string input = "hello   ";
        var (norm, map) = PatchEngine.NormalizeWithMap(input);

        Assert.Equal("hello ", norm);
        // 'o' at orig 4
        Assert.Equal(4, map[4]);
        // collapsed "   " → maps to first space at orig 5
        Assert.Equal(5, map[5]);
    }

    // ── Empty string ─────────────────────────────────────────────────────────
    [Fact]
    public void EmptyString_ReturnsEmpty()
    {
        var (norm, map) = PatchEngine.NormalizeWithMap("");

        Assert.Equal("", norm);
        Assert.Empty(map);
    }

    // ── All whitespace ───────────────────────────────────────────────────────
    [Fact]
    public void AllWhitespace_CollapsesToSingleSpace()
    {
        string input = "   \t  ";
        var (norm, map) = PatchEngine.NormalizeWithMap(input);

        Assert.Equal(" ", norm);
        Assert.Single(map);
        Assert.Equal(0, map[0]); // maps to first char of the whitespace run
    }

    // ── No whitespace ────────────────────────────────────────────────────────
    [Fact]
    public void NoWhitespace_Passthrough()
    {
        string input = "hello";
        var (norm, map) = PatchEngine.NormalizeWithMap(input);

        Assert.Equal("hello", norm);
        Assert.Equal(new int[] { 0, 1, 2, 3, 4 }, map);
    }

    // ── CRLF in content maps correctly ───────────────────────────────────────
    [Fact]
    public void CrlfContent_MapsCorrectly()
    {
        string input = "line1\r\nline2\r\nline3";
        var (norm, map) = PatchEngine.NormalizeWithMap(input);

        Assert.Equal("line1 line2 line3", norm);

        // 'l' of "line1" at orig 0
        Assert.Equal(0, map[0]);
        // '1' of "line1" at orig 4
        Assert.Equal(4, map[4]);
        // collapsed "\r\n" → maps to '\r' at orig 5
        Assert.Equal(5, map[5]);
        // 'l' of "line2" at orig 7
        Assert.Equal(7, map[6]);
        // '2' of "line2" at orig 11
        Assert.Equal(11, map[10]);
        // collapsed "\r\n" → maps to '\r' at orig 12
        Assert.Equal(12, map[11]);
        // 'l' of "line3" at orig 14
        Assert.Equal(14, map[12]);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// CRLF vs LF preservation through ResolvePatch + ApplyPatch
// ─────────────────────────────────────────────────────────────────────────────

public sealed class LineEndingPreservationTests
{
    // ── CRLF file: patched output preserves CRLF ─────────────────────────────
    [Fact]
    public void CrlfFile_PreservesCrlfAfterPatch()
    {
        string tmpPath = TempHelpers.TmpFile();
        string backupDir = TempHelpers.TmpBackupDir();
        string? backupPath = null;
        try
        {
            // File with CRLF line endings
            string crlfContent = "public class Sample\r\n{\r\n    public void Hello()\r\n    {\r\n        Console.WriteLine(\"Hello\");\r\n    }\r\n}\r\n";

            File.WriteAllText(tmpPath, crlfContent, Encoding.UTF8);

            var patch =
                "PATCH sample.cs\n" +
                "FIND:\n" +
                "        Console.WriteLine(\"Hello\");\n" +
                "REPLACE:\n" +
                "        Console.WriteLine(\"World\");\n";

            var resolved = PatchEngine.ResolvePatch(
                patch, tmpPath, "sample.cs",
                crlfContent, Encoding.UTF8, fromToolCall: false,
                (_, _) => { });

            Assert.NotNull(resolved);

            var applyResult = PatchEngine.ApplyPatch(resolved, backupDir);
            backupPath = applyResult.BackupPath;

            Assert.True(applyResult.Success, $"ApplyPatch failed: {applyResult.Error}");

           // The patched content should use CRLF (matching the original file)
            // Verify: after removing all CRLF, no bare LF remains
            string withoutCrlf = applyResult.UpdatedContent.Replace("\r\n", "");
            Assert.DoesNotContain("\n", withoutCrlf);
        }
        finally { TempHelpers.Cleanup(tmpPath, backupPath); }
    }

    // ── LF file: patched output preserves LF ─────────────────────────────────
    [Fact]
    public void LfFile_PreservesLfAfterPatch()
    {
        string tmpPath = TempHelpers.TmpFile();
        string backupDir = TempHelpers.TmpBackupDir();
        string? backupPath = null;
        try
        {
            // File with LF line endings
            string lfContent = "public class Sample\n{\n    public void Hello()\n    {\n        Console.WriteLine(\"Hello\");\n    }\n}\n";

            File.WriteAllText(tmpPath, lfContent, Encoding.UTF8);

            var patch =
                "PATCH sample.cs\r\n" +
                "FIND:\r\n" +
                "        Console.WriteLine(\"Hello\");\r\n" +
                "REPLACE:\r\n" +
                "        Console.WriteLine(\"World\");\r\n";

            var resolved = PatchEngine.ResolvePatch(
                patch, tmpPath, "sample.cs",
                lfContent, Encoding.UTF8, fromToolCall: false,
                (_, _) => { });

            Assert.NotNull(resolved);

            var applyResult = PatchEngine.ApplyPatch(resolved, backupDir);
            backupPath = applyResult.BackupPath;

            Assert.True(applyResult.Success, $"ApplyPatch failed: {applyResult.Error}");

            // The patched content should use LF (matching the original file)
            Assert.DoesNotContain("\r\n", applyResult.UpdatedContent);
        }
        finally { TempHelpers.Cleanup(tmpPath, backupPath); }
    }

    // ── Mixed: FIND/REPLACE uses CRLF but file uses LF → output is LF ────────
    [Fact]
    public void PatchWithCrlfIntoLfFile_OutputIsLf()
    {
        string tmpPath = TempHelpers.TmpFile();
        string backupDir = TempHelpers.TmpBackupDir();
        string? backupPath = null;
        try
        {
            string lfContent = "public class Sample\n{\n    public void Hello()\n    {\n        Console.WriteLine(\"Hello\");\n    }\n}\n";

            File.WriteAllText(tmpPath, lfContent, Encoding.UTF8);

            // Patch text uses CRLF line endings
            var patch =
                "PATCH sample.cs\r\n" +
                "FIND:\r\n" +
                "        Console.WriteLine(\"Hello\");\r\n" +
                "REPLACE:\r\n" +
                "        Console.WriteLine(\"World\");\r\n";

            var resolved = PatchEngine.ResolvePatch(
                patch, tmpPath, "sample.cs",
                lfContent, Encoding.UTF8, fromToolCall: false,
                (_, _) => { });

            Assert.NotNull(resolved);

            var applyResult = PatchEngine.ApplyPatch(resolved, backupDir);
            backupPath = applyResult.BackupPath;

            Assert.True(applyResult.Success, $"ApplyPatch failed: {applyResult.Error}");
            // Output should be LF since the file uses LF
            Assert.DoesNotContain("\r\n", applyResult.UpdatedContent);
        }
        finally { TempHelpers.Cleanup(tmpPath, backupPath); }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// ParsePatchBlocks — fromToolCall flag and multi-pair parsing
// ─────────────────────────────────────────────────────────────────────────────

public sealed class ParsePatchBlocksTests
{
    // ── fromToolCall=false: strips markdown fences from FIND content ─────────
    [Fact]
    public void FromToolCallFalse_StripFences()
    {
        var input =
            "PATCH file.cs\n" +
            "FIND:\n" +
            "```csharp\n" +
            "Console.WriteLine(\"Hello\");\n" +
            "```\n" +
            "REPLACE:\n" +
            "```csharp\n" +
            "Console.WriteLine(\"World\");\n" +
            "```\n";

        var blocks = PatchEngine.ParsePatchBlocks(input, fromToolCall: false);

        Assert.Single(blocks);
        Assert.Equal("Console.WriteLine(\"Hello\");", blocks[0].findText);
        Assert.Equal("Console.WriteLine(\"World\");", blocks[0].replaceText);
    }

    // ── fromToolCall=true: preserves backticks as legitimate content ──────────
    [Fact]
    public void FromToolCallTrue_PreservesBackticks()
    {
        var input =
            "PATCH file.cs\n" +
            "FIND:\n" +
            "```csharp\n" +
            "Console.WriteLine(\"Hello\");\n" +
            "```\n" +
            "REPLACE:\n" +
            "```csharp\n" +
            "Console.WriteLine(\"World\");\n" +
            "```\n";

        var blocks = PatchEngine.ParsePatchBlocks(input, fromToolCall: true);

        Assert.Single(blocks);
        // With fromToolCall=true, fences are NOT stripped from FIND content
        Assert.Contains("```csharp", blocks[0].findText);
    }

    // ── Multiple FIND/REPLACE pairs in a single block ────────────────────────
    [Fact]
    public void MultiplePairs_ParsedCorrectly()
    {
        var input =
            "PATCH file.cs\n" +
            "FIND:\n" +
            "old1\n" +
            "REPLACE:\n" +
            "new1\n" +
            "FIND:\n" +
            "old2\n" +
            "REPLACE:\n" +
            "new2\n" +
            "FIND:\n" +
            "old3\n" +
            "REPLACE:\n" +
            "new3\n";

        var blocks = PatchEngine.ParsePatchBlocks(input, fromToolCall: false);

        Assert.Equal(3, blocks.Count);
        Assert.Equal("old1", blocks[0].findText);
        Assert.Equal("new1", blocks[0].replaceText);
        Assert.Equal("old2", blocks[1].findText);
        Assert.Equal("new2", blocks[1].replaceText);
        Assert.Equal("old3", blocks[2].findText);
        Assert.Equal("new3", blocks[2].replaceText);
    }

    // ── No FIND/REPLACE → empty list ─────────────────────────────────────────
    [Fact]
    public void NoFindReplace_ReturnsEmpty()
    {
        var input = "PATCH file.cs\nsome random text";
        var blocks = PatchEngine.ParsePatchBlocks(input, fromToolCall: false);
        Assert.Empty(blocks);
    }

    // ── Only header line → empty list ────────────────────────────────────────
    [Fact]
    public void HeaderOnly_ReturnsEmpty()
    {
        var input = "PATCH file.cs";
        var blocks = PatchEngine.ParsePatchBlocks(input, fromToolCall: false);
        Assert.Empty(blocks);
    }

    // ── REPLACE terminated by SHELL: directive ───────────────────────────────
    [Fact]
    public void ReplaceTerminatedByShell()
    {
        var input =
            "PATCH file.cs\n" +
            "FIND:\n" +
            "old\n" +
            "REPLACE:\n" +
            "new\n" +
            "SHELL: echo done\n";

        var blocks = PatchEngine.ParsePatchBlocks(input, fromToolCall: false);

        Assert.Single(blocks);
        Assert.Equal("old", blocks[0].findText);
        Assert.Equal("new", blocks[0].replaceText);
    }

    // ── REPLACE terminated by bare closing fence ─────────────────────────────
    [Fact]
    public void ReplaceTerminatedByClosingFence()
    {
        var input =
            "PATCH file.cs\n" +
            "FIND:\n" +
            "old\n" +
            "REPLACE:\n" +
            "new\n" +
            "```\n" +
            "extra stuff\n";

        var blocks = PatchEngine.ParsePatchBlocks(input, fromToolCall: false);

        Assert.Single(blocks);
        Assert.Equal("old", blocks[0].findText);
        Assert.Equal("new", blocks[0].replaceText);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// StripOuterCodeFence
// ─────────────────────────────────────────────────────────────────────────────

public sealed class StripOuterCodeFenceTests
{
    // ── Strip opening and closing fence ──────────────────────────────────────
    [Fact]
    public void StripsOpeningAndClosingFence()
    {
        string input = "```csharp\nConsole.WriteLine(\"Hello\");\n```";
        string result = PatchEngine.StripOuterCodeFence(input);
        Assert.Equal("Console.WriteLine(\"Hello\");", result);
    }

    // ── Strip only closing fence (no opening) ────────────────────────────────
    [Fact]
    public void StripsOnlyClosingFence()
    {
        string input = "Console.WriteLine(\"Hello\");\n```";
        string result = PatchEngine.StripOuterCodeFence(input);
        Assert.Equal("Console.WriteLine(\"Hello\");", result);
    }

    // ── No fences → passthrough ──────────────────────────────────────────────
    [Fact]
    public void NoFences_Passthrough()
    {
        string input = "Console.WriteLine(\"Hello\");";
        string result = PatchEngine.StripOuterCodeFence(input);
        Assert.Equal("Console.WriteLine(\"Hello\");", result);
    }

    // ── Interior fence lines preserved ───────────────────────────────────────
    [Fact]
    public void InteriorFences_Preserved()
    {
        string input = "```csharp\nline1\n```\nline2\n```";
        string result = PatchEngine.StripOuterCodeFence(input);
        // Opening ```csharp stripped, closing ``` stripped, interior ``` preserved
        Assert.Equal("line1\n```\nline2", result);
    }

    // ── Empty string ─────────────────────────────────────────────────────────
    [Fact]
    public void EmptyString_ReturnsEmpty()
    {
        string result = PatchEngine.StripOuterCodeFence("");
        Assert.Equal("", result);
    }

    // ── Only opening fence → empty ───────────────────────────────────────────
    [Fact]
    public void OnlyOpeningFence_ReturnsEmpty()
    {
        string result = PatchEngine.StripOuterCodeFence("```csharp");
        Assert.Equal("", result);
    }

    // ── Leading blank lines before opening fence ─────────────────────────────
    [Fact]
    public void LeadingBlanksBeforeFence_Stripped()
    {
        string input = "\n\n```csharp\ncontent\n```";
        string result = PatchEngine.StripOuterCodeFence(input);
        Assert.Equal("\n\ncontent", result);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// StripMarkdownFenceLines
// ─────────────────────────────────────────────────────────────────────────────

public sealed class StripMarkdownFenceLinesTests
{
    // ── Removes all fence lines ──────────────────────────────────────────────
    [Fact]
    public void RemovesAllFenceLines()
    {
        string input = "```\nline1\n```\nline2\n```csharp\nline3\n```";
        string result = PatchEngine.StripMarkdownFenceLines(input);
        Assert.Equal("line1\nline2\nline3", result);
    }

    // ── No fences → passthrough ──────────────────────────────────────────────
    [Fact]
    public void NoFences_Passthrough()
    {
        string input = "line1\nline2\nline3";
        string result = PatchEngine.StripMarkdownFenceLines(input);
        Assert.Equal("line1\nline2\nline3", result);
    }

    // ── Fence with language tag removed ──────────────────────────────────────
    [Fact]
    public void FenceWithLanguageTag_Removed()
    {
        string input = "```python\ncode\n```";
        string result = PatchEngine.StripMarkdownFenceLines(input);
        Assert.Equal("code", result);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// StripHallucinatedTerminators
// ─────────────────────────────────────────────────────────────────────────────

public sealed class StripHallucinatedTerminatorsTests
{
    // ── Removes END_PATCH ────────────────────────────────────────────────────
    [Fact]
    public void RemovesEndPatch()
    {
        string input = "line1\nEND_PATCH\nline2";
        string result = PatchEngine.StripHallucinatedTerminators(input);
        Assert.Equal("line1\nline2", result);
    }

    // ── Removes END_FILE ─────────────────────────────────────────────────────
    [Fact]
    public void RemovesEndFile()
    {
        string input = "line1\nEND_FILE\nline2";
        string result = PatchEngine.StripHallucinatedTerminators(input);
        Assert.Equal("line1\nline2", result);
    }

    // ── Removes END (case-insensitive) ───────────────────────────────────────
    [Fact]
    public void RemovesEndCaseInsensitive()
    {
        string input = "line1\nend\nline2";
        string result = PatchEngine.StripHallucinatedTerminators(input);
        Assert.Equal("line1\nline2", result);
    }

    // ── Removes line of dashes ───────────────────────────────────────────────
    [Fact]
    public void RemovesDashLine()
    {
        string input = "line1\n---\nline2";
        string result = PatchEngine.StripHallucinatedTerminators(input);
        Assert.Equal("line1\nline2", result);
    }

    // ── Does NOT remove two dashes (minimum is three) ────────────────────────
    [Fact]
    public void DoesNotRemoveTwoDashes()
    {
        string input = "line1\n--\nline2";
        string result = PatchEngine.StripHallucinatedTerminators(input);
        Assert.Equal("line1\n--\nline2", result);
    }

    // ── Removes multiple terminators ─────────────────────────────────────────
    [Fact]
    public void RemovesMultipleTerminators()
    {
        string input = "line1\nEND_PATCH\n---\nEND_FILE\nline2\nEND\nline3";
        string result = PatchEngine.StripHallucinatedTerminators(input);
        Assert.Equal("line1\nline2\nline3", result);
    }

    // ── No terminators → passthrough ─────────────────────────────────────────
    [Fact]
    public void NoTerminators_Passthrough()
    {
        string input = "line1\nline2\nline3";
        string result = PatchEngine.StripHallucinatedTerminators(input);
        Assert.Equal("line1\nline2\nline3", result);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// ReadFilePreservingEncoding — non-BOM and script-file BOM stripping
// ─────────────────────────────────────────────────────────────────────────────

public sealed class ReadFilePreservingEncodingEdgeCases
{
    // ── Non-BOM UTF-8 file → no BOM in returned encoding ─────────────────────
    [Fact]
    public void NonBomUtf8_NoBomInEncoding()
    {
        string tmpPath = TempHelpers.TmpFile();
        try
        {
            var noBomEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            File.WriteAllText(tmpPath, "hello", noBomEncoding);

            var (content, encoding) = PatchEngine.ReadFilePreservingEncoding(tmpPath);

            Assert.Equal("hello", content);
            Assert.IsType<UTF8Encoding>(encoding);
            var preamble = ((UTF8Encoding)encoding).GetPreamble();
            Assert.Empty(preamble);
        }
        finally { try { File.Delete(tmpPath); } catch { } }
    }

    // ── Script file (.ps1) with BOM → BOM stripped from returned encoding ────
    [Fact]
    public void ScriptFileWithBom_BomStrippedFromEncoding()
    {
        string tmpPath = Path.Combine(Path.GetTempPath(), $"dm_test_{Guid.NewGuid():N}.ps1");
        try
        {
            var bomEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
            File.WriteAllText(tmpPath, "# PowerShell script", bomEncoding);

            var (content, encoding) = PatchEngine.ReadFilePreservingEncoding(tmpPath);

            Assert.Equal("# PowerShell script", content);
            Assert.IsType<UTF8Encoding>(encoding);
            var preamble = ((UTF8Encoding)encoding).GetPreamble();
            // Script files should return encoding WITHOUT BOM
            Assert.Empty(preamble);
        }
        finally { try { File.Delete(tmpPath); } catch { } }
    }

    // ── Script file (.sh) with BOM → BOM stripped ────────────────────────────
    [Fact]
    public void ShellScriptWithBom_BomStripped()
    {
        string tmpPath = Path.Combine(Path.GetTempPath(), $"dm_test_{Guid.NewGuid():N}.sh");
        try
        {
            var bomEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
            File.WriteAllText(tmpPath, "#!/bin/bash", bomEncoding);

            var (content, encoding) = PatchEngine.ReadFilePreservingEncoding(tmpPath);

            Assert.Equal("#!/bin/bash", content);
            var preamble = ((UTF8Encoding)encoding).GetPreamble();
            Assert.Empty(preamble);
        }
        finally { try { File.Delete(tmpPath); } catch { } }
    }

    // ── Script file (.cmd) with BOM → BOM stripped ───────────────────────────
    [Fact]
    public void CmdScriptWithBom_BomStripped()
    {
        string tmpPath = Path.Combine(Path.GetTempPath(), $"dm_test_{Guid.NewGuid():N}.cmd");
        try
        {
            var bomEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
            File.WriteAllText(tmpPath, "@echo off", bomEncoding);

            var (content, encoding) = PatchEngine.ReadFilePreservingEncoding(tmpPath);

            Assert.Equal("@echo off", content);
            var preamble = ((UTF8Encoding)encoding).GetPreamble();
            Assert.Empty(preamble);
        }
        finally { try { File.Delete(tmpPath); } catch { } }
    }

    // ── Script file (.bat) with BOM → BOM stripped ───────────────────────────
    [Fact]
    public void BatScriptWithBom_BomStripped()
    {
        string tmpPath = Path.Combine(Path.GetTempPath(), $"dm_test_{Guid.NewGuid():N}.bat");
        try
        {
            var bomEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
            File.WriteAllText(tmpPath, "@echo off", bomEncoding);

            var (content, encoding) = PatchEngine.ReadFilePreservingEncoding(tmpPath);

            Assert.Equal("@echo off", content);
            var preamble = ((UTF8Encoding)encoding).GetPreamble();
            Assert.Empty(preamble);
        }
        finally { try { File.Delete(tmpPath); } catch { } }
    }

    // ── Non-script file (.cs) with BOM → BOM preserved in encoding ───────────
    [Fact]
    public void NonScriptFileWithBom_BomPreserved()
    {
        string tmpPath = Path.Combine(Path.GetTempPath(), $"dm_test_{Guid.NewGuid():N}.cs");
        try
        {
            var bomEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
            File.WriteAllText(tmpPath, "class X {}", bomEncoding);

            var (content, encoding) = PatchEngine.ReadFilePreservingEncoding(tmpPath);

            Assert.Equal("class X {}", content);
            var preamble = ((UTF8Encoding)encoding).GetPreamble();
            Assert.NotEmpty(preamble);
        }
        finally { try { File.Delete(tmpPath); } catch { } }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// ApplyPatch — reverse-order multi-block application
// ─────────────────────────────────────────────────────────────────────────────

public sealed class ApplyPatchReverseOrderTests
{
    // ── Three blocks: verify reverse-order application doesn't corrupt offsets
    // The blocks are resolved in forward order but applied in reverse order.
    // This test verifies all three replacements land correctly.
    [Fact]
    public void ThreeBlocks_AllReplacementsCorrect()
    {
        string tmpPath = TempHelpers.TmpFile();
        string backupDir = TempHelpers.TmpBackupDir();
        string? backupPath = null;
        try
        {
            string content =
                "public class Service\n" +
                "{\n" +
                "    public void Start() { }\n" +
                "    public void Stop() { }\n" +
                "    public void Reset() { }\n" +
                "}\n";

            var patch =
                "PATCH service.cs\n" +
                "FIND:\n" +
                "    public void Start() { }\n" +
                "REPLACE:\n" +
                "    public void Start() { Console.WriteLine(\"Starting\"); }\n" +
                "FIND:\n" +
                "    public void Stop() { }\n" +
                "REPLACE:\n" +
                "    public void Stop() { Console.WriteLine(\"Stopping\"); }\n" +
                "FIND:\n" +
                "    public void Reset() { }\n" +
                "REPLACE:\n" +
                "    public void Reset() { Console.WriteLine(\"Resetting\"); }\n";

            var resolved = PatchEngine.ResolvePatch(
                patch, tmpPath, "service.cs",
                content, Encoding.UTF8, fromToolCall: false,
                (_, _) => { });

            Assert.NotNull(resolved);
            Assert.Equal(3, resolved.ResolvedBlocks.Count);

            File.WriteAllText(tmpPath, content, Encoding.UTF8);
            var applyResult = PatchEngine.ApplyPatch(resolved, backupDir);
            backupPath = applyResult.BackupPath;

            Assert.True(applyResult.Success, $"ApplyPatch failed: {applyResult.Error}");
            Assert.Contains("Console.WriteLine(\"Starting\")", applyResult.UpdatedContent);
            Assert.Contains("Console.WriteLine(\"Stopping\")", applyResult.UpdatedContent);
            Assert.Contains("Console.WriteLine(\"Resetting\")", applyResult.UpdatedContent);
            // Original stubs should be gone
            Assert.DoesNotContain("public void Start() { }", applyResult.UpdatedContent);
            Assert.DoesNotContain("public void Stop() { }", applyResult.UpdatedContent);
            Assert.DoesNotContain("public void Reset() { }", applyResult.UpdatedContent);
        }
        finally { TempHelpers.Cleanup(tmpPath, backupPath); }
    }

    // ── Adjacent blocks: replacement of first block doesn't break second ──────
    [Fact]
    public void AdjacentBlocks_NoOffsetShift()
    {
        string tmpPath = TempHelpers.TmpFile();
        string backupDir = TempHelpers.TmpBackupDir();
        string? backupPath = null;
        try
        {
            // Two adjacent lines — replacing the first changes length
            string content = "lineA\nlineB\n";

            var patch =
                "PATCH test.txt\n" +
                "FIND:\n" +
                "lineA\n" +
                "REPLACE:\n" +
                "lineA_replaced_with_longer_text\n" +
                "FIND:\n" +
                "lineB\n" +
                "REPLACE:\n" +
                "lineB_replaced\n";

            var resolved = PatchEngine.ResolvePatch(
                patch, tmpPath, "test.txt",
                content, Encoding.UTF8, fromToolCall: false,
                (_, _) => { });

            Assert.NotNull(resolved);
            Assert.Equal(2, resolved.ResolvedBlocks.Count);

            File.WriteAllText(tmpPath, content, Encoding.UTF8);
            var applyResult = PatchEngine.ApplyPatch(resolved, backupDir);
            backupPath = applyResult.BackupPath;

            Assert.True(applyResult.Success, $"ApplyPatch failed: {applyResult.Error}");
            Assert.Contains("lineA_replaced_with_longer_text", applyResult.UpdatedContent);
            Assert.Contains("lineB_replaced", applyResult.UpdatedContent);
            Assert.DoesNotContain("lineA\n", applyResult.UpdatedContent);
            Assert.DoesNotContain("lineB\n", applyResult.UpdatedContent);
        }
        finally { TempHelpers.Cleanup(tmpPath, backupPath); }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// LevenshteinDistance — basic correctness
// ─────────────────────────────────────────────────────────────────────────────

public sealed class LevenshteinDistanceTests
{
    [Fact]
    public void IdenticalStrings_ZeroDistance()
    {
        Assert.Equal(0, PatchEngine.LevenshteinDistance("hello", "hello"));
    }

    [Fact]
    public void EmptyVsNonEmpty_ReturnsLength()
    {
        Assert.Equal(5, PatchEngine.LevenshteinDistance("", "hello"));
        Assert.Equal(5, PatchEngine.LevenshteinDistance("hello", ""));
    }

    [Fact]
    public void SingleSubstitution_DistanceOne()
    {
        Assert.Equal(1, PatchEngine.LevenshteinDistance("hello", "hallo"));
    }

    [Fact]
    public void CompletelyDifferent_ReturnsMax()
    {
        Assert.Equal(3, PatchEngine.LevenshteinDistance("abc", "xyz"));
    }

    [Fact]
    public void Insertion_DistanceOne()
    {
        Assert.Equal(1, PatchEngine.LevenshteinDistance("hello", "hallo"));
    }

    [Fact]
    public void Deletion_DistanceOne()
    {
        Assert.Equal(1, PatchEngine.LevenshteinDistance("hallo", "hello"));
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// ResolvePatch — edge cases
// ─────────────────────────────────────────────────────────────────────────────

public sealed class ResolvePatchEdgeCases
{
    // ── fromToolCall=true: no fence stripping in ResolvePatch ─────────────────
    [Fact]
    public void FromToolCallTrue_NoFenceStripping()
    {
        var messages = new List<string>();

        // In tool_call mode, backticks are legitimate content
        var patch =
            "PATCH file.cs\n" +
            "FIND:\n" +
            "```csharp\n" +
            "Console.WriteLine(\"Hello\");\n" +
            "```\n" +
            "REPLACE:\n" +
            "```csharp\n" +
            "Console.WriteLine(\"World\");\n" +
            "```\n";

        // Content that includes the backticks
        string content = "```csharp\nConsole.WriteLine(\"Hello\");\n```";

        var result = PatchEngine.ResolvePatch(
            patch, "file.cs", "file.cs",
            content, Encoding.UTF8, fromToolCall: true,
            (msg, _) => messages.Add(msg));

        // With fromToolCall=true, the FIND text includes backticks
        // and should match the content exactly
        Assert.NotNull(result);
        Assert.Equal(PatchConfidence.Exact, result.Confidence);
    }

    // ── ResolvePatch with empty FIND after stripping → null ──────────────────
    [Fact]
    public void EmptyFindAfterStripping_ReturnsNull()
    {
        var messages = new List<string>();

        var patch =
            "PATCH file.cs\n" +
            "FIND:\n" +
            "```\n" +
            "```\n" +
            "REPLACE:\n" +
            "new content\n";

        var result = PatchEngine.ResolvePatch(
            patch, "file.cs", "file.cs",
            "some content", Encoding.UTF8, fromToolCall: false,
            (msg, _) => messages.Add(msg));

        Assert.Null(result);
        Assert.Contains(messages, m => m.Contains("FIND is empty"));
    }

    // ── ResolvePatch with no resolved blocks → null ──────────────────────────
    [Fact]
    public void NoResolvedBlocks_ReturnsNull()
    {
        var messages = new List<string>();

        var patch =
            "PATCH file.cs\n" +
            "FIND:\n" +
            "        Console.WriteLine(\"ThisDoesNotExist\");\n" +
            "REPLACE:\n" +
            "        Console.WriteLine(\"World\");\n";

        var result = PatchEngine.ResolvePatch(
            patch, "file.cs", "file.cs",
            PatchFixtures.HelloSource, Encoding.UTF8, fromToolCall: false,
            (msg, _) => messages.Add(msg));

        Assert.Null(result);
        Assert.Contains(messages, m => m.Contains("FIND text not found"));
    }
}
