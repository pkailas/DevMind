// File: RunShellDescriptionTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// run_shell's description used to contradict itself across six lines. It opened with a
// statement of mechanics — "multi-line commands and here-strings are passed to PowerShell
// verbatim" — which reads as an endorsement, and the model reaches it first. The
// prohibition ("do not route file content through the shell") arrived only after a
// paragraph about timeouts and background jobs, by which point the endorsement had landed.
//
// A tool description is a prompt. Order and adjacency decide what it actually says, so
// those are what this pins — not merely that both sentences exist somewhere in the blob:
//
//   * the prohibition comes BEFORE the timeout/background material, not after it;
//   * the verbatim-passing fact is still stated (it matters for commands that legitimately
//     span lines) but is never left standing alone as a capability — its qualifier sits in
//     the same sentence;
//   * the alternative is named, by tool: create_file / write_file / patch_file;
//   * the `command` parameter description carries the prohibition too, because that is the
//     text a model re-reads while composing the argument.
//
// Asserting only that "here-strings are passed verbatim" is absent would be satisfied by
// deleting the whole description, so every check below is paired with what must survive.

using System.ComponentModel;
using System.Reflection;
using Xunit;

namespace DevMind.McpServer.Tests
{
    public class RunShellDescriptionTests
    {
        private static MethodInfo RunShellMethod()
        {
            var method = typeof(DevMindTools).GetMethod("RunShell", BindingFlags.Public | BindingFlags.Instance);
            Assert.True(method != null, "DevMindTools.RunShell not found — the guard would be vacuous.");
            return method!;
        }

        private static string ToolDescription()
        {
            var attr = RunShellMethod().GetCustomAttribute<DescriptionAttribute>();
            Assert.True(attr != null, "run_shell has no [Description] — the guard would be vacuous.");
            return attr!.Description;
        }

        private static string CommandParameterDescription()
        {
            ParameterInfo? p = RunShellMethod().GetParameters()
                .FirstOrDefault(x => x.Name == "command");
            Assert.True(p != null, "run_shell has no 'command' parameter.");

            var attr = p!.GetCustomAttribute<DescriptionAttribute>();
            Assert.True(attr != null, "run_shell's 'command' parameter has no [Description].");
            return attr!.Description;
        }

        // ── What must survive ───────────────────────────────────────────────────

        [Fact]
        public void ToolDescription_StillCarriesItsRealContent()
        {
            string d = ToolDescription();

            Assert.Contains("PowerShell", d, StringComparison.Ordinal);
            Assert.Contains("timeout_seconds", d, StringComparison.Ordinal);
            Assert.Contains("background=true", d, StringComparison.Ordinal);
            Assert.Contains("shell_job_status", d, StringComparison.Ordinal);
            Assert.Contains("list_files", d, StringComparison.Ordinal);
            Assert.Contains("find_in_files", d, StringComparison.Ordinal);

            // The mechanical fact is still stated — it is true and it matters for a command
            // that genuinely spans lines.
            Assert.Contains("verbatim", d, StringComparison.Ordinal);
        }

        // ── What must no longer read as an endorsement ──────────────────────────

        [Fact]
        public void TheVerbatimFact_IsQualifiedInTheSameSentence_NotLeftStandingAlone()
        {
            string d = ToolDescription();

            // The sentence containing "verbatim" must itself contain the limit. If the
            // qualifier drifts into a later sentence, the endorsement is back.
            string sentence = SentenceContaining(d, "verbatim");

            Assert.Contains("NOT a file-writing mechanism", sentence, StringComparison.Ordinal);
            Assert.Contains("here-string", sentence, StringComparison.Ordinal);
        }

        [Fact]
        public void TheProhibition_PrecedesTheTimeoutAndBackgroundMaterial()
        {
            string d = ToolDescription();

            int prohibition = d.IndexOf("Do not route file content", StringComparison.Ordinal);
            int verbatim = d.IndexOf("verbatim", StringComparison.Ordinal);
            int timeouts = d.IndexOf("Default timeout", StringComparison.Ordinal);

            Assert.True(prohibition >= 0, "the file-content prohibition is gone from run_shell's description");
            Assert.True(timeouts >= 0, "the timeout guidance is gone from run_shell's description");

            // The original defect, stated as an ordering: the mechanic led, and the
            // prohibition sat on the far side of an unrelated paragraph.
            Assert.True(prohibition < verbatim,
                "the verbatim-passing mechanic is stated before the prohibition again — it reads as an endorsement");
            Assert.True(verbatim < timeouts,
                "the prohibition and the mechanic are separated from each other by the timeout/background paragraph");
        }

        [Fact]
        public void TheAlternativeIsNamedByTool_InBothDescriptions()
        {
            foreach (string text in new[] { ToolDescription(), CommandParameterDescription() })
            {
                Assert.Contains("create_file", text, StringComparison.Ordinal);
                Assert.Contains("write_file", text, StringComparison.Ordinal);
                Assert.Contains("patch_file", text, StringComparison.Ordinal);
            }
        }

        // ── The parameter description: what the model re-reads at composition time ──

        [Fact]
        public void CommandParameter_KeepsTheNewlineFact_AndCarriesTheProhibition()
        {
            string p = CommandParameterDescription();

            // Survives: the reason a multi-line command works at all.
            Assert.Contains("Newlines are preserved", p, StringComparison.Ordinal);

            // Added: the prohibition, at the point the argument is actually being written.
            Assert.Contains("here-string", p, StringComparison.Ordinal);
            Assert.Contains("Do NOT assemble file content here", p, StringComparison.Ordinal);
        }

        /// <summary>The sentence (period-delimited) containing the first occurrence of
        /// <paramref name="needle"/>. Em dashes and colons do not end a sentence, so a
        /// qualifier attached with either still counts as "same sentence".</summary>
        private static string SentenceContaining(string text, string needle)
        {
            int at = text.IndexOf(needle, StringComparison.Ordinal);
            Assert.True(at >= 0, $"\"{needle}\" not found in the description at all");

            int start = text.LastIndexOf('.', at);
            start = start < 0 ? 0 : start + 1;

            int end = text.IndexOf('.', at);
            end = end < 0 ? text.Length : end;

            return text.Substring(start, end - start);
        }
    }
}
