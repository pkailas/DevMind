// File: FuzzyPatchSafetyTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Regression tests for making fuzzy patch application safe for unattended agent runs.
//
// The incident: a fuzzy match against AgentJobManager.cs silently "merged two separate
// member declarations onto one line" and re-indented the surrounding block, yet still
// compiled and reported success. Root cause: the parser strips the trailing newline off
// the REPLACE text, but the fuzzy span is line-aligned (it owns the last matched line's
// newline) — so an N-line replacement for an N-line span that is not at end-of-file drops
// that final newline and glues the last replaced line onto the one below it.
//
//   1. The fuzzy path now restores the replacement's trailing newline to match the span's
//      line structure, so a line is never silently glued. The incident shape (1-line fuzzy
//      replace, parser-style, no trailing newline) must apply cleanly with the two
//      declarations still on separate lines.
//   2. A line-continuity guard is the safety net: if a fuzzy splice ever changes the line
//      count by anything other than (replacement lines - matched lines), it is rejected
//      with the affected line shown.
//   3. Structured formats (.slnx/.csproj/...) are never fuzzy-matched — a near-but-
//      inexact FIND is refused with a message stating fuzzy matching is disabled for the
//      file type, and an exact FIND still applies as Exact.

using System.Text;
using Xunit;

namespace DevMind.Core.Tests;

public sealed class FuzzyPatchSafetyTests
{
    // The same shape as the real incident (BuildVerification in AgentJobManager.cs) —
    // 11 lines, two adjacent single-line member declarations that a bad fuzzy splice
    // could fuse.
    private const string Source =
        "namespace DevMind\n" +
        "{\n" +
        "    internal sealed class BuildVerification\n" +
        "    {\n" +
        "        public required string Command { get; init; }\n" +
        "        public int ExitCode { get; init; }\n" +
        "        /// <summary>Last ~2 KB of build output</summary>\n" +
        "        public required string OutputTail { get; init; }\n" +
        "        public bool Succeeded => ExitCode == 0;\n" +
        "    }\n" +
        "}\n";

    // Runs a single-pair ResolvePairs and captures the Error reporter lines, which is
    // exactly what the MCP patch_file tool returns to the agent on failure. When
    // <paramref name="fullPath"/> is given the file is written there first so the caller
    // can ApplyPatch (which writes back to resolved.FullPath); otherwise a throwaway path
    // is used for resolve-only checks.
    private static (PatchResolveResult? resolved, string errors, string fullPath) Run(
        (string find, string replace)[] pairs, string fileName, string content, string? fullPath = null)
    {
        string path = fullPath ?? Path.Combine(@"c:\x", fileName);
        if (fullPath != null) File.WriteAllText(path, content, Encoding.UTF8);

        var errors = new StringBuilder();
        var resolved = PatchEngine.ResolvePairs(
            pairs.Select(p => (p.find, p.replace)).ToList(),
            path, fileName, content, Encoding.UTF8,
            (msg, color) => { if (color == OutputColor.Error) errors.Append(msg); });
        return (resolved, errors.ToString(), path);
    }

    // ── Requirement 1: the incident — a line must never be silently glued ──────

    // The incident shape: a 1-line fuzzy replace whose REPLACE text (as the parser hands
    // it to the engine) carries no trailing newline. Before the fix this dropped the
    // matched line's boundary and fused the OutputTail and Succeeded declarations onto one
    // line while still compiling. It must now apply cleanly with both lines intact and on
    // separate lines. The FIND has a one-char typo ("Ouptut") so the normalized exact
    // match misses and the fuzzy path runs.
    [Fact]
    public void FuzzyReplace_TrailingNewlineRestored_NoLineGlued()
    {
        string tmpPath = TempHelpers.TmpFile();
        string? backupPath = null;
        try
        {
            var (resolved, _, _) = Run(new[]
            {
                ("        public required string OuptutTail { get; init; }",
                 "        public required string OutputTail { get; set; }"), // parser-style: no trailing \n
            }, "x.cs", Source, fullPath: tmpPath);

            Assert.NotNull(resolved);
            Assert.Equal(PatchConfidence.Fuzzy, resolved.Confidence);

            var applied = PatchEngine.ApplyPatch(resolved!, TempHelpers.TmpBackupDir());
            backupPath = applied.BackupPath;
            Assert.True(applied.Success, applied.Error);

            // Both declarations survive, each on its own line.
            Assert.Contains("\n        public required string OutputTail { get; set; }\n", applied.UpdatedContent);
            Assert.Contains("\n        public bool Succeeded => ExitCode == 0;\n", applied.UpdatedContent);
            // The specific incident damage — the two declarations glued onto one line.
            Assert.DoesNotContain("OutputTail { get; set; }        public bool", applied.UpdatedContent);
        }
        finally { TempHelpers.Cleanup(tmpPath, backupPath); }
    }

    // A multi-line fuzzy replacement (4 lines in, 4 lines out) must also apply cleanly
    // with the line structure preserved — the existing confidence-only test did not verify
    // the applied bytes, so this pins that a legitimate multi-line fuzzy edit does not glue
    // its last line onto the one below it.
    [Fact]
    public void FuzzyMultiLine_LineStructurePreserved()
    {
        string greeter =
            "public class Greeter\n" +
            "{\n" +
            "    public string Greet(string name)\n" +
            "    {\n" +
            "        return \"Hello, \" + name + \"!\";\n" +
            "    }\n" +
            "}\n";

        string tmpPath = TempHelpers.TmpFile();
        string? backupPath = null;
        try
        {
            var (resolved, _, _) = Run(new[]
            {
                (
                 "    public string Greet(string name)\n    {\n        return \"Helo, \" + name + \"!\";\n    }",
                 "    public string Greet(string name)\n    {\n        return \"Hi, \" + name + \"!\";\n    }"),
            }, "greeter.cs", greeter, fullPath: tmpPath);

            Assert.NotNull(resolved);
            Assert.Equal(PatchConfidence.Fuzzy, resolved.Confidence);

            var applied = PatchEngine.ApplyPatch(resolved!, TempHelpers.TmpBackupDir());
            backupPath = applied.BackupPath;
            Assert.True(applied.Success, applied.Error);
            Assert.Contains("return \"Hi, \" + name + \"!\";", applied.UpdatedContent);
            // The closing brace of the method and the class stay on their own lines.
            Assert.Contains("\n    }\n", applied.UpdatedContent);
            Assert.Contains("\n}\n", applied.UpdatedContent);
            Assert.DoesNotContain("Hi, \" + name + \"!\";\n    }}", applied.UpdatedContent);
        }
        finally { TempHelpers.Cleanup(tmpPath, backupPath); }
    }

    // ── Requirement 1b: the boundary at the OTHER end of the file ─────────────
    //
    // The first fix tested "is the span at EOF?" as a proxy for "does the span end with a
    // newline". Those diverge for the common case of a newline-terminated file: the fuzzy
    // span is built as end = nl + 1, so a match on the file's LAST line has
    // origEnd == fileContent.Length while still owning a trailing newline. Testing for EOF
    // therefore dropped the file's terminal newline — the mirror image of the glue bug.
    // The predicate is now what the span itself ends with, which is correct at both ends.

    // A file that ends WITH a newline must still end with one after a fuzzy patch to its
    // last line. This is the regression: the patched file previously lost its final "\n".
    [Fact]
    public void FuzzyReplace_OnLastLineOfNewlineTerminatedFile_KeepsTrailingNewline()
    {
        string src =
            "namespace DevMind\n" +
            "{\n" +
            "    class A { }\n" +
            "}\n" +
            "// tail marker line with distinctive content here\n";

        string tmpPath = TempHelpers.TmpFile();
        string? backupPath = null;
        try
        {
            var (resolved, _, _) = Run(new[]
            {
                ("// tail marker line with distinctiv content here",   // typo → fuzzy path
                 "// tail marker line CHANGED content here"),          // parser-style: no trailing \n
            }, "x.cs", src, fullPath: tmpPath);

            Assert.NotNull(resolved);
            Assert.Equal(PatchConfidence.Fuzzy, resolved!.Confidence);

            var applied = PatchEngine.ApplyPatch(resolved, TempHelpers.TmpBackupDir());
            backupPath = applied.BackupPath;
            Assert.True(applied.Success, applied.Error);

            Assert.Contains("// tail marker line CHANGED content here", applied.UpdatedContent);
            Assert.True(applied.UpdatedContent.EndsWith("\n"),
                "the file's terminal newline must survive a fuzzy patch to its last line");
            // Exactly one — the span's newline must be restored, not doubled.
            Assert.False(applied.UpdatedContent.EndsWith("\n\n"),
                "the terminal newline must not be duplicated");
        }
        finally { TempHelpers.Cleanup(tmpPath, backupPath); }
    }

    // The converse: a file with NO trailing newline must not gain one. This is the case
    // the EOF proxy got right by accident, and it must keep working under the new predicate.
    [Fact]
    public void FuzzyReplace_OnLastLineOfUnterminatedFile_DoesNotAddTrailingNewline()
    {
        string src =
            "namespace DevMind\n" +
            "{\n" +
            "    class A { }\n" +
            "}\n" +
            "// tail marker line with distinctive content here"; // no trailing newline

        string tmpPath = TempHelpers.TmpFile();
        string? backupPath = null;
        try
        {
            var (resolved, _, _) = Run(new[]
            {
                ("// tail marker line with distinctiv content here",
                 "// tail marker line CHANGED content here"),
            }, "x.cs", src, fullPath: tmpPath);

            Assert.NotNull(resolved);
            Assert.Equal(PatchConfidence.Fuzzy, resolved!.Confidence);

            var applied = PatchEngine.ApplyPatch(resolved, TempHelpers.TmpBackupDir());
            backupPath = applied.BackupPath;
            Assert.True(applied.Success, applied.Error);

            Assert.EndsWith("// tail marker line CHANGED content here", applied.UpdatedContent);
            Assert.False(applied.UpdatedContent.EndsWith("\n"),
                "a file without a terminal newline must not gain one");
        }
        finally { TempHelpers.Cleanup(tmpPath, backupPath); }
    }

    // Same boundary in a CRLF file: the span ends "\r\n" and the restored newline must be
    // "\r\n" too, not a bare "\n" that would leave a mixed line ending at EOF.
    [Fact]
    public void FuzzyReplace_OnLastLineOfCrlfFile_KeepsCrlfTerminator()
    {
        string src =
            "namespace DevMind\r\n" +
            "{\r\n" +
            "    class A { }\r\n" +
            "}\r\n" +
            "// tail marker line with distinctive content here\r\n";

        string tmpPath = TempHelpers.TmpFile();
        string? backupPath = null;
        try
        {
            var (resolved, _, _) = Run(new[]
            {
                ("// tail marker line with distinctiv content here",
                 "// tail marker line CHANGED content here"),
            }, "x.cs", src, fullPath: tmpPath);

            Assert.NotNull(resolved);
            Assert.Equal(PatchConfidence.Fuzzy, resolved!.Confidence);

            var applied = PatchEngine.ApplyPatch(resolved, TempHelpers.TmpBackupDir());
            backupPath = applied.BackupPath;
            Assert.True(applied.Success, applied.Error);

            Assert.EndsWith("// tail marker line CHANGED content here\r\n", applied.UpdatedContent);
            Assert.DoesNotContain("here\r\n\r\n", applied.UpdatedContent); // not doubled
        }
        finally { TempHelpers.Cleanup(tmpPath, backupPath); }
    }

    // ── Requirement 2: structured formats never fuzzy-match ───────────────────

    // A .csproj with a near-but-inexact FIND (typo "FrameworK"). Without the guard this
    // would fuzzy-match; with it, it must be refused and tell the agent fuzzy matching is
    // disabled for this file type.
    [Fact]
    public void StructuredFormat_Csproj_NearButInexactFind_IsRefused()
    {
        string csproj =
            "<Project Sdk=\"Microsoft.NET.Sdk\">\n" +
            "  <PropertyGroup>\n" +
            "    <TargetFramework>net10.0</TargetFramework>\n" +
            "  </PropertyGroup>\n" +
            "</Project>\n";

        var (resolved, errors, _) = Run(new[]
        {
            ("    <TargetFrameworK>net10.0</TargetFrameworK>",
             "    <TargetFramework>net9.0</TargetFramework>"),
        }, "app.csproj", csproj);

        Assert.Null(resolved);
        Assert.Contains("Fuzzy matching is disabled", errors);
    }

    // The same near-but-inexact FIND in a .slnx is refused too (the brief names both).
    [Fact]
    public void StructuredFormat_Slnx_NearButInexactFind_IsRefused()
    {
        string slnx =
            "<Solution>\n" +
            "  <Folder Name=\"Projects\">\n" +
            "    <Project Path=\"DevMind.Core\\DevMind.Core.csproj\" />\n" +
            "  </Folder>\n" +
            "</Solution>\n";

        // Typo "csprj" makes the exact match miss; the guard must refuse before fuzzy runs.
        var (resolved, errors, _) = Run(new[]
        {
            ("    <Project Path=\"DevMind.Core\\DevMind.Core.csprj\" />",
             "    <Project Path=\"DevMind.Core\\DevMind.Core.csproj\" DisableFastUpToDateCheck=\"true\" />"),
        }, "DevMind.slnx", slnx);

        Assert.Null(resolved);
        Assert.Contains("Fuzzy matching is disabled", errors);
    }

    // The structured-format guard only blocks the FUZZY path. An exact (normalized) match
    // on a .csproj must still resolve as Exact and apply.
    [Fact]
    public void StructuredFormat_ExactFind_StillAppliesAsExact()
    {
        string csproj =
            "<Project Sdk=\"Microsoft.NET.Sdk\">\n" +
            "  <PropertyGroup>\n" +
            "    <TargetFramework>net10.0</TargetFramework>\n" +
            "  </PropertyGroup>\n" +
            "</Project>\n";

        var (resolved, _, _) = Run(new[]
        {
            ("    <TargetFramework>net10.0</TargetFramework>",
             "    <TargetFramework>net10.0</TargetFramework>\n    <Nullable>enable</Nullable>"),
        }, "app.csproj", csproj);

        Assert.NotNull(resolved);
        Assert.Equal(PatchConfidence.Exact, resolved.Confidence);
    }
}
