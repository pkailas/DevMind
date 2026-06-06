// File: ShellRunner.cs  v1.7
// Copyright (c) iOnline Consulting LLC. All rights reserved.
// v1.5: trace mcp.shell.spawn / output_line / exit events via DevMind.Trace (alias DmTrace).
// v1.6: fix git stdio hang by setting GIT_REDIRECT_STDIN/STDERR (Git for Windows handle-inheritance issue).
// v1.7: add ExecuteArgvAsync (shell-free, ArgumentList-based) for data-driven callers; extract shared
//       RunProcessAsync plumbing. String-path ExecuteAsync behavior unchanged. Resolves the quoting TODO.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DmTrace = DevMind.Trace;

namespace DevMind
{
    public readonly struct ShellOutputLine
    {
        public string Line    { get; }
        public bool   IsError { get; }
        public ShellOutputLine(string line, bool isError) { Line = line; IsError = isError; }
    }

    /// <summary>
    /// Platform-agnostic shell command executor. Carries working-directory state
    /// so the agentic loop can cd without affecting the host process.
    /// No WPF, VS SDK, or UI dependencies — callable from any host.
    /// </summary>
    public sealed class ShellRunner
    {
        public string WorkingDirectory { get; private set; }

        public ShellRunner(string workingDir = null)
        {
            WorkingDirectory = workingDir ?? Environment.CurrentDirectory;
        }

        /// <summary>
        /// Change working directory. Path may be absolute or relative to the current
        /// WorkingDirectory. Returns false and leaves WorkingDirectory unchanged if the
        /// resolved path does not exist.
        /// </summary>
        public bool ChangeDirectory(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            string resolved = Path.IsPathRooted(path)
                ? path
                : Path.GetFullPath(Path.Combine(WorkingDirectory, path));
            if (!Directory.Exists(resolved)) return false;
            WorkingDirectory = resolved;
            return true;
        }

        /// <summary>
        /// Execute a shell command (arbitrary PowerShell / cmd.exe) and return buffered
        /// output and exit code. This is the general-purpose run-arbitrary-shell path used
        /// by run_shell, run_build, and the CLI agentic host. Cancellation kills the process
        /// tree immediately; timeout (default 120s) also kills.
        /// <para>
        /// Data-driven callers (git_commit, run_tests, clip_read/clip_write, git log/diff)
        /// must NOT build command strings for this method. They use <see cref="ExecuteArgvAsync"/>,
        /// which passes arguments via ProcessStartInfo.ArgumentList with no shell re-parsing —
        /// no quoting, no SanitizeCommand — so a value can never break out into command syntax.
        /// (Resolves the former Stage 7 Item 3 quoting TODO, now that DevMind.Core targets net10.0.)
        /// </para>
        /// </summary>
        public async Task<(string output, int exitCode)> ExecuteAsync(
            string command,
            CancellationToken cancellationToken = default,
            int timeoutSeconds = 120,
            IProgress<ShellOutputLine> onLine = null)
        {
            // npm/npx/yarn/pnpm/bun are .cmd shims on Windows. When PowerShell spawns them it
            // creates a child cmd.exe WITHOUT CreateNoWindow, which causes a visible console window
            // and routes stdio to that window instead of our redirected pipes. Invoking cmd.exe
            // directly keeps CreateNoWindow on the right process and captures output correctly.
            bool forceCmdExe  = IsCmdShimCommand(command);
            bool usePowerShell = !forceCmdExe && IsPowerShellAvailable();
            string shell = usePowerShell ? "powershell.exe" : "cmd.exe";

            // cmd.exe supports && natively; only rewrite for PowerShell.
            if (usePowerShell)
                command = command.Replace(" && ", "; ");

            string sanitized = SanitizeCommand(command);
            string args = usePowerShell
                ? $"-NoProfile -NonInteractive -Command \"{sanitized.Replace("\"", "\\\"")}\""
                : $"/c \"{sanitized}\"";

            var psi = new ProcessStartInfo(shell, args);
            return await RunProcessAsync(
                psi, shell, args, sanitized, usePowerShell, forceCmdExe,
                cancellationToken, timeoutSeconds, onLine);
        }

        /// <summary>
        /// Execute a program with an explicit argument list, bypassing the shell entirely.
        /// Arguments go through ProcessStartInfo.ArgumentList — no PowerShell/cmd parsing,
        /// no quoting, no SanitizeCommand — so values cannot break out into command syntax.
        /// Use this for every data-driven invocation (git, dotnet, and Set-Clipboard with the
        /// clipboard value supplied via <paramref name="extraEnv"/> rather than the command text).
        /// </summary>
        public async Task<(string output, int exitCode)> ExecuteArgvAsync(
            string fileName,
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken = default,
            int timeoutSeconds = 120,
            IProgress<ShellOutputLine> onLine = null,
            IReadOnlyDictionary<string, string> extraEnv = null)
        {
            var psi = new ProcessStartInfo(fileName);
            if (arguments != null)
                foreach (var a in arguments)
                    psi.ArgumentList.Add(a);

            if (extraEnv != null)
                foreach (var kv in extraEnv)
                    psi.EnvironmentVariables[kv.Key] = kv.Value;

            string traceArgs = arguments != null ? string.Join(" ", arguments) : "";
            return await RunProcessAsync(
                psi, fileName, traceArgs, traceArgs, tracePowerShell: false, traceForceCmdExe: false,
                cancellationToken, timeoutSeconds, onLine);
        }

        /// <summary>
        /// Shared process plumbing for both <see cref="ExecuteAsync"/> and
        /// <see cref="ExecuteArgvAsync"/>: applies the common ProcessStartInfo settings and the
        /// GIT_REDIRECT_* fix, spawns the process, pumps stdout/stderr, enforces
        /// timeout/cancellation via taskkill /F /T, traces, and collects buffered output.
        /// </summary>
        private async Task<(string output, int exitCode)> RunProcessAsync(
            ProcessStartInfo psi,
            string traceShell,
            string traceArgs,
            string traceSanitizedInput,
            bool tracePowerShell,
            bool traceForceCmdExe,
            CancellationToken cancellationToken,
            int timeoutSeconds,
            IProgress<ShellOutputLine> onLine)
        {
            try
            {
                psi.WorkingDirectory       = WorkingDirectory;
                psi.UseShellExecute        = false;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError  = true;
                psi.CreateNoWindow         = true;
                psi.WindowStyle            = ProcessWindowStyle.Hidden;

                // Git for Windows hangs when spawned with inherited redirected stdio handles
                // it doesn't know to close. These enable git's own redirection (v2.11.0(2)).
                // Kept in the shared core so BOTH the string and argv paths get the fix.
                psi.EnvironmentVariables["GIT_REDIRECT_STDIN"] = "off";
                psi.EnvironmentVariables["GIT_REDIRECT_STDERR"] = "2>&1";

                long spawnStartTicks = Stopwatch.GetTimestamp();
                long stdoutBytes = 0;
                long stderrBytes = 0;
                long stdoutLines = 0;
                long stderrLines = 0;

                var outputBuffer = new StringBuilder();
                var exitTcs = new TaskCompletionSource<bool>();

                using var proc = new Process();
                proc.StartInfo = psi;
                proc.EnableRaisingEvents = true;
                proc.Exited             += (s, e) => exitTcs.TrySetResult(true);
                proc.OutputDataReceived += (s, e) =>
                {
                    if (e.Data != null)
                    {
                        outputBuffer.AppendLine(e.Data);
                        onLine?.Report(new ShellOutputLine(e.Data, isError: false));
                        stdoutBytes += e.Data.Length;
                        stdoutLines += 1;
                        DmTrace.Event("debug", "mcp.shell.output_line",
                            new Dictionary<string, object>
                            {
                                ["is_error"] = false,
                                ["line"]     = e.Data
                            });
                    }
                };
                proc.ErrorDataReceived += (s, e) =>
                {
                    if (e.Data != null)
                    {
                        outputBuffer.AppendLine(e.Data);
                        onLine?.Report(new ShellOutputLine(e.Data, isError: true));
                        stderrBytes += e.Data.Length;
                        stderrLines += 1;
                        DmTrace.Event("debug", "mcp.shell.output_line",
                            new Dictionary<string, object>
                            {
                                ["is_error"] = true,
                                ["line"]     = e.Data
                            });
                    }
                };

                // Capture the exact spawn parameters BEFORE proc.Start() so the
                // trace records what we asked the OS to launch, not what came back.
                // The env captured here is what ProcessStartInfo will inherit (we
                // don't set psi.Environment, so .NET passes through the McpServer
                // process's current environment).
                DmTrace.Event("info", "mcp.shell.spawn",
                    new Dictionary<string, object>
                    {
                        ["shell"]           = traceShell,
                        ["args"]            = traceArgs,
                        ["working_dir"]     = WorkingDirectory,
                        ["sanitized_input"] = traceSanitizedInput,
                        ["use_powershell"]  = tracePowerShell,
                        ["force_cmd_exe"]   = traceForceCmdExe,
                        ["env"]             = RedactEnv()
                    });

                proc.Start();
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();

                // Kill the process tree when the caller cancels (Stop button or Ctrl+C).
                // proc.Kill() only kills the immediate process; child processes (e.g. ping.exe
                // spawned by powershell.exe) inherit the pipe handle and keep writing until
                // natural completion. taskkill /F /T kills the entire tree atomically.
                // Windows-specific — cross-platform kill mechanism deferred with rest of
                // cross-platform shell support (Phase C.10+).
                cancellationToken.Register(() =>
                {
                    try
                    {
                        Process.Start(new ProcessStartInfo
                        {
                            FileName        = "taskkill",
                            Arguments       = $"/F /T /PID {proc.Id}",
                            CreateNoWindow  = true,
                            UseShellExecute = false
                        })?.WaitForExit(2000);
                    }
                    catch { /* process may have already exited naturally */ }
                });

                var timeoutTask = Task.Delay(timeoutSeconds * 1_000);
                var cancelTask  = Task.Delay(Timeout.Infinite, cancellationToken);

                var winner     = await Task.WhenAny(exitTcs.Task, timeoutTask, cancelTask);
                bool timedOut  = winner == timeoutTask;
                bool cancelled = winner == cancelTask || cancellationToken.IsCancellationRequested;

                if (timedOut || cancelled)
                {
                    try
                    {
                        Process.Start(new ProcessStartInfo
                        {
                            FileName        = "taskkill",
                            Arguments       = $"/F /T /PID {proc.Id}",
                            CreateNoWindow  = true,
                            UseShellExecute = false
                        })?.WaitForExit(2000);
                    }
                    catch { }
                }

                // WaitForExit() ensures all pending OutputDataReceived/ErrorDataReceived events drain.
                await Task.Run(() => proc.WaitForExit(5_000));

                long durationTicks = Stopwatch.GetTimestamp() - spawnStartTicks;
                long durationMs    = durationTicks * 1000L / Stopwatch.Frequency;

                int rawExitCode;
                try { rawExitCode = proc.ExitCode; } catch { rawExitCode = -1; }

                DmTrace.Event("info", "mcp.shell.exit",
                    new Dictionary<string, object>
                    {
                        ["exit_code"]    = rawExitCode,
                        ["duration_ms"]  = durationMs,
                        ["timed_out"]    = timedOut,
                        ["cancelled"]    = cancelled,
                        ["stdout_lines"] = stdoutLines,
                        ["stderr_lines"] = stderrLines,
                        ["stdout_bytes"] = stdoutBytes,
                        ["stderr_bytes"] = stderrBytes
                    });

                int exitCode;
                try { exitCode = (timedOut || cancelled) ? -1 : proc.ExitCode; }
                catch { exitCode = -1; }

                if (timedOut)        onLine?.Report(new ShellOutputLine("[SHELL] Command timed out after 120 seconds.", isError: true));
                else if (cancelled)  onLine?.Report(new ShellOutputLine("[SHELL] Command cancelled.", isError: true));

                var sb = new StringBuilder();
                if (timedOut)        sb.AppendLine("[SHELL] Command timed out after 120 seconds.");
                else if (cancelled)  sb.AppendLine("[SHELL] Command cancelled.");
                string buffered = outputBuffer.ToString().TrimEnd();
                if (!string.IsNullOrEmpty(buffered)) sb.Append(buffered);
                if (sb.Length == 0) { sb.Append("(no output)"); onLine?.Report(new ShellOutputLine("(no output)", isError: false)); }

                return (sb.ToString().TrimEnd(), exitCode);
            }
            catch (OperationCanceledException)
            {
                return ("[SHELL] Command cancelled.", -1);
            }
            catch (Exception ex)
            {
                return ($"(error: {ex.Message})", -1);
            }
        }

        public static bool IsPowerShellAvailable()
        {
            try
            {
                string ps = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.System),
                    "WindowsPowerShell", "v1.0", "powershell.exe");
                return File.Exists(ps);
            }
            catch { return false; }
        }

        private static readonly HashSet<string> _cmdShims = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "npm", "npx", "yarn", "pnpm", "bun"
        };

        /// <summary>
        /// Returns true when the command's first token is a known Windows .cmd shim.
        /// These must be invoked via cmd.exe directly — not via PowerShell — to prevent
        /// PowerShell from spawning an unconcealed child cmd.exe that captures stdio.
        /// </summary>
        private static bool IsCmdShimCommand(string command)
        {
            if (string.IsNullOrEmpty(command)) return false;
            int space = command.IndexOf(' ');
            string first = space < 0 ? command : command.Substring(0, space);
            return _cmdShims.Contains(first);
        }

        /// <summary>
        /// Replaces newlines (and tabs) inside quoted strings with a single space, collapsing
        /// runs of whitespace to one space. Content outside quotes is passed through unchanged.
        /// Prevents PowerShell "missing string terminator" errors from multi-line commit messages.
        /// Handles both single- and double-quoted strings; the closing quote must match the opener.
        /// </summary>
        public static string SanitizeCommand(string command)
        {
            if (string.IsNullOrEmpty(command)) return command;

            var  sb           = new StringBuilder(command.Length);
            char quoteChar    = '\0';
            bool lastWasSpace = false;

            foreach (char c in command)
            {
                if (quoteChar == '\0')
                {
                    if (c == '"' || c == '\'') quoteChar = c;
                    sb.Append(c);
                }
                else if (c == quoteChar)
                {
                    quoteChar    = '\0';
                    lastWasSpace = false;
                    sb.Append(c);
                }
                else if (c == '\r' || c == '\n' || c == '\t')
                {
                    if (!lastWasSpace)
                    {
                        sb.Append(' ');
                        lastWasSpace = true;
                    }
                }
                else
                {
                    sb.Append(c);
                    lastWasSpace = (c == ' ');
                }
            }

            return sb.ToString();
        }

        public static List<string> ParseDirectives(string response)
        {
            var result = new List<string>();
            foreach (var line in response.Split('\n'))
            {
                string trimmed = line.TrimEnd('\r').Trim();
                if (trimmed.StartsWith("SHELL:", StringComparison.OrdinalIgnoreCase))
                {
                    string cmd = trimmed.Substring("SHELL:".Length).Trim();
                    if (!string.IsNullOrEmpty(cmd) && (result.Count == 0 || result[result.Count - 1] != cmd))
                        result.Add(cmd);
                }
            }
            return result;
        }

        private static IDictionary<string, object> RedactEnv()
        {
            var patterns = new[]
            {
                "_KEY", "_TOKEN", "_SECRET", "_PASSWORD",
                "ANTHROPIC_", "OPENAI_", "OPENROUTER_"
            };

            var result = new Dictionary<string, object>();
            var raw = Environment.GetEnvironmentVariables();
            foreach (System.Collections.DictionaryEntry entry in raw)
            {
                string key = entry.Key != null  ? entry.Key.ToString()   : "";
                string val = entry.Value != null ? entry.Value.ToString() : "";

                bool redact = false;
                foreach (var pat in patterns)
                {
                    if (pat.EndsWith("_"))
                    {
                        if (key.StartsWith(pat, StringComparison.OrdinalIgnoreCase))
                        { redact = true; break; }
                    }
                    else
                    {
                        if (key.EndsWith(pat, StringComparison.OrdinalIgnoreCase))
                        { redact = true; break; }
                    }
                }

                result[key] = redact ? "[REDACTED]" : val;
            }
            return result;
        }
    }
}
