// File: ShellRunner.cs  v1.3
// Copyright (c) iOnline Consulting LLC. All rights reserved.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

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
        /// Execute a shell command and return buffered output and exit code.
        /// Cancellation kills the process immediately; timeout (default 120s) also kills.
        /// <para>
        /// TODO: ArgumentList-based command building once target framework supports it
        /// (.NET Core 2.1+ / netstandard2.1+). Current string-concat approach has a
        /// quoting bug for PowerShell strings containing double quotes (Stage 7 Item 3).
        /// </para>
        /// </summary>
        public async Task<(string output, int exitCode)> ExecuteAsync(
            string command,
            CancellationToken cancellationToken = default,
            int timeoutSeconds = 120,
            IProgress<ShellOutputLine> onLine = null)
        {
            bool usePowerShell = IsPowerShellAvailable();
            string shell = usePowerShell ? "powershell.exe" : "cmd.exe";

            if (usePowerShell)
                command = command.Replace(" && ", "; ");

            string sanitized = SanitizeCommand(command);
            string args = usePowerShell
                ? $"-NoProfile -NonInteractive -Command \"{sanitized.Replace("\"", "\\\"")}\""
                : $"/c \"{sanitized}\"";

            try
            {
                var psi = new ProcessStartInfo(shell, args)
                {
                    WorkingDirectory = WorkingDirectory,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                };

                var outputBuffer = new StringBuilder();
                var exitTcs = new TaskCompletionSource<bool>();

                using var proc = new Process();
                proc.StartInfo = psi;
                proc.EnableRaisingEvents = true;
                proc.Exited             += (s, e) => exitTcs.TrySetResult(true);
                proc.OutputDataReceived += (s, e) =>
                {
                    if (e.Data != null) { outputBuffer.AppendLine(e.Data); onLine?.Report(new ShellOutputLine(e.Data, isError: false)); }
                };
                proc.ErrorDataReceived += (s, e) =>
                {
                    if (e.Data != null) { outputBuffer.AppendLine(e.Data); onLine?.Report(new ShellOutputLine(e.Data, isError: true)); }
                };

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
    }
}
