// File: AgenticLoopTurnClockParityTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Guards the placement of the turn-clock increment (LlmClient.IncrementTurn) so the next
// front-end port cannot reintroduce the per-iteration bug.
//
// The bug, introduced in 64f3a5c (TUI) and copied in ba4cdbe (headless) and mirrored in
// DevMind.Cli: an agentic loop called IncrementTurn() at the top of its while(true) body —
// once per ITERATION — so the context-aging clock ran ~an order of magnitude faster than
// dropAge was tuned for. The fix moves the increment to the turn boundary (once per user
// send, and not again on an ask_caller answer — see TurnClock).
//
// The behavioural tests pin the DECISION (TurnClockTests) and the per-port placement for the
// ports that exist today. This source-derived guard (the same shape as
// ToolCatalogueRegistryParityTests) pins the invariant for ANY port, now or next year:
//
//   IncrementTurn() must not be called from inside a while/for loop body — UNLESS that loop
//   body also calls ClearHistory() (the per-iteration turn reset that makes a per-iteration
//   increment correct, as DocumentDigester's per-chunk loop does).
//
// A new port that drops the increment at the top of an agentic loop, with no reset, fails
// the build here instead of silently degrading every long run.
//
// Robustness: the scanner is a small comment/string-stripped brace matcher, and it is
// SELF-TESTED below on embedded snippets — it must FIRE on the buggy pattern and PASS on
// the correct boundary and the reset-bearing loop — so the test cannot pass vacuously.

using System;
using System.IO;
using System.Linq;
using Xunit;

namespace DevMind.Core.Tests
{
    public class AgenticLoopTurnClockParityTests
    {
        // ── The scanner ─────────────────────────────────────────────────────────

        private sealed class Frame
        {
            public bool IsLoop;
            public bool HasReset;
        }

        // Strips // and /* */ comments and string/char/verbatim literals so braces and
        // keywords inside them cannot confuse the brace matcher.
        private static string StripCommentsAndStrings(string src)
        {
            var sb = new System.Text.StringBuilder(src.Length);
            int i = 0, n = src.Length;
            while (i < n)
            {
                char c = src[i];
                if (c == '/' && i + 1 < n && src[i + 1] == '/')
                {
                    while (i < n && src[i] != '\n') i++;
                }
                else if (c == '/' && i + 1 < n && src[i + 1] == '*')
                {
                    int end = src.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    int stop = end < 0 ? n : end + 2;
                    int newlines = 0;
                    for (int k = i; k < stop; k++) if (src[k] == '\n') newlines++;
                    for (int k = 0; k < newlines; k++) sb.Append('\n');
                    i = stop;
                }
                else if (c == '"' && i > 0 && src[i - 1] == '@')
                {
                    sb.Append('"'); i++;
                    while (i < n)
                    {
                        if (src[i] == '"')
                        {
                            if (i + 1 < n && src[i + 1] == '"') { sb.Append("\"\""); i += 2; continue; }
                            sb.Append('"'); i++; break;
                        }
                        if (src[i] == '\n') sb.Append('\n');
                        i++;
                    }
                }
                else if (c == '$' && i + 1 < n && src[i + 1] == '"')
                {
                    sb.Append('"'); i += 2;
                    while (i < n)
                    {
                        if (src[i] == '\\' && i + 1 < n) { i += 2; continue; }
                        if (src[i] == '{' && i + 1 < n && src[i + 1] == '{') { i += 2; continue; }
                        if (src[i] == '}' && i + 1 < n && src[i + 1] == '}') { i += 2; continue; }
                        if (src[i] == '"') { sb.Append('"'); i++; break; }
                        i++;
                    }
                }
                else if (c == '"')
                {
                    sb.Append('"'); i++;
                    while (i < n)
                    {
                        if (src[i] == '\\' && i + 1 < n) { i += 2; continue; }
                        if (src[i] == '\n') sb.Append('\n');
                        if (src[i] == '"') { sb.Append('"'); i++; break; }
                        i++;
                    }
                }
                else if (c == '\'')
                {
                    i++;
                    while (i < n)
                    {
                        if (src[i] == '\\' && i + 1 < n) { i += 2; continue; }
                        if (src[i] == '\'') { i++; break; }
                        if (src[i] == '\n') sb.Append('\n');
                        i++;
                    }
                }
                else
                {
                    sb.Append(c);
                    if (c == '\n') { }
                    i++;
                }
            }
            return sb.ToString();
        }

        // For each IncrementTurn() CALL (not the `void IncrementTurn()` definition), reports the
        // 1-based line and whether it sits inside a while/for body that also calls ClearHistory().
        // Returns (line, inLoop, loopHasReset).
        private static (int line, bool inLoop, bool loopHasReset)[] FindBareIncrementTurnInLoops(string source)
        {
            string s = StripCommentsAndStrings(source);
            var frames = new List<Frame>();   // used as a stack; index 0 = outermost block
            bool pendingLoop = false;
            var results = new List<(int, bool, bool)>();

            int i = 0, n = s.Length;
            int line = 1;
            while (i < n)
            {
                char c = s[i];
                if (c == '\n') { line++; i++; continue; }

                if (char.IsLetter(c) || c == '_')
                {
                    int start = i;
                    while (i < n && (char.IsLetterOrDigit(s[i]) || s[i] == '_')) i++;
                    string word = s[start..i];

                    if (word == "while" || word == "for")
                    {
                        pendingLoop = true;
                    }
                    else if (word == "ClearHistory")
                    {
                        // A reset anywhere inside a loop body counts; mark every open loop frame.
                        foreach (var fr in frames) if (fr.IsLoop) fr.HasReset = true;
                    }
                    else if (word == "IncrementTurn")
                    {
                        int after = i;
                        while (after < n && char.IsWhiteSpace(s[after])) after++;
                        bool isCall = after < n && s[after] == '(';
                        int before = start - 1;
                        while (before >= 0 && char.IsWhiteSpace(s[before])) before--;
                        // The definition is `public void IncrementTurn()` — a call is
                        // `<expr>.IncrementTurn()` (preceded by '.') or bare `IncrementTurn()`.
                        bool isDefinition = before >= 0 && s[before] != '.' && WordEndingAt(s, before) == "void";
                        if (isCall && !isDefinition)
                        {
                            Frame? innermostLoop = null;
                            for (int f = frames.Count - 1; f >= 0; f--)
                                if (frames[f].IsLoop) { innermostLoop = frames[f]; break; }
                            results.Add((line, innermostLoop != null, innermostLoop != null && innermostLoop.HasReset));
                        }
                    }
                    continue;
                }

                if (c == '{')
                {
                    frames.Add(new Frame { IsLoop = pendingLoop, HasReset = false });
                    pendingLoop = false;
                    i++;
                    continue;
                }
                if (c == '}')
                {
                    if (frames.Count > 0) frames.RemoveAt(frames.Count - 1);
                    pendingLoop = false;
                    i++;
                    continue;
                }
                if (c == ';')
                {
                    pendingLoop = false;
                    i++;
                    continue;
                }
                i++;
            }
            return results.ToArray();
        }

        // Returns the identifier ending at s[lastIdx] (inclusive), or "" if lastIdx is not an
        // identifier character. (e.g. s="void IncrementTurn", lastIdx=index of 'd' → "void").
        private static string WordEndingAt(string s, int lastIdx)
        {
            if (lastIdx < 0 || !(char.IsLetterOrDigit(s[lastIdx]) || s[lastIdx] == '_')) return "";
            int j = lastIdx;
            while (j >= 0 && (char.IsLetterOrDigit(s[j]) || s[j] == '_')) j--;
            return s[(j + 1)..(lastIdx + 1)];
        }

        // ── Repo-wide guard ─────────────────────────────────────────────────────

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

        private static IEnumerable<string> ProductionSourceFiles()
        {
            string root = RepoRoot();
            var all = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories);
            return all.Where(p =>
            {
                string rel = p.Substring(root.Length).Replace('\\', '/');
                foreach (string bad in new[] { "/_archive/", "/bin/", "/obj/", "/dist/", "/publish/", "/tools/", "/.vs/", "/.devmind/", "/CodeReviewBenchmarkWithBugs/" })
                    if (rel.Contains(bad, StringComparison.OrdinalIgnoreCase)) return false;
                foreach (string testDir in new[] { ".Core.Tests/", ".Cli.Tests/", ".McpServer.Tests/", ".TUI.Tests/" })
                    if (rel.Contains(testDir, StringComparison.OrdinalIgnoreCase)) return false;
                return true;
            });
        }

        [Fact]
        public void NoBareIncrementTurnInAnyAgenticLoop()
        {
            var violations = new List<string>();
            int filesScanned = 0;
            foreach (string path in ProductionSourceFiles())
            {
                filesScanned++;
                string src = File.ReadAllText(path);
                if (!src.Contains("IncrementTurn(", StringComparison.Ordinal)) continue;
                foreach (var (line, inLoop, loopHasReset) in FindBareIncrementTurnInLoops(src))
                {
                    if (inLoop && !loopHasReset)
                        violations.Add($"{path.Substring(RepoRoot().Length).Replace('\\', '/')}:{line}");
                }
            }

            Assert.True(filesScanned > 0, "Scanned zero source files — RepoRoot() resolved wrong; the guard is vacuous.");
            Assert.True(violations.Count == 0,
                "IncrementTurn() called from inside a while/for loop body WITHOUT a ClearHistory() " +
                "in that body — the per-iteration turn-clock bug. Move the increment to the turn " +
                "boundary (TurnClock), or add a per-iteration ClearHistory() if the loop resets the " +
                "turn each pass (DocumentDigester).\nOffenders: " + string.Join(", ", violations));
        }

        // ── Self-tests: the scanner must actually detect the bug (no vacuous pass) ──

        private const string BadLoop =
            "class X { void F(ILlmClient c) { while (true) { c.IncrementTurn(); g(); } } }";

        private const string GoodBoundary =
            "class X { void F(ILlmClient c) { c.IncrementTurn(); while (true) { g(); } } }";

        private const string ResetBearingLoop =
            "class X { void F(ILlmClient c) { while (true) { c.ClearHistory(); c.IncrementTurn(); } } }";

        [Fact]
        public void Scanner_FiresOnTheBuggyPattern()
        {
            var r = FindBareIncrementTurnInLoops(BadLoop);
            var call = Assert.Single(r);
            Assert.True(call.inLoop, "scanner failed to detect IncrementTurn inside the while body");
            Assert.False(call.loopHasReset, "scanner wrongly thought the loop has a ClearHistory");
        }

        [Fact]
        public void Scanner_PassesOnTheTurnBoundary()
        {
            var r = FindBareIncrementTurnInLoops(GoodBoundary);
            var call = Assert.Single(r);
            Assert.False(call.inLoop, "scanner wrongly thought the boundary increment is in a loop");
        }

        [Fact]
        public void Scanner_PassesOnAResetBearingLoop()
        {
            var r = FindBareIncrementTurnInLoops(ResetBearingLoop);
            var call = Assert.Single(r);
            Assert.True(call.inLoop);
            Assert.True(call.loopHasReset, "scanner failed to see the ClearHistory in the same loop body");
        }

        [Fact]
        public void Scanner_IgnoresTheDefinitionAndComments()
        {
            // The definition `public void IncrementTurn()` and a commented-out call must not count.
            const string src =
                "class C { public void IncrementTurn() { } void F() { /* c.IncrementTurn(); */ while(true){ g(); } } }";
            Assert.Empty(FindBareIncrementTurnInLoops(src));
        }
    }
}
