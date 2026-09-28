// File: ReadToolGolden.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Golden-output cases for the read-side file tools (read_file incl. git, grep_file,
// find_in_files, list_files, diff_file), shared by THREE test projects so every case runs
// through every adapter on the same fixture:
//   DevMind.Core.Tests    — BufferedAgenticHost (CLI + headless jobs)
//   DevMind.TUI.Tests     — TuiAgenticHost
//   DevMind.McpServer.Tests — the MCP tools (DevMindTools)
// This file is linked into the other two projects (<Compile Include Link>), so it must
// compile under both xUnit v2 and v3 and with nullable on or off.
//
// Expected text is written once. The ONLY documented difference between surfaces is the
// tool vocabulary in messages and headers, written as placeholders:
//   {READ} {GREP} {FIND} {DIFF}  →  READ GREP FIND DIFF (agent)  |  read_file grep_file
//                                   find_in_files diff_file (MCP)
// {ROOT} is the fixture root, {S} the directory separator, and {NL} Environment.NewLine
// (grep/find/list and numbered range lines are joined with AppendLine on every surface).

#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace DevMind.ReadToolGolden
{
    /// <summary>One surface's read-side tools, as its caller sees them.</summary>
    public interface IReadToolAdapter
    {
        /// <summary>True for the MCP vocabulary (read_file, grep_file, …).</summary>
        bool IsMcp { get; }
        Task<string> Read(string filename, int? startLine = null, int? endLine = null, bool forceFull = false);
        Task<string> Grep(string pattern, string filename, int? startLine = null, int? endLine = null);
        Task<string> Find(string pattern, string glob, int? startLine = null, int? endLine = null);
        Task<string> List(string glob, bool recursive = true);
        Task<string> Diff(string filename);
    }

    /// <summary>Adapter over an agent host (BufferedAgenticHost or TuiAgenticHost), called the
    /// way AgenticExecutor calls it (an absent start/end line arrives as 0).</summary>
    public sealed class AgentHostAdapter : IReadToolAdapter
    {
        private readonly IAgenticHost _host;
        public AgentHostAdapter(IAgenticHost host) { _host = host; }
        public bool IsMcp => false;
        public Task<string> Read(string filename, int? startLine = null, int? endLine = null, bool forceFull = false)
            => _host.LoadFileContentAsync(filename, startLine ?? 0, endLine ?? 0, forceFull);
        public Task<string> Grep(string pattern, string filename, int? startLine = null, int? endLine = null)
            => _host.GrepFileAsync(pattern, filename, startLine, endLine);
        public Task<string> Find(string pattern, string glob, int? startLine = null, int? endLine = null)
            => _host.FindInFilesAsync(pattern, glob, startLine, endLine);
        public Task<string> List(string glob, bool recursive = true)
            => _host.ListFilesAsync(glob, recursive, CancellationToken.None);
        public Task<string> Diff(string filename) => _host.GetFileDiffAsync(filename);
    }

    /// <summary>
    /// A small repo on disk: nested dirs, two same-named files, a 450-line file, a UTF-8 BOM
    /// file, a CRLF file, a .cshtml view, a binary, a noise directory, one git commit.
    /// </summary>
    public sealed class ReadToolFixture : IDisposable
    {
        public string Root { get; }
        public string S => Path.DirectorySeparatorChar.ToString();

        public ReadToolFixture()
        {
            Root = Path.Combine(Path.GetTempPath(), $"devmind_readgolden_{Guid.NewGuid():N}");
            Directory.CreateDirectory(Root);

            Write("src/App/Program.cs",
                "namespace App\n{\n    public static class Program\n    {\n        public static void Main() { }\n        // needle in App\n    }\n}\n");
            Write("src/Lib/Program.cs",
                "namespace Lib\n{\n    public static class Program\n    {\n        public static int Answer() => 42;\n    }\n}\n");

            var big = new StringBuilder();
            big.Append("namespace Lib\n{\n    public class Big\n    {\n");
            for (int i = 5; i <= 449; i++) big.Append($"        // line {i}\n");
            big.Append("}");                                  // line 450, no trailing newline
            Write("src/Lib/Big.cs", big.ToString());

            File.WriteAllBytes(Path.Combine(Root, "src", "Lib", "Bom.cs"),
                new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes("// bom first line needle\nclass Bom { }\n")).ToArray());
            Write("src/Lib/Crlf.cs", "class Crlf\r\n{\r\n    // needle crlf\r\n}\r\n");
            Write("Views/Home/Index.cshtml", "@model App.HomeModel\n<h1>@Model.Title</h1>\n<p>needle view</p>\n");
            File.WriteAllBytes(Path.Combine(Root, "assets", "blob.bin").Also(p => Directory.CreateDirectory(Path.GetDirectoryName(p)!)),
                new byte[] { 0, 1, 2, 0xFF }.Concat(Encoding.ASCII.GetBytes("needle\n")).Concat(new byte[] { 0, 0 }).ToArray());
            Write("bin/Debug/Noise.cs", "// needle in noise\n");
            Write("readme.txt", "readme needle\n");
            // Breadth-first enumeration reaches tools/ before src/App/ — only a real sort puts
            // it last, so list_files' ordering is actually exercised.
            Write("tools/Zz.cs", "// zz\n");

            Git("init", "-q");
            Git("config", "core.autocrlf", "false"); // no CRLF warnings on stderr
            Git("-c", "user.email=t@example.com", "-c", "user.name=t", "add", "-A");
            Git("-c", "user.email=t@example.com", "-c", "user.name=t", "-c", "core.autocrlf=false",
                "commit", "-q", "-m", "init fixture");
        }

        public string P(string relative) => Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));

        public void Write(string relative, string content)
        {
            string path = P(relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, Encoding.UTF8.GetBytes(content));
        }

        public string Git(params string[] args)
        {
            var psi = new ProcessStartInfo("git") { WorkingDirectory = Root, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            string output = p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            p.WaitForExit();
            return output;
        }

        public void Dispose()
        {
            try
            {
                foreach (var f in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
                    File.SetAttributes(f, FileAttributes.Normal); // git objects are read-only
                Directory.Delete(Root, recursive: true);
            }
            catch { /* best effort */ }
        }
    }

    internal static class FixtureExtensions
    {
        public static string Also(this string s, Action<string> a) { a(s); return s; }
    }

    /// <summary>The cases. Each takes a fresh fixture and a fresh adapter bound to it.</summary>
    public static class ReadToolGoldenCases
    {
        public static IEnumerable<object[]> Names => Cases.Keys.Select(k => new object[] { k });

        /// <summary>Runs one case against the adapter the factory builds for the fixture root.</summary>
        public static async Task RunAsync(string name, Func<string, IReadToolAdapter> adapterFor)
        {
            using var fx = new ReadToolFixture();
            IReadToolAdapter tool = adapterFor(fx.Root);
            try { await Cases[name](fx, tool); }
            finally { (tool as IDisposable)?.Dispose(); }
        }

        /// <summary>Expands the vocabulary placeholders for the adapter's surface.</summary>
        public static string Expect(IReadToolAdapter t, ReadToolFixture fx, string template) => template
            .Replace("{READ}", t.IsMcp ? "read_file" : "READ")
            .Replace("{GREP}", t.IsMcp ? "grep_file" : "GREP")
            .Replace("{FIND}", t.IsMcp ? "find_in_files" : "FIND")
            .Replace("{DIFF}", t.IsMcp ? "diff_file" : "DIFF")
            .Replace("{ROOT}", fx.Root)
            .Replace("{S}", fx.S)
            .Replace("{NL}", Environment.NewLine);

        private static void Golden(IReadToolAdapter t, ReadToolFixture fx, string template, string actual)
            => Assert.Equal(Expect(t, fx, template), actual);

        private static readonly Dictionary<string, Func<ReadToolFixture, IReadToolAdapter, Task>> Cases =
            new Dictionary<string, Func<ReadToolFixture, IReadToolAdapter, Task>>
        {
            // ── read_file ────────────────────────────────────────────────────
            ["read_full_small"] = async (fx, t) => Golden(t, fx,
                "[READ:Program.cs]\nThe following files have been loaded for context:\n\nProgram.cs\n```\n" +
                "namespace App\n{\n    public static class Program\n    {\n        public static void Main() { }\n        // needle in App\n    }\n}\n" +
                "\n```\n\n",
                await t.Read(fx.P("src/App/Program.cs"))),

            ["read_relative_path_resolves_like_absolute"] = async (fx, t) =>
                Assert.Equal(await t.Read(fx.P("src/Lib/Crlf.cs"), 1, 2), await t.Read("src/Lib/Crlf.cs", 1, 2)),

            ["read_big_file_is_outline"] = async (fx, t) =>
            {
                string r = await t.Read(fx.P("src/Lib/Big.cs"));
                Assert.StartsWith("[READ:Big.cs] (450 lines — outline only; call read_file again with force_full:true for the whole file, or start_line/end_line for a range)\n", r);
                Assert.Contains("Big", r.Substring(r.IndexOf('\n')));
            },

            ["read_big_file_force_full"] = async (fx, t) =>
            {
                string r = await t.Read(fx.P("src/Lib/Big.cs"), forceFull: true);
                Assert.StartsWith("[READ:Big.cs]\nThe following files have been loaded for context:\n\nBig.cs\n```\nnamespace Lib\n", r);
                Assert.EndsWith("        // line 449\n}\n```\n\n", r);
            },

            ["read_range"] = async (fx, t) => Golden(t, fx,
                "[READ:Big.cs:10-12] (lines 10-12 of 450 total)\n```\n10:         // line 10{NL}11:         // line 11{NL}12:         // line 12\n```\n\n",
                await t.Read(fx.P("src/Lib/Big.cs"), 10, 12)),

            ["read_range_reversed_is_swapped"] = async (fx, t) =>
                Assert.Equal(await t.Read(fx.P("src/Lib/Big.cs"), 10, 12), await t.Read(fx.P("src/Lib/Big.cs"), 12, 10)),

            // DRIFT fix: the agent surface read lines 1..start when end_line was absent.
            ["read_start_line_only_reads_to_end_of_file"] = async (fx, t) => Golden(t, fx,
                "[READ:Big.cs:448-450] (lines 448-450 of 450 total)\n```\n448:         // line 448{NL}449:         // line 449{NL}450: }\n```\n\n",
                await t.Read(fx.P("src/Lib/Big.cs"), 448)),

            ["read_range_clamped_to_file_end"] = async (fx, t) => Golden(t, fx,
                "[READ:Big.cs:449-450] (lines 449-450 of 450 total — clamped)\n```\n449:         // line 449{NL}450: }\n```\n\n",
                await t.Read(fx.P("src/Lib/Big.cs"), 449, 500)),

            ["read_range_out_of_bounds"] = async (fx, t) => Golden(t, fx,
                "[{READ}] Range 600-610 out of bounds for Big.cs (450 lines)",
                await t.Read(fx.P("src/Lib/Big.cs"), 600, 610)),

            ["read_bom_file_has_no_bom_char"] = async (fx, t) => Golden(t, fx,
                "[READ:Bom.cs:1-1] (lines 1-1 of 3 total)\n```\n1: // bom first line needle\n```\n\n",
                await t.Read(fx.P("src/Lib/Bom.cs"), 1, 1)),

            ["read_crlf_range_strips_cr"] = async (fx, t) => Golden(t, fx,
                "[READ:Crlf.cs:1-3] (lines 1-3 of 5 total)\n```\n1: class Crlf{NL}2: {{NL}3:     // needle crlf\n```\n\n",
                await t.Read(fx.P("src/Lib/Crlf.cs"), 1, 3)),

            ["read_crlf_full_keeps_file_bytes"] = async (fx, t) => Golden(t, fx,
                "[READ:Crlf.cs]\nThe following files have been loaded for context:\n\nCrlf.cs\n```\nclass Crlf\r\n{\r\n    // needle crlf\r\n}\r\n\n```\n\n",
                await t.Read(fx.P("src/Lib/Crlf.cs"))),

            ["read_cshtml"] = async (fx, t) => Golden(t, fx,
                "[READ:Index.cshtml]\nThe following files have been loaded for context:\n\nIndex.cshtml\n```\n@model App.HomeModel\n<h1>@Model.Title</h1>\n<p>needle view</p>\n\n```\n\n",
                await t.Read(fx.P("Views/Home/Index.cshtml"))),

            ["read_not_found_names_the_tool"] = async (fx, t) => Golden(t, fx,
                "{READ}: file not found — {ROOT}{S}src{S}Nope.cs{NL}" +
                "Searched recursively (excluding bin/obj/.git/node_modules etc.): {ROOT}{NL}" +
                "No file with that name exists in the searched directories — it is absent from this project.",
                await t.Read(fx.P("src/Nope.cs"))),

            ["grep_not_found_names_the_tool"] = async (fx, t) =>
                Assert.StartsWith(Expect(t, fx, "{GREP}: file not found — {ROOT}{S}src{S}Nope.cs{NL}"),
                    await t.Grep("x", fx.P("src/Nope.cs"))),

            ["read_reread_same_file_is_outline"] = async (fx, t) =>
            {
                await t.Read(fx.P("src/App/Program.cs"));
                Assert.StartsWith("[READ:Program.cs] (9 lines — outline only;", await t.Read(fx.P("src/App/Program.cs")));
            },

            // DRIFT fix: the agent hosts keyed "already read" by bare name, so reading one
            // Program.cs made a DIFFERENT Program.cs come back as an outline.
            ["read_same_named_other_file_is_not_outline"] = async (fx, t) =>
            {
                await t.Read(fx.P("src/App/Program.cs"));
                Assert.StartsWith("[READ:Program.cs]\nThe following files have been loaded for context:\n\nProgram.cs\n```\nnamespace Lib\n",
                    await t.Read(fx.P("src/Lib/Program.cs")));
            },

            // ── git branch ───────────────────────────────────────────────────
            ["git_log"] = async (fx, t) =>
            {
                string r = await t.Read("git log");
                string hash = fx.Git("rev-parse", "--short", "HEAD").Trim();
                Golden(t, fx, "[{READ}] git log (last 10 commits)\n```\n" + hash + " init fixture\n```\n\n", r);
            },

            ["git_log_count_from_start_line"] = async (fx, t) =>
                Assert.StartsWith(Expect(t, fx, "[{READ}] git log (last 3 commits)\n```\n"), await t.Read("git log", 3)),

            ["git_diff_working_changes"] = async (fx, t) =>
            {
                fx.Write("readme.txt", "readme changed\n");
                string r = await t.Read("git diff");
                Assert.StartsWith(Expect(t, fx, "[{READ}] git diff (working changes)\n```\ndiff --git a/readme.txt b/readme.txt{NL}"), r);
                Assert.Contains("+readme changed", r);
            },

            // DRIFT fix (security): the agent surface ran 'git diff <args>' through the shell,
            // so the args could chain a second command. Now argv: the args reach git verbatim.
            ["git_diff_args_are_not_shell_interpreted"] = async (fx, t) =>
            {
                string r = await t.Read("git diff --stat && echo pwned > injected.txt");
                Assert.False(File.Exists(fx.P("injected.txt")), "a shell ran the chained command");
                Assert.StartsWith(Expect(t, fx, "[{READ}] git diff --stat && echo pwned > injected.txt\n(error — exit code"), r);
            },

            // DRIFT fix: the agent surface only recognised a .git DIRECTORY; worktrees and
            // submodules have a .git FILE.
            ["git_root_found_through_a_git_file"] = async (fx, t) =>
            {
                string gitDir = fx.P(".git");
                string moved = fx.P(".git-real");
                Directory.Move(gitDir, moved);
                File.WriteAllText(gitDir, "gitdir: " + moved.Replace('\\', '/') + "\n");
                Assert.StartsWith(Expect(t, fx, "[{READ}] git log (last 10 commits)\n```\n"), await t.Read("git log"));
            },

            // ── grep_file ────────────────────────────────────────────────────
            ["grep_matches"] = async (fx, t) => Golden(t, fx,
                "{GREP} results for \"needle|Main\" in {ROOT}{S}src{S}App{S}Program.cs (2 matches):{NL}  5:         public static void Main() { }{NL}  6:         // needle in App",
                await t.Grep("needle|Main", fx.P("src/App/Program.cs"))),

            ["grep_line_window"] = async (fx, t) => Golden(t, fx,
                "{GREP} results for \"line 1\" in {ROOT}{S}src{S}Lib{S}Big.cs (2 matches):{NL}  10:         // line 10{NL}  11:         // line 11",
                await t.Grep("line 1", fx.P("src/Lib/Big.cs"), 10, 11)),

            ["grep_cap_50"] = async (fx, t) =>
                Assert.StartsWith(Expect(t, fx, "{GREP} results for \"// line\" in {ROOT}{S}src{S}Lib{S}Big.cs (50 of 445 matches — narrow your pattern or use a line range):{NL}   5:         // line 5{NL}"),
                    await t.Grep("// line", fx.P("src/Lib/Big.cs"))),

            ["grep_bom_and_crlf"] = async (fx, t) =>
            {
                Golden(t, fx, "{GREP} results for \"needle\" in {ROOT}{S}src{S}Lib{S}Bom.cs (1 match):{NL}  1: // bom first line needle",
                    await t.Grep("needle", fx.P("src/Lib/Bom.cs")));
                Golden(t, fx, "{GREP} results for \"needle\" in {ROOT}{S}src{S}Lib{S}Crlf.cs (1 match):{NL}  3:     // needle crlf",
                    await t.Grep("needle", fx.P("src/Lib/Crlf.cs")));
            },

            ["grep_miss"] = async (fx, t) => Golden(t, fx,
                "{GREP}: no matches for \"zzz\" in {ROOT}{S}src{S}App{S}Program.cs",
                await t.Grep("zzz", fx.P("src/App/Program.cs"))),

            ["grep_sees_out_of_band_edit"] = async (fx, t) =>
            {
                await t.Grep("needle", fx.P("src/App/Program.cs"));
                fx.Write("src/App/Program.cs", "fresh needle\n");
                File.SetLastWriteTimeUtc(fx.P("src/App/Program.cs"), DateTime.UtcNow.AddMinutes(1));
                Assert.EndsWith("  1: fresh needle", await t.Grep("needle", fx.P("src/App/Program.cs")));
            },

            // ── find_in_files ────────────────────────────────────────────────
            ["find_cs_skips_noise_dirs"] = async (fx, t) => Golden(t, fx,
                "{FIND} results for \"needle\" in *.cs (3 matches):{NL}  Program.cs:6:         // needle in App{NL}  Bom.cs:1: // bom first line needle{NL}  Crlf.cs:3:     // needle crlf",
                await t.Find("needle", "*.cs")),

            // DRIFT fix: the MCP tool opened binaries (and cloud placeholders) for text search.
            ["find_skips_binary_files"] = async (fx, t) =>
            {
                string r = await t.Find("needle", "*");
                Assert.DoesNotContain("blob.bin", r);
                Assert.Contains("readme.txt:1: readme needle", r);
                Assert.Contains("Index.cshtml:3: <p>needle view</p>", r);
            },

            ["find_cshtml"] = async (fx, t) => Golden(t, fx,
                "{FIND} results for \"@model\" in *.cshtml (2 matches):{NL}  Index.cshtml:1: @model App.HomeModel{NL}  Index.cshtml:2: <h1>@Model.Title</h1>",
                await t.Find("@model", "*.cshtml")),

            ["find_dir_prefixed_glob"] = async (fx, t) => Golden(t, fx,
                "{FIND} results for \"class\" in src/Lib/*.cs (4 matches):{NL}  Big.cs:3:     public class Big{NL}  Bom.cs:2: class Bom { }{NL}  Crlf.cs:1: class Crlf{NL}  Program.cs:3:     public static class Program",
                await t.Find("class", "src/Lib/*.cs")),

            ["find_miss"] = async (fx, t) =>
                Assert.StartsWith(Expect(t, fx, "{FIND}: no matches for \"zzz\" in *.cs"), await t.Find("zzz", "*.cs")),

            // ── list_files ───────────────────────────────────────────────────
            ["list_recursive_sorted_absolute"] = async (fx, t) => Golden(t, fx,
                "{ROOT}{S}src{S}App{S}Program.cs{NL}{ROOT}{S}src{S}Lib{S}Big.cs{NL}{ROOT}{S}src{S}Lib{S}Bom.cs{NL}{ROOT}{S}src{S}Lib{S}Crlf.cs{NL}{ROOT}{S}src{S}Lib{S}Program.cs{NL}{ROOT}{S}tools{S}Zz.cs",
                await t.List("*.cs")),

            ["list_top_level_only"] = async (fx, t) => Golden(t, fx, "{ROOT}{S}readme.txt", await t.List("*.txt", recursive: false)),

            ["list_no_match"] = async (fx, t) => Golden(t, fx, "[no matches]", await t.List("*.nope")),

            ["list_empty_pattern"] = async (fx, t) => Golden(t, fx, "[ERROR: glob pattern is empty]", await t.List("src/")),

            // ── diff_file ────────────────────────────────────────────────────
            ["diff_untouched_file"] = async (fx, t) => Golden(t, fx,
                "{DIFF}: No changes — {ROOT}{S}readme.txt has not been modified this session.",
                await t.Diff(fx.P("readme.txt"))),

            ["diff_no_changes_after_read"] = async (fx, t) =>
            {
                await t.Read(fx.P("readme.txt"));
                Golden(t, fx, "{DIFF}: No changes detected in {ROOT}{S}readme.txt.", await t.Diff(fx.P("readme.txt")));
            },

            // DRIFT fix (MCP): read_file did not capture a baseline, so this said "no changes tracked".
            ["diff_after_read_and_edit"] = async (fx, t) =>
            {
                await t.Read(fx.P("readme.txt"));
                fx.Write("readme.txt", "readme needle\nsecond line\n");
                string r = await t.Diff(fx.P("readme.txt"));
                Assert.Contains("+second line", r);
                Assert.Contains(fx.P("readme.txt"), r);
            },

            // DRIFT fix (agent): a deleted file errored instead of diffing against empty.
            ["diff_deleted_file"] = async (fx, t) =>
            {
                await t.Read(fx.P("readme.txt"));
                File.Delete(fx.P("readme.txt"));
                Assert.Contains("-readme needle", await t.Diff(fx.P("readme.txt")));
            },
        };
    }
}
