// File: DefaultSystemPromptTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// The fallback prompt — the one used when no system-prompt.md has been authored — existed
// as the same literal in four places: TuiOptions, CliOptions, HeadlessOptions and
// LlmClient. Four copies is four places to drift, and none of them carried the
// markdown-formatting guidance the authored prompt has had since 2026-09-20.
//
// That costs nothing on a machine that has the authored file, because the fallback is
// never reached there. It costs everything on a machine that does not — a fresh install
// or a colleague's box gets prose answers, no tables and no fenced code, which is the
// exact gap the Qwen Code comparison measured. A defect invisible where it is written and
// visible only where nobody is looking is one a test has to hold.
//
// So the last guard here is source-derived: it reads the production tree and fails when a
// second literal appears. That is the one that stops the four copies coming back.

using Xunit;

namespace DevMind.Core.Tests
{
    public class DefaultSystemPromptTests
    {
        [Fact]
        public void Default_CarriesTheFormattingGuidance_NotJustTheRoleSentence()
        {
            Assert.Contains("markdown", DefaultPrompts.System, StringComparison.Ordinal);
            Assert.Contains("fenced", DefaultPrompts.System, StringComparison.Ordinal);

            // The specific shapes, because "format as markdown" alone is what produced
            // prose answers with no tables in the first place.
            Assert.Contains("tables for comparisons", DefaultPrompts.System, StringComparison.Ordinal);
            Assert.Contains("Lead with the result, then the detail.", DefaultPrompts.System, StringComparison.Ordinal);

            // The role sentence survives the addition.
            Assert.StartsWith("You are a helpful coding assistant. Be concise and precise.",
                DefaultPrompts.System, StringComparison.Ordinal);
        }

        // The formatting paragraph is worded identically to the authored prompt's, so the
        // built-in floor and the authored prompt cannot disagree about what a well-formed
        // answer looks like. Asserted against the tracked copy, which is the source of
        // truth (prompts/system-prompt.md); the live %APPDATA% copy is reconciled against
        // that file by deploy.ps1, so pinning the tracked one pins both.
        [Fact]
        public void FormattingGuidance_MatchesTheAuthoredPromptWordForWord()
        {
            string authored = File.ReadAllText(Path.Combine(RepoRoot(), "prompts", "system-prompt.md"));
            Assert.True(authored.Length > 0, "prompts/system-prompt.md is empty — RepoRoot() resolved wrong, the guard is vacuous");

            const string Paragraph =
                "Format answers as markdown: tables for comparisons, `inline code` for\n" +
                "identifiers, paths and commands, fenced blocks with a language tag for code.\n" +
                "Lead with the result, then the detail.";

            Assert.Contains(Paragraph, DefaultPrompts.System.Replace("\r\n", "\n"), StringComparison.Ordinal);
            Assert.Contains(Paragraph, authored.Replace("\r\n", "\n"), StringComparison.Ordinal);
        }

        [Fact]
        public void HeadlessOptions_DefaultsToTheSharedConstant()
        {
            Assert.Equal(DefaultPrompts.System, new HeadlessOptions().SystemPrompt);
        }

        // The guard that stops a fifth copy. Scans production source only: a test project
        // may legitimately hold prompt text as a fixture (SystemPromptFileTests uses a
        // truncated variant to exercise the size warning), and that is not a fallback.
        [Fact]
        public void TheDefaultPromptLiteral_ExistsExactlyOnceInProductionSource()
        {
            const string Literal = "You are a helpful coding assistant";

            var offenders = new List<string>();
            int scanned = 0;

            foreach (string file in ProductionSourceFiles())
            {
                scanned++;
                string[] lines = File.ReadAllLines(file);
                for (int i = 0; i < lines.Length; i++)
                {
                    if (!lines[i].Contains(Literal, StringComparison.Ordinal)) continue;
                    if (lines[i].TrimStart().StartsWith("//", StringComparison.Ordinal)) continue;
                    offenders.Add($"{Path.GetFileName(file)}:{i + 1}");
                }
            }

            Assert.True(scanned > 0, "Scanned zero source files — RepoRoot() resolved wrong; the guard is vacuous.");
            Assert.True(offenders.Count == 1,
                "The default system prompt must exist as a literal in exactly one place, " +
                "DefaultPrompts.cs. Anything else is a copy that will drift out of step with it " +
                "and will be missing the formatting guidance. Found: " + string.Join(", ", offenders));
            Assert.StartsWith("DefaultPrompts.cs:", offenders[0], StringComparison.Ordinal);
        }

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
            foreach (string project in new[] { "DevMind.Core", "DevMind.TUI", "DevMind.Cli", "DevMind.McpServer" })
            {
                string dir = Path.Combine(root, project);
                if (!Directory.Exists(dir)) continue;

                foreach (string path in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
                {
                    string rel = path.Substring(dir.Length).Replace(Path.DirectorySeparatorChar, '/');
                    if (rel.Contains("/bin/", StringComparison.OrdinalIgnoreCase)) continue;
                    if (rel.Contains("/obj/", StringComparison.OrdinalIgnoreCase)) continue;
                    yield return path;
                }
            }
        }
    }
}
