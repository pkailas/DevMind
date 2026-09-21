// File: DiagnosticRoutingParityTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// DevMindLogTests proves DevMindLog.Write is not [Conditional] and therefore survives a
// Release build. That is only half the fix: it is worth nothing if a diagnostic site goes
// back to calling Debug.WriteLine, which the compiler deletes from Release along with its
// string literal. Nothing about that reversion is visible in a Debug test run — the line
// still appears in the Output window — so it needs a structural guard, the same shape as
// ToolCatalogueRegistryParityTests and AgenticLoopTurnClockParityTests.
//
// The split this pins:
//
//   * Diagnostics worth reading after the fact — a swallowed failure, or the once-per-
//     session state that makes such a failure readable — go through DevMindLog.
//   * Per-turn and per-request tracing stays on Debug.WriteLine, marked "[DevMind TRACE]".
//     Those fire many times per turn, are for attaching a debugger rather than reading
//     later, and routing them to a file would drown it and slow the hot path.
//
// Expressed as one invariant: inside DevMind.Core, every surviving Debug.WriteLine is a
// TRACE line. A new swallowed-failure diagnostic written the old way fails here.

using Xunit;

namespace DevMind.Core.Tests
{
    public class DiagnosticRoutingParityTests
    {
        private static string RepoRoot()
        {
            // DevMind.Core.Tests/bin/<cfg>/<tfm>/ → up 3 = DevMind.Core.Tests → up 1 = repo root.
            string dir = AppContext.BaseDirectory;
            for (int up = 0; up < 4; up++)
            {
                var d = new DirectoryInfo(dir);
                if (d.Parent == null) break;
                dir = d.Parent.FullName;
            }
            return dir;
        }

        private static IEnumerable<string> CoreSourceFiles()
        {
            string core = Path.Combine(RepoRoot(), "DevMind.Core");
            return Directory.EnumerateFiles(core, "*.cs", SearchOption.AllDirectories)
                .Where(p =>
                {
                    string rel = p.Substring(core.Length).Replace(Path.DirectorySeparatorChar, '/');
                    return !rel.Contains("/bin/", StringComparison.OrdinalIgnoreCase)
                        && !rel.Contains("/obj/", StringComparison.OrdinalIgnoreCase);
                });
        }

        /// <summary>
        /// Every diagnostic converted away from Debug.WriteLine, keyed by the message prefix
        /// that identifies it in the source. Each must still be emitted, and emitted through
        /// DevMindLog — the literal itself is what vanishes from a Release build if it is not.
        /// </summary>
        public static IEnumerable<object[]> ConvertedSites() => new[]
        {
            // Failures that are otherwise swallowed by the surrounding catch.
            new object[] { "DevMind.Core/JsonlTrainingLogger.cs", "[JsonlTrainingLogger] Write failed:" },
            new object[] { "DevMind.Core/NearlineCache.cs",       "[NearlineCache] disk read failed for" },
            new object[] { "DevMind.Core/NearlineCache.cs",       "[NearlineCache] session dir delete failed:" },
            new object[] { "DevMind.Core/NearlineCache.cs",       "[NearlineCache] spill write failed for" },
            new object[] { "DevMind.Core/NearlineCache.cs",       "[NearlineCache] file delete failed for" },
            new object[] { "DevMind.Core/NearlineCache.cs",       "[NearlineCache] cleanup of" },
            new object[] { "DevMind.Core/NearlineCache.cs",       "[NearlineCache] cleanup scan failed:" },
            new object[] { "DevMind.Core/PatchBackupSweeper.cs",  "[PatchBackupSweeper] delete of" },
            new object[] { "DevMind.Core/PatchBackupSweeper.cs",  "[PatchBackupSweeper] sweep failed:" },
            new object[] { "DevMind.Core/LlmClient.cs",           "[DevMind] Context detection WARNING: could not read n_ctx" },
            new object[] { "DevMind.Core/LlmClient.cs",           "[DevMind] Context detection WARNING: exception during detection" },
            new object[] { "DevMind.Core/LlmClient.cs",           "not recognized" },
            new object[] { "DevMind.Core/LlmClient.cs",           "[DevMind] Server type auto-detect skipped" },
            new object[] { "DevMind.Core/LlmClient.cs",           "[DevMind] GenerateSummaryAsync failed:" },
            new object[] { "DevMind.Core/LlmClient.cs",           "[DevMind] tool-output spill write failed:" },

            // Once-per-Configure() resolved state, without which a failure line has no context.
            new object[] { "DevMind.Core/LlmClient.cs",           "[DevMind] Context strategy hint:" },
            new object[] { "DevMind.Core/LlmClient.cs",           "[DevMind] Using manual context size:" },
            new object[] { "DevMind.Core/LlmClient.cs",           "[DevMind] Server type forced via DEVMIND_SERVER_TYPE:" },
            new object[] { "DevMind.Core/LlmClient.cs",           "[DevMind] Context detection: server=llama-server" },
            new object[] { "DevMind.Core/LlmClient.cs",           "[DevMind] Context detection: server=LM Studio" },
            new object[] { "DevMind.Core/LlmClient.cs",           "[DevMind] Context detection: server=vLLM" },
            new object[] { "DevMind.Core/LlmClient.cs",           "[DevMind] Context detection: server=Custom" },
            new object[] { "DevMind.Core/LlmClient.cs",           "[DevMind] Context detection: unknown server type" },
            new object[] { "DevMind.Core/LlmClient.cs",           "[DevMind] Context detection: n_ctx=" },
            new object[] { "DevMind.Core/LlmClient.cs",           "[DevMind] Server type auto-detected: vLLM" },
        };

        [Theory]
        [MemberData(nameof(ConvertedSites))]
        public void ConvertedDiagnosticsRouteThroughDevMindLog(string relativePath, string marker)
        {
            string path = Path.Combine(RepoRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(path), $"source not found: {path} — RepoRoot() resolved wrong, the guard is vacuous.");

            string[] lines = File.ReadAllLines(path);

            // The emit is the line carrying the marker, or — for a call split across lines —
            // the nearest preceding line that opens the call.
            int i = Array.FindIndex(lines, l => l.Contains(marker, StringComparison.Ordinal));
            Assert.True(i >= 0, $"{relativePath} no longer emits anything matching \"{marker}\".");

            string emit = lines[i];
            if (!emit.Contains("DevMindLog.Write(", StringComparison.Ordinal)
                && !emit.Contains("Debug.WriteLine(", StringComparison.Ordinal)
                && i > 0)
                emit = lines[i - 1] + " " + emit;

            Assert.True(emit.Contains("DevMindLog.Write(", StringComparison.Ordinal),
                $"{relativePath}: \"{marker}\" is not emitted through DevMindLog — it will be " +
                "compiled out of a Release build. Line: " + emit.Trim());
        }

        [Fact]
        public void EverySurvivingDebugWriteLineInCoreIsMarkedAsTracing()
        {
            var offenders = new List<string>();
            int scanned = 0;

            foreach (string file in CoreSourceFiles())
            {
                scanned++;

                // DevMindLog's own call is the deliberate dual-emit that gives debugger
                // users the Output-window line; it is the mechanism, not a site.
                if (Path.GetFileName(file).Equals("DevMindLog.cs", StringComparison.OrdinalIgnoreCase))
                    continue;

                string[] lines = File.ReadAllLines(file);
                for (int i = 0; i < lines.Length; i++)
                {
                    if (!lines[i].Contains("Debug.WriteLine(", StringComparison.Ordinal)) continue;
                    if (lines[i].TrimStart().StartsWith("//", StringComparison.Ordinal)) continue;

                    // The message may sit on the call's line or the next one.
                    string text = lines[i] + (i + 1 < lines.Length ? " " + lines[i + 1] : "");
                    if (text.Contains("[DevMind TRACE]", StringComparison.Ordinal)) continue;

                    offenders.Add($"{Path.GetFileName(file)}:{i + 1}");
                }
            }

            Assert.True(scanned > 0, "Scanned zero source files — RepoRoot() resolved wrong; the guard is vacuous.");
            Assert.True(offenders.Count == 0,
                "Debug.WriteLine in DevMind.Core without the [DevMind TRACE] marker. Debug.WriteLine is " +
                "[Conditional(\"DEBUG\")] and is deleted from the Release build that actually ships, so a " +
                "diagnostic worth reading after the fact belongs on DevMindLog.Write. Offenders: " +
                string.Join(", ", offenders));
        }
    }
}
