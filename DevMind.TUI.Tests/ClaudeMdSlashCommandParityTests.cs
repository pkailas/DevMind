// File: ClaudeMdSlashCommandParityTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// CLAUDE.md's slash-command table was hand-maintained and drifted badly: it listed
// 14 commands while the registry held 28. Everything it omitted — /cache, /prompt,
// /compact, /library, /lsp, /restart, /resolve, /image, /output-lines and the rest —
// was invisible to anyone (human or agent) reading the file as the reference.
//
// This is the same argument ToolCatalogueRegistryParityTests makes for the tool
// catalogue, and the fix is the same shape: derive the expected set structurally
// from the registry instead of trusting a list someone typed.
//
// Deliberately narrow: it pins the SET OF COMMAND NAMES, nothing else. It does NOT
// compare the prose. A doc table is not a runtime contract, and pinning the wording
// would make every improvement to a help string a build failure in a Markdown file —
// which trains people to stop improving help strings. The failure mode worth catching
// is "a command exists and the reference does not mention it", and that is exactly
// what a name-set comparison catches.

using Xunit;

namespace DevMind.TUI.Tests
{
    public class ClaudeMdSlashCommandParityTests
    {
        private static string RepoRoot()
        {
            // DevMind.TUI.Tests/bin/<cfg>/<tfm>/ → up 3 = DevMind.TUI.Tests → up 1 = repo root.
            string dir = AppContext.BaseDirectory;
            for (int up = 0; up < 4; up++)
            {
                var d = new DirectoryInfo(dir);
                if (d.Parent == null) break;
                dir = d.Parent.FullName;
            }
            return dir;
        }

        /// <summary>
        /// Command names named in the "## TUI Slash Commands" table — the leading token of
        /// each row's first cell, which the table writes as a backticked usage string
        /// (`/resume &lt;n&gt;`). Group heading rows (**Session**) carry no backtick and are
        /// skipped naturally.
        /// </summary>
        private static HashSet<string> DocumentedCommands(out int rowsSeen)
        {
            string path = Path.Combine(RepoRoot(), "CLAUDE.md");
            Assert.True(File.Exists(path), $"CLAUDE.md not found at {path} — RepoRoot() resolved wrong.");

            string[] lines = File.ReadAllLines(path);
            int start = Array.FindIndex(lines, l => l.StartsWith("## TUI Slash Commands", StringComparison.Ordinal));
            Assert.True(start >= 0, "CLAUDE.md has no '## TUI Slash Commands' section.");

            var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            rowsSeen = 0;

            for (int i = start + 1; i < lines.Length; i++)
            {
                string line = lines[i];
                if (line.StartsWith("## ", StringComparison.Ordinal)) break;   // next section
                if (!line.StartsWith("| `", StringComparison.Ordinal)) continue;

                rowsSeen++;
                // "| `/resume <n>` | ..." → first whitespace-delimited token after the backtick.
                string cell = line.Substring(3);
                int end = cell.IndexOfAny(new[] { ' ', '`' });
                if (end > 0) found.Add(cell.Substring(0, end));
            }

            return found;
        }

        [Fact]
        public void EveryRegisteredCommandIsDocumented_AndNothingIsDocumentedThatIsNotRegistered()
        {
            var registered = SlashCommand.ListCommands()
                .Select(c => c.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            Assert.True(registered.Count > 0, "The slash-command registry is empty — the guard would be vacuous.");

            var documented = DocumentedCommands(out int rowsSeen);
            Assert.True(rowsSeen > 0, "Parsed zero table rows out of CLAUDE.md — the guard would be vacuous.");

            var undocumented = registered.Except(documented, StringComparer.OrdinalIgnoreCase).OrderBy(x => x).ToList();
            var phantom = documented.Except(registered, StringComparer.OrdinalIgnoreCase).OrderBy(x => x).ToList();

            Assert.True(undocumented.Count == 0,
                "Registered in SlashCommand.RegisterBuiltinCommands but missing from the CLAUDE.md " +
                "slash-command table: " + string.Join(", ", undocumented));

            Assert.True(phantom.Count == 0,
                "Listed in the CLAUDE.md slash-command table but not registered: " + string.Join(", ", phantom));
        }
    }
}
