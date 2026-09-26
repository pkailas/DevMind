// File: ShellRunner.cs  v1.7
// Copyright (c) iOnline Consulting LLC. All rights reserved.
// v1.5: trace mcp.shell.spawn / output_line / exit events via DevMind.Trace (alias DmTrace).
// v1.6: fix git stdio hang by setting GIT_REDIRECT_STDIN/STDERR (Git for Windows handle-inheritance issue).
// v1.7: add ExecuteArgvAsync (shell-free, ArgumentList-based) for data-driven callers; extract shared
//       RunProcessAsync plumbing. String-path ExecuteAsync behavior unchanged. Resolves the quoting TODO.
// v1.8: redirect + immediately close every spawned child's stdin so it gets an instant EOF instead of
//       inheriting the McpServer's open stdio pipe. Without this any tool that reads stdin (npx, vite,
//       npm) blocks to the 120s timeout. Generalizes the git-specific GIT_REDIRECT_STDIN workaround.
// v1.9: multi-line PowerShell commands go through -EncodedCommand (base64 UTF-16) so every newline
//       and quote reaches PowerShell VERBATIM — no SanitizeCommand, no \" escaping. Here-strings and
//       multi-line scripts previously arrived flattened to one line and died on parse errors
//       (SanitizeCommand collapses newlines inside quoted spans, and a here-string's '@ terminator
//       must sit at column 0 on its own line). Single-line commands keep the battle-tested path.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DmTrace = DevMind.Trace;

// v1.10: MSBUILDDISABLENODEREUSE=1 on every spawned shell so dotnet build/test does
//        not leave a persistent MSBuild worker pool behind; reap (taskkill /F /T) failures
//        are now reported instead of swallowed, and a process still alive after the reap
//        is flagged in the output. Observability + node-reuse prevention only — no Job
//        Objects, no P/Invoke, no retry/escalation.
// v1.11: per-command Windows Job Object (KILL_ON_JOB_CLOSE) around every command —
//        lineage-based containment so orphaned/re-parented grandchildren (the MSBuild
//        escapee) are killed by closing the job handle, which taskkill /F /T cannot reach.
//        Strictly additive and strictly degradable: if ANY part of the job setup fails
//        (non-Windows, API error, nested-job access-denied, process already gone) the
//        command runs exactly as it did in v1.10. See WindowsJobObject.cs.
// v1.12: the PowerShell `&&` -> `;` rewrite is token-aware (TranslateChainOperators): quoted strings,
//        here-strings and comments are no longer touched (job-1697, watchlist H-21).
// v1.13: %VAR% expansion uses the same tokenizer (TokenizeShellSpans) and follows PowerShell
//        interpolation rules: expanded at statement level and inside "...", never inside '...',
//        here-strings or comments (watchlist H-27).

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
        /// tree immediately; timeout defaults to 120s, overridable via <paramref name="timeoutSeconds"/>
        /// or the DEVMIND_SHELL_TIMEOUT environment variable.
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
            int? timeoutSeconds = null,
            IProgress<ShellOutputLine> onLine = null)
        {
            int effectiveTimeout = ResolveTimeout(timeoutSeconds);
            // npm/npx/yarn/pnpm/bun are .cmd shims on Windows. When PowerShell spawns them it
            // creates a child cmd.exe WITHOUT CreateNoWindow, which causes a visible console window
            // and routes stdio to that window instead of our redirected pipes. Invoking cmd.exe
            // directly keeps CreateNoWindow on the right process and captures output correctly.
            bool forceCmdExe  = IsCmdShimCommand(command);
            bool usePowerShell = !forceCmdExe && IsPowerShellAvailable();
            string shell = usePowerShell ? "powershell.exe" : "cmd.exe";

            // cmd.exe supports && natively; only rewrite for PowerShell.
            if (usePowerShell)
            {
                command = TranslateChainOperators(command);

                // %VAR% is cmd syntax — PowerShell passes it through literally, and a
                // live agent's -p:OutDir=%TEMP%\... created directories literally
                // named "%TEMP%" inside the repo. Treated as sugar for $env:VAR, so it
                // follows PowerShell interpolation rules (H-27).
                command = ExpandCmdStyleEnvironmentVariables(command);
            }

            // Multi-line test is computed on the ORIGINAL command, before wrapping,
            // because WrapForPowerShell injects newlines itself. SanitizeCommand is
            // applied to single-line commands only and is deliberately bypassed for
            // multi-line ones (it destroys here-strings).
            bool multiLine = command.IndexOf('\n') >= 0;

            string sanitized;
            string args;
            if (usePowerShell)
            {
                // Wrap FIRST, then encode: WrapForPowerShell injects newlines, so a
                // wrapped command is always multi-line and must travel via
                // -EncodedCommand (the old -Command + quote-escaping branch is
                // gone — it was the source of repeated escaping bugs).
                // The same wrapped text is base64-encoded for execution AND
                // passed as the trace argument, so trace and execution always agree.
                string body = multiLine ? command : SanitizeCommand(command);
                sanitized = WrapForPowerShell(body);
                string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(sanitized));
                args = $"-NoProfile -NonInteractive -EncodedCommand {encoded}";
            }
            else
            {
                sanitized = SanitizeCommand(command);
                args = @"/c " + @"""" + sanitized + @"""";
            }

            var psi = new ProcessStartInfo(shell, args);
           return await RunProcessAsync(
                psi, shell, args, sanitized, usePowerShell, forceCmdExe,
                cancellationToken, effectiveTimeout, onLine);
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
            int? timeoutSeconds = null,
            IProgress<ShellOutputLine> onLine = null,
            IReadOnlyDictionary<string, string> extraEnv = null)
        {
            int effectiveTimeout = ResolveTimeout(timeoutSeconds);

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
                cancellationToken, effectiveTimeout, onLine);
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
            // Per-command containment: a Job Object with KILL_ON_JOB_CLOSE, assigned to
            // the child right after Start(). Job membership is inherited by descendants
            // and is LINEAGE-based — unlike taskkill /F /T it reaches grandchildren that
            // orphaned or re-parented (the MSBuild node-reuse escapee). Strictly
            // degradable: any failure leaves jobHandle == IntPtr.Zero and the command runs
            // with exactly the pre-job behavior (taskkill reap + ClassifyReapResult + the
            // still-alive message below). The handle is closed in the finally below, AFTER
            // the wait/reap sequence — that close is the authoritative kill for anything
            // still contained. Never throws, by construction (WindowsJobObject.cs).
            IntPtr jobHandle = IntPtr.Zero;

            try
            {
                psi.WorkingDirectory       = WorkingDirectory;
                psi.UseShellExecute        = false;
                psi.RedirectStandardInput  = true;   // so we can close it → instant EOF for the child
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError  = true;
                psi.CreateNoWindow         = true;
                psi.WindowStyle            = ProcessWindowStyle.Hidden;

                // Git for Windows hangs when spawned with inherited redirected stdio handles
                // it doesn't know to close. These enable git's own redirection (v2.11.0(2)).
                // Kept in the shared core so BOTH the string and argv paths get the fix.
                psi.EnvironmentVariables["GIT_REDIRECT_STDIN"] = "off";
                psi.EnvironmentVariables["GIT_REDIRECT_STDERR"] = "2>&1";

                // MSBuild's build-server / node-reuse pool deliberately outlives the invoking
                // command — a timed-out `dotnet test` leaves a persistent MSBuild worker behind
                // that kept growing (64 GB observed in the field) while the invoking shell's
                // taskkill /F /T tree was already gone. Setting MSBUILDDISABLENODEREUSE on
                // every spawned shell makes dotnet build/test exit with its worker pool, so the
                // tree kill actually reaches everything. Set unconditionally: the var is inert
                // for any process that does not read it, so the cost for non-MSBuild commands
                // is zero. Scoped to this shell's child (ProcessStartInfo.EnvironmentVariables
                // inherits + overrides), not the McpServer process itself.
                psi.EnvironmentVariables["MSBUILDDISABLENODEREUSE"] = "1";

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

                // Give every spawned child an immediate stdin EOF. We never feed stdin to
                // children; without this the child inherits the McpServer's stdio pipe (open,
                // never EOF) and any tool that reads stdin (npx, vite, npm) blocks until the
                // timeout. Closing it here generalizes the git-specific GIT_REDIRECT_STDIN fix.
                try { proc.StandardInput.Close(); } catch { /* best effort */ }

                // Assign the child to its per-command job. The accepted CREATE_SUSPENDED race:
                // a grandchild spawned in the sub-millisecond window between proc.Start() and
                // the assignment would not be contained — practically unreachable for a shell
                // child (first grandchild appears seconds later) and a conservative
                // trade-off against re-implementing CreateProcess here. On failure (most
                // commonly Win32 5 when DevMind itself is a member of a no-breakaway parent
                // job, e.g. some CI runners) we trace mcp.shell.job.degraded and continue with
                // the v1.10 behavior — the command is NEVER blocked by the job layer.
                if (jobHandle == IntPtr.Zero)
                {
                    var job = WindowsJobObject.TryCreateAndAssignJob(proc);
                    jobHandle = job.jobHandle;
                    if (job.jobHandle == IntPtr.Zero && job.reason != null)
                        DmTrace.Event("info", "mcp.shell.job.degraded",
                            new Dictionary<string, object>
                            {
                                ["reason"] = job.reason
                            });
                }

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
                    // Best-effort early kill on cancel. The authoritative reap — with outcome
                    // reporting — happens below in the timeout/cancel branch (ReapProcessTree).
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

                // Reap the process tree on timeout/cancel and report the outcome. A failed
                // reap used to be invisible (bare catch + exit code never checked) — the
                // survivor kept growing while the caller was told "timed out" and moved on.
                string reapMessage = null;
                if (timedOut || cancelled)
                {
                    var reap = ReapProcessTree(proc.Id);
                    reapMessage = reap.Succeeded ? null : $"[SHELL] Failed to reap process tree (PID {proc.Id}): {reap.Reason}";
                    if (reapMessage != null)
                        onLine?.Report(new ShellOutputLine(reapMessage, isError: true));

                    // Authoritative kill: closing the job handle (KILL_ON_JOB_CLOSE) tears
                    // down the ENTIRE contained tree in one OS operation — including the
                    // orphaned grandchildren that taskkill /F /T already failed to reach.
                    // IN ADDITION TO the taskkill reap above, never instead of it (the taskkill
                    // path is still the only mechanism on the degraded/no-job path).
                    if (jobHandle != IntPtr.Zero)
                        WindowsJobObject.ReleaseJob(jobHandle);
                    jobHandle = IntPtr.Zero;
                }

                // WaitForExit() ensures all pending OutputDataReceived/ErrorDataReceived events drain.
                // Its return value is the verification signal for CHANGE 3: false means the 5s
                // window elapsed and the process had not exited yet.
                bool waitTimedOut = !(await Task.Run(() => proc.WaitForExit(5_000)));

                // Verify the process actually died. Report it as a fact ("still alive after the
                // 5s wait") — do not assert a cause we do not have. A 1s grace window on the
                // exit event catches a slow exit before declaring the process a survivor.
                // Only reachable on the reap path: a normally completing process exits within
                // the 5s window, so waitTimedOut is false and this block never runs.
                if ((timedOut || cancelled) && waitTimedOut)
                {
                    var exitGate = await Task.WhenAny(exitTcs.Task, Task.Delay(1_000));
                    if (!ReferenceEquals(exitGate, exitTcs.Task))
                    {
                        string trigger = timedOut ? "the timeout" : "cancellation";
                        string stillAliveMsg = $"[SHELL] Process (PID {proc.Id}) is still alive 5 seconds after {trigger} and the reap attempt — it may be a detached child that outlived the tree (e.g. an MSBuild worker). Consider killing it manually.";
                        onLine?.Report(new ShellOutputLine(stillAliveMsg, isError: true));
                        reapMessage = reapMessage != null ? $"{reapMessage} {stillAliveMsg}" : stillAliveMsg;
                    }
                }

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
                        ["reap_result"]  = reapMessage ?? (timedOut || cancelled ? "ok" : "not_needed"),
                        ["stdout_lines"] = stdoutLines,
                        ["stderr_lines"] = stderrLines,
                        ["stdout_bytes"] = stdoutBytes,
                        ["stderr_bytes"] = stderrBytes
                    });

                int exitCode;
                try { exitCode = (timedOut || cancelled) ? -1 : proc.ExitCode; }
                catch { exitCode = -1; }

                if (timedOut)        onLine?.Report(new ShellOutputLine($"[SHELL] Command timed out after {timeoutSeconds} seconds.", isError: true));
                else if (cancelled)  onLine?.Report(new ShellOutputLine("[SHELL] Command cancelled.", isError: true));

                var sb = new StringBuilder();
                if (timedOut)        sb.AppendLine($"[SHELL] Command timed out after {timeoutSeconds} seconds.");
                else if (cancelled)  sb.AppendLine("[SHELL] Command cancelled.");
                if (reapMessage != null) sb.AppendLine(reapMessage);
                string buffered = outputBuffer.ToString().TrimEnd();
                if (!string.IsNullOrEmpty(buffered)) sb.Append(buffered);
                if (sb.Length == 0) { sb.Append("(no output)"); onLine?.Report(new ShellOutputLine("(no output)", isError: false)); }

                return (sb.ToString().TrimEnd(), exitCode);
            }
            catch (OperationCanceledException)
            {
                WindowsJobObject.ReleaseJob(jobHandle);
                return ("[SHELL] Command cancelled.", -1);
            }
            catch (Exception ex)
            {
                WindowsJobObject.ReleaseJob(jobHandle);
                return ($"(error: {ex.Message})", -1);
            }
            finally
            {
                // Release on every path. Normal completion: closing an empty job is a no-op.
                // Timeout/cancel: already released above (handle zeroed). Early throws
                // (proc.Start() failed after the job was armed): kills any child that
                // started inside the job before the exception surfaced.
                if (jobHandle != IntPtr.Zero)
                    WindowsJobObject.ReleaseJob(jobHandle);
            }
        }

        /// <summary>
        /// Outcome of a <c>taskkill /F /T</c> reap attempt. <see cref="Succeeded"/> is true when
        /// the reap succeeded (exit 0) OR the target was already gone (exit 128 "not found" —
        /// normal right after a process exits naturally). A non-zero exit code other than 128,
        /// an exception, or a null exit code (taskkill never produced one) is a failure: the
        /// caller must report it so a runaway survivor is no longer invisible.
        /// </summary>
        public sealed class ReapResult
        {
            public bool   Succeeded { get; }
            public string Reason    { get; }
            ReapResult(bool succeeded, string reason) { Succeeded = succeeded; Reason = reason; }
            public static ReapResult Ok(string reason)   => new ReapResult(true,  reason);
            public static ReapResult Fail(string reason) => new ReapResult(false, reason);
        }

        /// <summary>
        /// Classify a <c>taskkill /F /T</c> outcome as a success or a failure.
        /// Exit 0 (killed) and exit 128 (process not found — already exited) are BOTH
        /// non-failures. Anything else is a failure: we do not assert a cause, only
        /// report the observed code/exception.
        /// </summary>
        public static ReapResult ClassifyReapResult(int? taskkillExitCode, Exception exception = null)
        {
            if (exception != null)
                return ReapResult.Fail($"taskkill threw: {exception.Message}");
            if (taskkillExitCode == null)
                return ReapResult.Fail("taskkill produced no exit code");
            if (taskkillExitCode == 0)
                return ReapResult.Ok("process tree killed");
            if (taskkillExitCode == 128)
                return ReapResult.Ok("process not found (already exited)");
            return ReapResult.Fail($"taskkill exited with code {taskkillExitCode}");
        }

        /// <summary>
        /// Kill the process tree rooted at <paramref name="pid"/> via <c>taskkill /F /T</c> and
        /// report the outcome. The previous implementation swallowed every failure (bare catch,
        /// exit code never read) so a runaway survivor was invisible — this is the observability
        /// half of the fix. The other half is <c>MSBUILDDISABLENODEREUSE=1</c> (see above), which
        /// prevents the common MSBuild-worker-leaves-the-tree case in the first place. Windows
        /// only; on other platforms taskkill is unavailable and the failure is reported honestly.
        /// </summary>
        private static ReapResult ReapProcessTree(int pid)
        {
            try
            {
                using var tk = Process.Start(new ProcessStartInfo
                {
                    FileName        = "taskkill",
                    Arguments       = $"/F /T /PID {pid}",
                    CreateNoWindow  = true,
                    UseShellExecute = false
                });
                if (tk == null) return ReapResult.Fail("taskkill did not start");
                tk.WaitForExit(2000);
                int? code;
                try { code = tk.ExitCode; } catch { code = null; }
                return ClassifyReapResult(code);
            }
            catch (Exception ex)
            {
                return ClassifyReapResult(null, ex);
            }
        }

        /// <summary>
        /// Resolves the effective timeout in seconds for a shell command.
        /// Precedence: explicit value (if > 0) > DEVMIND_SHELL_TIMEOUT env var > 120s fallback.
        /// A value of 0 or negative from <paramref name="explicit"/> means "use the default."
        /// </summary>
        public static int ResolveTimeout(int? explicitSeconds = null)
        {
            // Layer 1: explicit per-call value (must be positive)
            if (explicitSeconds.HasValue && explicitSeconds.Value > 0)
                return explicitSeconds.Value;

            // Layer 2: DEVMIND_SHELL_TIMEOUT environment variable
            string envVal = Environment.GetEnvironmentVariable("DEVMIND_SHELL_TIMEOUT");
            if (envVal != null && int.TryParse(envVal, out int envTimeout) && envTimeout > 0)
                return envTimeout;

            // Layer 3: hardcoded fallback
            return 120;
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
        /// Wraps a PowerShell command body so that a native executable writing to stderr
        /// does not make the whole script exit non-zero (PowerShell's
        /// <c>NativeCommandError</c> trap). Without this, a successful Python/tqdm run
        /// whose progress bar goes to stderr reports exit code 1, the loop classifies it
        /// as a failure, and retries identical work — observed burning ~90 min of GPU.
        /// <para>
        /// <c>$ErrorActionPreference='Continue'</c> demotes <c>NativeCommandError</c> to a
        /// warning (non-fatal). <c>$Error.Clear()</c> empties the PowerShell automatic
        /// error-collection list so a non-fatal <c>NativeCommandError</c> does not
        /// accumulate in it. The final <c>exit $LASTEXITCODE</c> propagates the real
        /// exit code when the body's last statement was a native executable; when the
        /// body ends in a PowerShell statement ($LASTEXITCODE is $null) this is a no-op.
        /// </para>
        /// <para>
        /// <c>$ProgressPreference='SilentlyContinue'</c> stops PowerShell serialising
        /// progress records ("Preparing modules for first use.") to stderr as a CLIXML
        /// blob. With stderr redirected, every spawn otherwise emits ~600 bytes of
        /// <c>#&lt; CLIXML &lt;Objs …&gt;</c> noise into the tool result (2 stderr lines on all
        /// 28 spawns in the 2026-09-01 trace) — tokens for nothing, and it hides real stderr.
        /// </para>
        /// <para>
        /// The body runs inside <c>&amp; { … } 6&gt;&amp;1 3&gt;&amp;1 | … | Out-String -Stream</c> so the
        /// Information stream (<c>Write-Host</c>) and the Warning stream land in stdout as plain
        /// text. With stderr redirected and no console host, PowerShell otherwise serialises every
        /// <c>Write-Host</c> line as a ~700-byte CLIXML <c>InformationRecord</c> on stderr
        /// (job-1390, 2026-09-02: a dozen Write-Host lines became ~9 KB of XML per shell call).
        /// Two non-obvious parts, both verified on PowerShell 5.1: (1) a merged WarningRecord
        /// that reaches the host unconverted is re-routed to stderr as CLIXML anyway, so it is
        /// stringified in a <c>ForEach-Object</c>; (2) the trailing <c>exit</c> runs before the
        /// host's deferred formatter flushes object output, which silently drops tables — hence
        /// <c>Out-String -Stream</c>, which formats inside the pipeline. <c>$LASTEXITCODE</c> is
        /// global, so a native command's exit code inside the block still reaches the final
        /// <c>exit</c>.
        /// </para>
        /// </summary>
        private static string WrapForPowerShell(string command)
        {
            return "$ErrorActionPreference = 'Continue';\n" +
                   "$ProgressPreference = 'SilentlyContinue';\n" +
                   "$Error.Clear();\n" +
                   "& {\n" +
                   command + "\n" +
                   "} 6>&1 3>&1 | ForEach-Object { if ($_ -is [System.Management.Automation.WarningRecord]) { \"WARNING: $_\" } else { $_ } } | Out-String -Stream -Width 200\n" +
                   "if ($null -ne $LASTEXITCODE) { exit $LASTEXITCODE }\n";
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
        /// Rewrites a statement-level <c> &amp;&amp; </c> to <c>; </c> — Windows PowerShell 5.1 (the
        /// shell run_shell uses) has no <c>&amp;&amp;</c> operator and rejects it as a parse error.
        /// Only <see cref="TokenizeShellSpans"/> code spans are rewritten: the old blind
        /// <c>Replace</c> turned C# <c>wpid == pid &amp;&amp; IsWindowVisible(h)</c> inside an Add-Type
        /// here-string into <c>wpid == pid; IsWindowVisible(h)</c> (job-1697).
        /// <c>;</c> runs the next statement even when the previous one failed — unchanged behaviour.
        /// </summary>
        internal static string TranslateChainOperators(string command)
        {
            if (string.IsNullOrEmpty(command) || command.IndexOf(" && ", StringComparison.Ordinal) < 0)
                return command;
            return RewriteSpans(command, kind => kind == ShellSpanKind.Code, code => code.Replace(" && ", "; "));
        }

        /// <summary>
        /// Expands cmd-style <c>%VAR%</c>. <c>%VAR%</c> is sugar for <c>$env:VAR</c>, so it follows
        /// PowerShell interpolation rules: expanded at statement level and inside <c>"…"</c>, left
        /// alone inside <c>'…'</c>, here-strings and comments — use single quotes for a literal
        /// <c>%NAME%</c>. Unknown <c>%...%</c> sequences are left untouched
        /// (<see cref="Environment.ExpandEnvironmentVariables"/>). Before H-27 the whole command was
        /// expanded, corrupting e.g. a C# format string inside an Add-Type here-string.
        /// </summary>
        internal static string ExpandCmdStyleEnvironmentVariables(string command)
        {
            if (string.IsNullOrEmpty(command) || command.IndexOf('%') < 0)
                return command;
            return RewriteSpans(command,
                kind => kind == ShellSpanKind.Code || kind == ShellSpanKind.DoubleQuoted,
                Environment.ExpandEnvironmentVariables);
        }

        private static string RewriteSpans(string command, Func<ShellSpanKind, bool> applies, Func<string, string> rewrite)
        {
            var sb = new StringBuilder(command.Length);
            foreach (var (start, length, kind) in TokenizeShellSpans(command))
            {
                string text = command.Substring(start, length);
                sb.Append(applies(kind) ? rewrite(text) : text);
            }
            return sb.ToString();
        }

        internal enum ShellSpanKind { Code, SingleQuoted, DoubleQuoted, HereString, Comment }

        /// <summary>
        /// Splits a PowerShell command into consecutive spans that together cover it exactly:
        /// statement-level <see cref="ShellSpanKind.Code"/>, <c>'…'</c> and <c>"…"</c> strings
        /// (<c>''</c> and backtick escapes), <c>@'…'@</c> / <c>@"…"@</c> here-strings (terminator at
        /// column 0), and <c>#</c> line / <c>&lt;# … #&gt;</c> block comments. Each rewrite pass picks
        /// the kinds it applies to. An unterminated span runs to the end of the command.
        /// </summary>
        internal static IEnumerable<(int Start, int Length, ShellSpanKind Kind)> TokenizeShellSpans(string command)
        {
            int n = command.Length;
            int codeStart = 0;
            int i = 0;
            while (i < n)
            {
                char c = command[i];
                int end = -1;
                var kind = ShellSpanKind.Code;

                if (c == '@' && i + 1 < n && (command[i + 1] == '\'' || command[i + 1] == '"')
                    && IsHereStringOpener(command, i + 2))
                {
                    kind = ShellSpanKind.HereString;
                    // Terminator: the quote + '@' at column 0 of a later line.
                    int close = command.IndexOf("\n" + command[i + 1] + "@", i + 2, StringComparison.Ordinal);
                    end = close < 0 ? n : close + 3;
                }
                else if (c == '\'')
                {
                    kind = ShellSpanKind.SingleQuoted;
                    // '' inside a single-quoted string closes and reopens — same span either way.
                    int close = command.IndexOf('\'', i + 1);
                    end = close < 0 ? n : close + 1;
                }
                else if (c == '"')
                {
                    kind = ShellSpanKind.DoubleQuoted;
                    int j = i + 1;
                    while (j < n && command[j] != '"')
                        j += command[j] == '`' ? 2 : 1;   // backtick escapes the next char
                    end = j >= n ? n : j + 1;
                }
                else if (c == '<' && i + 1 < n && command[i + 1] == '#')
                {
                    kind = ShellSpanKind.Comment;
                    int close = command.IndexOf("#>", i + 2, StringComparison.Ordinal);
                    end = close < 0 ? n : close + 2;
                }
                else if (c == '#' && (i == 0 || char.IsWhiteSpace(command[i - 1]) || ";({|".IndexOf(command[i - 1]) >= 0))
                {
                    kind = ShellSpanKind.Comment;
                    int close = command.IndexOf('\n', i);
                    end = close < 0 ? n : close;
                }

                if (end > i)
                {
                    if (i > codeStart) yield return (codeStart, i - codeStart, ShellSpanKind.Code);
                    yield return (i, end - i, kind);
                    i = end;
                    codeStart = end;
                }
                else
                {
                    i++;
                }
            }
            if (n > codeStart) yield return (codeStart, n - codeStart, ShellSpanKind.Code);
        }

        // A here-string opener (@' or @") must be followed by nothing but whitespace up to the newline.
        private static bool IsHereStringOpener(string command, int afterQuote)
        {
            for (int j = afterQuote; j < command.Length; j++)
            {
                char c = command[j];
                if (c == '\n' || c == '\r') return true;
                if (c != ' ' && c != '\t') return false;
            }
            return false;
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
