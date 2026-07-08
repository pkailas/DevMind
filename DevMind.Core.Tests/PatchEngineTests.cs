// File: PatchEngineTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Ported from DevMindTestBed/PatchEngineTests.cs — faithful 1:1 port of the
// 8 hand-rolled tests into xUnit [Fact] tests. No test logic was changed;
// only the harness (TestResult/RunAll → xUnit Assert) was replaced.

using System.Text;
using Xunit;

namespace DevMind.Core.Tests;

// ─────────────────────────────────────────────────────────────────────────────
// Shared fixtures used by multiple tests (copied verbatim from DevMindTestBed)
// ─────────────────────────────────────────────────────────────────────────────

internal static class PatchFixtures
{
    // Two-method class — used by most exact/ambiguous/backup tests
    public static string HelloSource =>
        "public class Sample\n" +
        "{\n" +
        "    public void Hello()\n" +
        "    {\n" +
        "        Console.WriteLine(\"Hello\");\n" +
        "    }\n" +
        "}\n";

    // Same return value appearing twice — triggers ambiguity check
    public static string DuplicateSource =>
        "public class Config\n" +
        "{\n" +
        "    public int GetTimeout() { return 30; }\n" +
        "    public int GetRetries() { return 30; }\n" +
        "}\n";

    // Two distinct stubs — for multi-block test
    public static string TwoStubSource =>
        "public class Service\n" +
        "{\n" +
        "    public void Start() { }\n" +
        "    public void Stop() { }\n" +
        "}\n";

    // Single method with a known string — one char changed for fuzzy test
    public static string GreeterSource =>
        "public class Greeter\n" +
        "{\n" +
        "    public string Greet(string name)\n" +
        "    {\n" +
        "        return \"Hello, \" + name + \"!\";\n" +
        "    }\n" +
        "}\n";
}

internal static class TempHelpers
{
    public static string TmpFile() =>
        Path.Combine(Path.GetTempPath(), $"dm_test_{Guid.NewGuid():N}.cs");

    public static string TmpBackupDir() =>
        Path.Combine(Path.GetTempPath(), "dm_test_backups");

    public static void Cleanup(string path, string? backupPath)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
        try { if (backupPath != null && File.Exists(backupPath)) File.Delete(backupPath); } catch { }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// ResolvePatch tests — exact match
// ─────────────────────────────────────────────────────────────────────────────

public sealed class ResolvePatchExactMatchTests
{
    // ── Original test 1: ExactMatch_Confidence ──────────────────────────────
    // Verifies that an exact (whitespace-normalized) match produces
    // PatchConfidence.Exact and exactly one resolved block.

    [Fact]
    public void ExactMatch_Confidence()
    {
        var messages = new List<string>();

        var patch =
            "PATCH sample.cs\n" +
            "FIND:\n" +
            "        Console.WriteLine(\"Hello\");\n" +
            "REPLACE:\n" +
            "        Console.WriteLine(\"World\");\n";

        var result = PatchEngine.ResolvePatch(
            patch, "sample.cs", "sample.cs",
            PatchFixtures.HelloSource, Encoding.UTF8, fromToolCall: false,
            (msg, _) => messages.Add(msg));

        Assert.NotNull(result);
        Assert.Equal(PatchConfidence.Exact, result.Confidence);
        Assert.Single(result.ResolvedBlocks);
    }

    // ── Original test 2: ExactMatch_Content ──────────────────────────────────
    // Verifies that after ResolvePatch + ApplyPatch, the updated content
    // contains the replacement and no longer contains the original text.

    [Fact]
    public void ExactMatch_Content()
    {
        string tmpPath = TempHelpers.TmpFile();
        string backupDir = TempHelpers.TmpBackupDir();
        string? backupPath = null;
        try
        {
            var patch =
                "PATCH sample.cs\n" +
                "FIND:\n" +
                "        Console.WriteLine(\"Hello\");\n" +
                "REPLACE:\n" +
                "        Console.WriteLine(\"World\");\n";

            var resolved = PatchEngine.ResolvePatch(
                patch, tmpPath, "sample.cs",
                PatchFixtures.HelloSource, Encoding.UTF8, fromToolCall: false,
                (_, _) => { });

            Assert.NotNull(resolved);

            File.WriteAllText(tmpPath, PatchFixtures.HelloSource, Encoding.UTF8);
            var applyResult = PatchEngine.ApplyPatch(resolved, backupDir);
            backupPath = applyResult.BackupPath;

            Assert.True(applyResult.Success, $"ApplyPatch failed: {applyResult.Error}");
            Assert.Contains("Console.WriteLine(\"World\")", applyResult.UpdatedContent);
            Assert.DoesNotContain("Console.WriteLine(\"Hello\")", applyResult.UpdatedContent);
        }
        finally { TempHelpers.Cleanup(tmpPath, backupPath); }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// ResolvePatch tests — fuzzy match
// ─────────────────────────────────────────────────────────────────────────────

public sealed class ResolvePatchFuzzyMatchTests
{
    // ── Original test 3: FuzzyMatch_Confidence ──────────────────────────────
    // FIND contains "Helo, " (one 'l' missing). Exact normalized match fails;
    // fuzzy window match succeeds at ~98% similarity (well above the 85% threshold).

    [Fact]
    public void FuzzyMatch_Confidence()
    {
        var messages = new List<string>();

        var patch =
            "PATCH greeter.cs\n" +
            "FIND:\n" +
            "    public string Greet(string name)\n" +
            "    {\n" +
            "        return \"Helo, \" + name + \"!\";\n" +
            "    }\n" +
            "REPLACE:\n" +
            "    public string Greet(string name)\n" +
            "    {\n" +
            "        return \"Hi, \" + name + \"!\";\n" +
            "    }\n";

        var result = PatchEngine.ResolvePatch(
            patch, "greeter.cs", "greeter.cs",
            PatchFixtures.GreeterSource, Encoding.UTF8, fromToolCall: false,
            (msg, _) => messages.Add(msg));

        Assert.NotNull(result);
        Assert.Equal(PatchConfidence.Fuzzy, result.Confidence);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// ResolvePatch tests — failure cases (null returns)
// ─────────────────────────────────────────────────────────────────────────────

public sealed class ResolvePatchFailureTests
{
    // ── Original test 4: NoMatch_ReturnsNull ────────────────────────────────
    // FIND text does not exist in the file at all — ResolvePatch returns null
    // and the reporter receives a "FIND text not found" message.

    [Fact]
    public void NoMatch_ReturnsNull()
    {
        var messages = new List<string>();

        var patch =
            "PATCH sample.cs\n" +
            "FIND:\n" +
            "        Console.WriteLine(\"ThisTextDoesNotExist\");\n" +
            "REPLACE:\n" +
            "        Console.WriteLine(\"World\");\n";

        var result = PatchEngine.ResolvePatch(
            patch, "sample.cs", "sample.cs",
            PatchFixtures.HelloSource, Encoding.UTF8, fromToolCall: false,
            (msg, _) => messages.Add(msg));

        Assert.Null(result);
        Assert.Contains(messages, m => m.Contains("FIND text not found"));
    }

    // ── Original test 5: AmbiguousMatch_ReturnsNull ─────────────────────────
    // "return 30;" appears twice — ambiguity check must reject it.

    [Fact]
    public void AmbiguousMatch_ReturnsNull()
    {
        var messages = new List<string>();

        var patch =
            "PATCH config.cs\n" +
            "FIND:\n" +
            "return 30;\n" +
            "REPLACE:\n" +
            "return 60;\n";

        var result = PatchEngine.ResolvePatch(
            patch, "config.cs", "config.cs",
            PatchFixtures.DuplicateSource, Encoding.UTF8, fromToolCall: false,
            (msg, _) => messages.Add(msg));

        Assert.Null(result);
        Assert.Contains(messages, m => m.Contains("Ambiguous"));
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// ResolvePatch + ApplyPatch tests — multi-block
// ─────────────────────────────────────────────────────────────────────────────

public sealed class ResolvePatchMultiBlockTests
{
    // ── Original test 6: MultiBlock_BothApplied ─────────────────────────────
    // Two FIND/REPLACE pairs in a single PATCH block — both should resolve
    // and both replacements should appear in the final content.

    [Fact]
    public void MultiBlock_BothApplied()
    {
        string tmpPath = TempHelpers.TmpFile();
        string backupDir = TempHelpers.TmpBackupDir();
        string? backupPath = null;
        try
        {
            var patch =
                "PATCH service.cs\n" +
                "FIND:\n" +
                "    public void Start() { }\n" +
                "REPLACE:\n" +
                "    public void Start() { Console.WriteLine(\"Starting\"); }\n" +
                "FIND:\n" +
                "    public void Stop() { }\n" +
                "REPLACE:\n" +
                "    public void Stop() { Console.WriteLine(\"Stopping\"); }\n";

            var resolved = PatchEngine.ResolvePatch(
                patch, tmpPath, "service.cs",
                PatchFixtures.TwoStubSource, Encoding.UTF8, fromToolCall: false,
                (_, _) => { });

            Assert.NotNull(resolved);
            Assert.Equal(2, resolved.ResolvedBlocks.Count);

            File.WriteAllText(tmpPath, PatchFixtures.TwoStubSource, Encoding.UTF8);
            var applyResult = PatchEngine.ApplyPatch(resolved, backupDir);
            backupPath = applyResult.BackupPath;

            Assert.True(applyResult.Success, $"ApplyPatch failed: {applyResult.Error}");
            Assert.Contains("Console.WriteLine(\"Starting\")", applyResult.UpdatedContent);
            Assert.Contains("Console.WriteLine(\"Stopping\")", applyResult.UpdatedContent);
        }
        finally { TempHelpers.Cleanup(tmpPath, backupPath); }
    }

    // Atomic-fail guarantee that batched patch_file relies on: if ANY block's FIND is
    // missing, the whole patch resolves to null — no partial application.
    [Fact]
    public void MultiBlock_OneFindMissing_ResolvesToNull()
    {
        var patch =
            "PATCH service.cs\n" +
            "FIND:\n" +
            "    public void Start() { }\n" +
            "REPLACE:\n" +
            "    public void Start() { Console.WriteLine(\"Starting\"); }\n" +
            "FIND:\n" +
            "    public void DoesNotExist() { }\n" +   // no such method → whole patch must fail
            "REPLACE:\n" +
            "    public void DoesNotExist() { Console.WriteLine(\"x\"); }\n";

        var resolved = PatchEngine.ResolvePatch(
            patch, TempHelpers.TmpFile(), "service.cs",
            PatchFixtures.TwoStubSource, Encoding.UTF8, fromToolCall: false, (_, _) => { });

        Assert.Null(resolved); // all-or-nothing — the valid first block is NOT applied on its own
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// ReadFilePreservingEncoding tests
// ─────────────────────────────────────────────────────────────────────────────

public sealed class ReadFilePreservingEncodingTests
{
    // ── Original test 7: EncodingRoundTrip_BomPreserved ─────────────────────
    // Write a file with UTF-8 BOM, read it back with ReadFilePreservingEncoding,
    // patch it, and verify the BOM is preserved in the written output.

    [Fact]
    public void EncodingRoundTrip_BomPreserved()
    {
        string tmpPath = TempHelpers.TmpFile();
        string backupDir = TempHelpers.TmpBackupDir();
        string? backupPath = null;
        try
        {
            var bomEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
            File.WriteAllText(tmpPath, PatchFixtures.HelloSource, bomEncoding);

            var (content, detectedEncoding) = PatchEngine.ReadFilePreservingEncoding(tmpPath);

            Assert.IsType<UTF8Encoding>(detectedEncoding);
            var preamble = ((UTF8Encoding)detectedEncoding).GetPreamble();
            Assert.NotEmpty(preamble);

            var patch =
                "PATCH sample.cs\n" +
                "FIND:\n" +
                "        Console.WriteLine(\"Hello\");\n" +
                "REPLACE:\n" +
                "        Console.WriteLine(\"World\");\n";

            var resolved = PatchEngine.ResolvePatch(
                patch, tmpPath, "sample.cs",
                content, detectedEncoding, fromToolCall: false,
                (_, _) => { });

            Assert.NotNull(resolved);

            var applyResult = PatchEngine.ApplyPatch(resolved, backupDir);
            backupPath = applyResult.BackupPath;

            Assert.True(applyResult.Success, $"ApplyPatch failed: {applyResult.Error}");

            byte[] written = File.ReadAllBytes(tmpPath);

            Assert.True(written.Length >= 3, "File too short for BOM");
            Assert.Equal(0xEF, written[0]);
            Assert.Equal(0xBB, written[1]);
            Assert.Equal(0xBF, written[2]);
        }
        finally { TempHelpers.Cleanup(tmpPath, backupPath); }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// ApplyPatch tests — backup creation
// ─────────────────────────────────────────────────────────────────────────────

public sealed class ApplyPatchBackupTests
{
    // ── Original test 8: BackupCreation_FileExists ──────────────────────────
    // After ApplyPatch, a backup file should exist on disk at the path
    // returned in PatchApplyResult.BackupPath.

    [Fact]
    public void BackupCreation_FileExists()
    {
        string tmpPath = TempHelpers.TmpFile();
        string backupDir = TempHelpers.TmpBackupDir();
        string? backupPath = null;
        try
        {
            File.WriteAllText(tmpPath, PatchFixtures.HelloSource, Encoding.UTF8);

            var patch =
                "PATCH sample.cs\n" +
                "FIND:\n" +
                "        Console.WriteLine(\"Hello\");\n" +
                "REPLACE:\n" +
                "        Console.WriteLine(\"World\");\n";

            var resolved = PatchEngine.ResolvePatch(
                patch, tmpPath, "sample.cs",
                PatchFixtures.HelloSource, Encoding.UTF8, fromToolCall: false,
                (_, _) => { });

            Assert.NotNull(resolved);

            var applyResult = PatchEngine.ApplyPatch(resolved, backupDir);
            backupPath = applyResult.BackupPath;

            Assert.True(applyResult.Success, $"ApplyPatch failed: {applyResult.Error}");
            Assert.NotNull(backupPath);
            Assert.True(File.Exists(backupPath), $"Backup file not found at: {backupPath}");
        }
        finally { TempHelpers.Cleanup(tmpPath, backupPath); }
    }
}
