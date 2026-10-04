// File: GitWriteGuard.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Does a delegated shell command write git object content onto a working-tree file?
//
// job-11 ran "git show HEAD~1:file | Set-Content file" and destroyed two earlier tasks'
// uncommitted changes, so the headless shell guard blocked "git show" next to any ">",
// "set-content" or "out-file" anywhere in the command text. That was a substring test,
// and it fought the briefs that told agents to compare against HEAD (H-59): job-2116 was
// blocked for a Select-String regex containing "p>", job-2115 for the quoted string '=>'
// in a Where-Object filter. Neither wrote a file.
//
// This reads the command the way a shell would, roughly: statements (; newline && ||),
// pipeline stages (|), quoted strings left intact. A git-content source is `git show` or
// `git cat-file`, or a variable assigned from one. It is blocked only when its output
// reaches a file write — a stdout redirect (> >> 1> *>), Out-File / Set-Content /
// Add-Content / Tee-Object (and aliases), [IO.File]::Write*, or git's own --output — whose
// target resolves inside the working directory (following any cd in the command). A target
// that cannot be resolved (a variable) counts as inside. Printing, piping to a reader and
// assigning to a variable are allowed. Restore-type commands (checkout/restore/reset/clean)
// are classified here too; the caller keeps its older substring checks as a backstop.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace DevMind
{
    internal static class GitWriteGuard
    {
        public const string WriteReason = "writes git object content over working-tree files";
        public const string RestoreReason = "restores/discards working-tree files from git";
        public const string MutationReason =
            "read-only git only in delegated jobs (status, diff, log, show, blame, ls-files...); Paul owns commits";

        // H-48: subcommands that change the repo, index, refs or working tree in every form.
        // add/commit are allowed when the job has allow_commit; tag/branch/config/remote/stash/
        // worktree are decided per form (their list/read forms are allowed) in GitCall.IsMutation.
        private static readonly HashSet<string> MutatingSubcommands = new HashSet<string>(StringComparer.Ordinal)
        {
            "checkout", "switch", "reset", "clean", "restore", "rm", "mv", "rebase", "merge", "cherry-pick",
            "revert", "am", "apply", "pull", "push", "fetch", "gc", "prune", "update-ref",
            "submodule", "bisect", "notes", "update-index", "filter-branch", "symbolic-ref", "sparse-checkout",
            "init", "clone", "read-tree", "checkout-index", "replace",
        };

        // Read options of tag/branch/config whose value is the next word, not a ref or key to write.
        private static readonly HashSet<string> ListValueOptions = new HashSet<string>(StringComparer.Ordinal)
        {
            "--contains", "--no-contains", "--merged", "--no-merged", "--points-at", "--sort", "--format",
        };
        private static readonly HashSet<string> ConfigValueOptions = new HashSet<string>(StringComparer.Ordinal)
        {
            "-f", "--file", "--blob", "--type", "--default",
        };

        // Wrappers whose quoted argument is a command line of its own: cmd /c "...", powershell -Command "...".
        private static readonly HashSet<string> CmdShells = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "cmd", "cmd.exe" };
        private static readonly HashSet<string> PsShells = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "powershell", "powershell.exe", "pwsh", "pwsh.exe",
        };

        private static readonly HashSet<string> Writers = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "out-file", "set-content", "sc", "add-content", "ac", "tee-object", "tee",
        };

        private static readonly HashSet<string> PathParams = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "-filepath", "-path", "-literalpath", "-pspath", "-lp", "-file",
        };

        // Writer parameters that take a value which is not the path.
        private static readonly HashSet<string> ValueParams = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "-encoding", "-value", "-width", "-delimiter", "-stream", "-filter", "-include",
            "-exclude", "-credential", "-inputobject", "-erroraction", "-ea",
        };

        private static readonly HashSet<string> CdCommands = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "cd", "chdir", "set-location", "sl", "pushd",
        };

        private static readonly Regex Assignment = new Regex(
            @"^\s*\$(?<name>[A-Za-z_][\w]*)\s*[+]?=(?!=)", RegexOptions.Compiled);

        private static readonly Regex FileWriteCall = new Regex(
            @"\[(?:system\.)?io\.file\]::(?:writealltext|writealllines|writeallbytes|appendalltext|appendalllines)\s*\(",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>
        /// The block reason for <paramref name="command"/>, or null when it does not write git
        /// object content onto, or restore, working-tree files, and runs no mutating git
        /// subcommand (H-48; add/commit pass when <paramref name="allowCommit"/>).
        /// </summary>
        public static string Classify(string command, string workingDirectory, bool allowCommit = false)
        {
            string dir = SafeFull(workingDirectory) ?? SafeFull(Directory.GetCurrentDirectory());
            return Classify(command, dir, dir, allowCommit);
        }

        private static string Classify(string command, string baseDir, string root, bool allowCommit)
        {
            if (string.IsNullOrWhiteSpace(command)) return null;
            var tainted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (string statement in SplitTopLevel(command, statements: true))
            {
                string body = statement;
                string assignedVar = null;
                Match asg = Assignment.Match(body);
                if (asg.Success)
                {
                    assignedVar = asg.Groups["name"].Value;
                    body = body.Substring(asg.Length);
                }

                List<string> stages = SplitTopLevel(body, statements: false);
                int sourceAt = -1;
                for (int i = 0; i < stages.Count; i++)
                {
                    List<string> words = Words(stages[i]);
                    if (words.Count == 0) continue;

                    if (stages.Count == 1 && assignedVar == null && CdCommands.Contains(words[0]))
                    {
                        string target = words.Skip(1).FirstOrDefault(w => !w.StartsWith("-") && !w.StartsWith("/"));
                        if (target != null) baseDir = Resolve(baseDir, target) ?? baseDir;
                        continue;
                    }

                    string inner = WrappedCommand(words);
                    if (inner != null)
                    {
                        string nested = Classify(inner, baseDir, root, allowCommit);
                        if (nested != null) return nested;
                    }

                    GitCall git = null;
                    foreach (int at in CommandStarts(words))
                        if ((git = ParseGit(words, at)) != null) break;
                    if (git != null && git.IsRestore) return RestoreReason;
                    if (git != null && git.IsMutation(allowCommit)) return MutationReason;

                    bool isSource = (git != null && git.ProducesObjectContent) || ReferencesAny(stages[i], tainted);
                    if (isSource && sourceAt < 0) sourceAt = i;
                    if (sourceAt < 0) continue;

                    // From the source on, every way this stage can put the stream in a file.
                    foreach (string target in WriteTargets(stages[i], words, git))
                        if (IsInside(target, baseDir, root)) return WriteReason;
                }

                if (assignedVar != null)
                {
                    if (sourceAt >= 0) tainted.Add(assignedVar);
                    else tainted.Remove(assignedVar);
                }
            }
            return null;
        }

        // ── Git ────────────────────────────────────────────────────────────────

        private sealed class GitCall
        {
            public string Sub;
            public List<string> Args = new List<string>();
            public bool ProducesObjectContent => Sub is "show" or "cat-file";

            public bool IsRestore
            {
                get
                {
                    if (Sub == "restore") return true;
                    if (Sub == "reset") return Args.Any(a => a.Equals("--hard", StringComparison.OrdinalIgnoreCase));
                    if (Sub == "clean") return true;
                    if (Sub != "checkout") return false;
                    // "git checkout <rev> -- <path>", "git checkout -- <path>", "git checkout .",
                    // "git checkout HEAD <path>"; not "git checkout main" / "git checkout -b new".
                    if (Args.Contains("--") || Args.Contains(".")) return true;
                    var positional = new List<string>();
                    for (int i = 0; i < Args.Count; i++)
                    {
                        string a = Args[i];
                        if (a is "-b" or "-B" or "--orphan") { i++; continue; }
                        if (a.StartsWith("-")) continue;
                        positional.Add(a);
                    }
                    return positional.Count >= 2
                        || (positional.Count == 1 && positional[0].StartsWith("head", StringComparison.OrdinalIgnoreCase));
                }
            }

            /// <summary>H-48: does this call change the repo, index, refs or working tree?</summary>
            public bool IsMutation(bool allowCommit)
            {
                if (Sub is "add" or "commit") return !allowCommit;
                if (MutatingSubcommands.Contains(Sub)) return true;
                switch (Sub)
                {
                    case "stash":    // "git stash" alone is "git stash push"
                        return !(Args.Count > 0 && Args[0] is "list" or "show");
                    case "worktree":
                        return !(Args.Count > 0 && Args[0] == "list");
                    case "remote":   // "git remote", "-v", "show", "get-url" read
                        return Positionals().FirstOrDefault() is string r && r is not ("show" or "get-url");
                    case "tag":
                        return HasAny("-d", "--delete", "-a", "--annotate", "-s", "--sign", "-u", "-f", "--force", "-m", "-F", "-e", "--edit")
                            || (Positionals().Any() && !HasAny("-l", "--list", "-v", "--verify"));
                    case "branch":
                        return HasAny("-d", "-D", "--delete", "-m", "-M", "--move", "-c", "-C", "--copy", "-f", "--force",
                                      "-u", "--set-upstream-to", "--unset-upstream", "--edit-description", "--track", "-t")
                            || Args.Any(a => a.StartsWith("--set-upstream-to=", StringComparison.Ordinal))
                            || (Positionals().Any() && !HasAny("-l", "--list"));
                    case "config":   // --get* / -l / --list and the "get"/"list" verbs read; one key alone reads it
                        if (Args.Any(a => a.StartsWith("--get", StringComparison.Ordinal)) || HasAny("-l", "--list")) return false;
                        if (HasAny("--unset", "--unset-all", "--add", "--replace-all", "--rename-section", "--remove-section", "-e", "--edit"))
                            return true;
                        List<string> pos = Positionals().ToList();
                        if (pos.Count > 0 && pos[0] is "get" or "list") return false;
                        return pos.Count >= 2 || (pos.Count > 0 && pos[0] is "set" or "unset" or "rename-section" or "remove-section" or "edit");
                    default:
                        return false;
                }
            }

            private bool HasAny(params string[] flags) => Args.Any(a => flags.Contains(a, StringComparer.Ordinal));

            // Non-option arguments; an option that takes a separate value skips it.
            private IEnumerable<string> Positionals()
            {
                for (int i = 0; i < Args.Count; i++)
                {
                    string a = Args[i];
                    if (ListValueOptions.Contains(a) || (Sub == "config" && ConfigValueOptions.Contains(a))) { i++; continue; }
                    if (a.StartsWith("-", StringComparison.Ordinal)) continue;
                    yield return a;
                }
            }
        }

        /// <summary>The command line inside a <c>cmd /c "..."</c> or <c>powershell -Command "..."</c>
        /// stage (quotes already removed by <see cref="Words"/>), or null.</summary>
        private static string WrappedCommand(List<string> words)
        {
            if (words.Count < 3) return null;
            bool cmd = CmdShells.Contains(words[0]);
            bool ps = PsShells.Contains(words[0]);
            if (!cmd && !ps) return null;
            for (int i = 1; i < words.Count - 1; i++)
            {
                string w = words[i].ToLowerInvariant();
                if (cmd ? w is "/c" or "/k" : w is "-c" or "-command" or "-com" or "-comm" or "-comma" or "-comman")
                    return string.Join(" ", words.Skip(i + 1));
            }
            return null;
        }

        /// <summary>Indexes of words in command position: the first word, and any word right after
        /// a "{", "(" or "&amp;" (a script block or call inside the stage).</summary>
        private static IEnumerable<int> CommandStarts(List<string> words)
        {
            for (int i = 0; i < words.Count; i++)
                if (i == 0 || words[i - 1] is "{" or "(" or "&" || words[i - 1].EndsWith("{")) yield return i;
        }

        private static GitCall ParseGit(List<string> words, int start)
        {
            // "git", "git.exe", or a (quoted) path to one: C:\Program Files\Git\cmd\git.exe
            string exe = words[start];
            string name = exe.Substring(exe.LastIndexOfAny(new[] { '\\', '/' }) + 1);
            if (!(name.Equals("git", StringComparison.OrdinalIgnoreCase) || name.Equals("git.exe", StringComparison.OrdinalIgnoreCase)))
                return null;
            int i = start + 1;
            // Global options before the subcommand: -C <dir>, -c <k=v>, --no-pager, --git-dir=...
            // (or --git-dir / --work-tree / --namespace with the value as the next word).
            while (i < words.Count && words[i].StartsWith("-"))
            {
                if (words[i] is "-C" or "-c" or "--git-dir" or "--work-tree" or "--namespace") i++;
                i++;
            }
            if (i >= words.Count) return null;
            var call = new GitCall { Sub = words[i].ToLowerInvariant() };
            call.Args.AddRange(words.Skip(i + 1));
            return call;
        }

        // ── Write targets ──────────────────────────────────────────────────────

        private static IEnumerable<string> WriteTargets(string stage, List<string> words, GitCall git)
        {
            foreach (string t in Redirects(stage)) yield return t;

            if (git != null)
            {
                for (int i = 0; i < git.Args.Count; i++)
                {
                    string a = git.Args[i];
                    if (a.StartsWith("--output=", StringComparison.OrdinalIgnoreCase)) yield return a.Substring(9);
                    else if (a.Equals("--output", StringComparison.OrdinalIgnoreCase) && i + 1 < git.Args.Count) yield return git.Args[i + 1];
                }
            }

            foreach (int at in CommandStarts(words))
            {
                if (!Writers.Contains(words[at])) continue;
                string target = WriterPath(words, at);
                if (target != null) yield return target;
            }

            Match call = FileWriteCall.Match(stage);
            if (call.Success)
            {
                // First argument of the call: a quoted literal, or anything else (unknown → "$").
                string rest = stage.Substring(call.Index + call.Length).TrimStart();
                Match lit = Regex.Match(rest, "^(?:'(?<p>[^']*)'|\"(?<p>[^\"]*)\")");
                yield return lit.Success ? lit.Groups["p"].Value : "$";
            }
        }

        private static string WriterPath(List<string> words, int start)
        {
            bool isTee = words[start].Equals("tee-object", StringComparison.OrdinalIgnoreCase) || words[start].Equals("tee", StringComparison.OrdinalIgnoreCase);
            for (int i = start + 1; i < words.Count; i++)
            {
                string w = words[i];
                if (PathParams.Contains(w)) return i + 1 < words.Count ? words[i + 1] : "$";
                int colon = w.IndexOf(':');
                if (w.StartsWith("-") && colon > 0 && PathParams.Contains(w.Substring(0, colon))) return w.Substring(colon + 1);
                if (isTee && (w.Equals("-variable", StringComparison.OrdinalIgnoreCase) || w.Equals("-var", StringComparison.OrdinalIgnoreCase)))
                    return null; // Tee-Object -Variable keeps it in memory
                if (ValueParams.Contains(w)) { i++; continue; }
                if (w.StartsWith("-") && w.Length > 1) continue;
                if (w is "}" or ")") return null;
                return w;
            }
            return null;
        }

        /// <summary>Targets of stdout redirects outside quotes: "&gt;", "&gt;&gt;", "1&gt;", "*&gt;".
        /// "2&gt;", "2&gt;&amp;1", "&gt;$null", "&gt;NUL" and "&gt;/dev/null" are not file writes of the stream.</summary>
        private static IEnumerable<string> Redirects(string stage)
        {
            char quote = '\0';
            for (int i = 0; i < stage.Length; i++)
            {
                char ch = stage[i];
                if (quote != '\0')
                {
                    if (ch == '`' && quote == '"') { i++; continue; }
                    if (ch == quote) quote = '\0';
                    continue;
                }
                if (ch == '\'' || ch == '"') { quote = ch; continue; }
                if (ch == '`') { i++; continue; }
                if (ch != '>') continue;

                char before = i > 0 ? stage[i - 1] : ' ';
                if (char.IsDigit(before) && before != '1') { if (i + 1 < stage.Length && stage[i + 1] == '>') i++; continue; }
                int j = i + 1;
                if (j < stage.Length && stage[j] == '>') j++;
                if (j < stage.Length && stage[j] == '&') { i = j; continue; } // >&1, >&2
                while (j < stage.Length && char.IsWhiteSpace(stage[j])) j++;
                string target = ReadWord(stage, ref j);
                i = j - 1;
                if (target.Length == 0) continue;
                if (target.Equals("$null", StringComparison.OrdinalIgnoreCase)
                    || target.Equals("nul", StringComparison.OrdinalIgnoreCase)
                    || target == "/dev/null") continue;
                yield return target;
            }
        }

        // ── Paths ──────────────────────────────────────────────────────────────

        private static bool IsInside(string target, string baseDir, string root)
        {
            string full = Resolve(baseDir, target);
            if (full == null) return true; // unresolvable (a variable): assume the worst
            if (root == null) return true;
            string r = root.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            return (full.TrimEnd('\\', '/') + Path.DirectorySeparatorChar).StartsWith(r, StringComparison.OrdinalIgnoreCase);
        }

        private static string Resolve(string baseDir, string path)
        {
            string p = path.Trim().TrimEnd('}', ')', ';');
            p = Regex.Replace(p, @"\$env:(\w+)", m => Environment.GetEnvironmentVariable(m.Groups[1].Value) ?? "$", RegexOptions.IgnoreCase);
            p = Regex.Replace(p, @"%(\w+)%", m => Environment.GetEnvironmentVariable(m.Groups[1].Value) ?? "$");
            if (p.StartsWith("~")) p = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + p.Substring(1);
            if (p.Length == 0 || p.Contains('$')) return null;
            try
            {
                return SafeFull(Path.IsPathRooted(p) || baseDir == null ? p : Path.Combine(baseDir, p));
            }
            catch { return null; }
        }

        private static string SafeFull(string p)
        {
            if (string.IsNullOrWhiteSpace(p)) return null;
            try { return Path.GetFullPath(p); } catch { return null; }
        }

        private static bool ReferencesAny(string stage, HashSet<string> vars)
        {
            foreach (string v in vars)
                if (Regex.IsMatch(stage, @"\$" + Regex.Escape(v) + @"(?![\w])", RegexOptions.IgnoreCase)) return true;
            return false;
        }

        // ── Lexing ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Splits outside quotes: statements on ; newline &amp;&amp; ||, or pipeline stages on a
        /// single |. Braces are not tracked — a writer inside a script block is still a stage.
        /// </summary>
        private static List<string> SplitTopLevel(string text, bool statements)
        {
            var parts = new List<string>();
            var sb = new StringBuilder();
            char quote = '\0';
            for (int i = 0; i < text.Length; i++)
            {
                char ch = text[i];
                if (quote != '\0')
                {
                    sb.Append(ch);
                    if (ch == '`' && quote == '"' && i + 1 < text.Length) { sb.Append(text[++i]); continue; }
                    if (ch == quote) quote = '\0';
                    continue;
                }
                if (ch == '\'' || ch == '"') { quote = ch; sb.Append(ch); continue; }
                if (ch == '`' && i + 1 < text.Length && text[i + 1] != '\n' && text[i + 1] != '\r') { sb.Append(ch).Append(text[++i]); continue; }

                bool dbl = i + 1 < text.Length && text[i + 1] == ch;
                bool split = statements
                    ? ch == ';' || ch == '\n' || ch == '\r' || ((ch == '&' || ch == '|') && dbl)
                    : ch == '|' && !dbl;
                if (!statements && ch == '|' && dbl) { sb.Append("||"); i++; continue; }
                if (split)
                {
                    if (statements && dbl) i++;
                    if (sb.ToString().Trim().Length > 0) parts.Add(sb.ToString());
                    sb.Clear();
                    continue;
                }
                sb.Append(ch);
            }
            if (sb.ToString().Trim().Length > 0) parts.Add(sb.ToString());
            return parts;
        }

        /// <summary>Whitespace-separated words with quotes removed; a leading { ( &amp; or . is dropped
        /// so "{ git show x" and "&amp; git show x" start with the command name.</summary>
        private static List<string> Words(string stage)
        {
            var words = new List<string>();
            int i = 0;
            while (i < stage.Length)
            {
                while (i < stage.Length && char.IsWhiteSpace(stage[i])) i++;
                if (i >= stage.Length) break;
                string w = ReadWord(stage, ref i);
                if (w.Length > 0) words.Add(w);
            }
            while (words.Count > 0 && (words[0] is "{" or "(" or "&" or "." || words[0].EndsWith("{")))
                words.RemoveAt(0);
            if (words.Count > 0) words[0] = words[0].TrimStart('{', '(', '&');
            return words;
        }

        private static string ReadWord(string s, ref int i)
        {
            var sb = new StringBuilder();
            while (i < s.Length && !char.IsWhiteSpace(s[i]))
            {
                char ch = s[i];
                if (ch == '\'' || ch == '"')
                {
                    char q = ch;
                    i++;
                    while (i < s.Length && s[i] != q)
                    {
                        if (s[i] == '`' && q == '"' && i + 1 < s.Length) i++;
                        sb.Append(s[i]);
                        i++;
                    }
                    i++; // closing quote
                    continue;
                }
                if (ch == '>' && sb.Length > 0) break; // "a.cs>b" — stop at a redirect
                sb.Append(ch);
                i++;
            }
            return sb.ToString();
        }
    }
}
