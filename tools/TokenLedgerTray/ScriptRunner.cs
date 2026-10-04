using System.Diagnostics;
using System.Text;

namespace DevMind.TokenLedgerTray;

/// <summary>Outcome of one script run.</summary>
internal sealed record ScriptResult(bool TimedOut, int ExitCode, IReadOnlyList<string> StderrTail)
{
    public bool Success => !TimedOut && ExitCode == 0;

    /// <summary>Last non-empty stderr line, or empty when there was none.</summary>
    public string LastStderrLine
    {
        get
        {
            for (var i = StderrTail.Count - 1; i >= 0; i--)
            {
                var line = StderrTail[i].Trim();
                if (line.Length > 0)
                {
                    return line;
                }
            }

            return string.Empty;
        }
    }
}

/// <summary>
/// Runs a PowerShell ledger script hidden and fully asynchronous, with stdout and
/// stderr drained through the async event readers (never ReadToEnd - that
/// deadlocks once the child fills the 4 KB pipe buffer and blocks on write while
/// the parent blocks on exit).
///
/// Timeout kills the whole process tree by putting the child in a job object
/// flagged KILL_ON_JOB_CLOSE and closing the job handle.
/// </summary>
internal static class ScriptRunner
{
    /// <summary>Windows PowerShell 5.1, the runtime the ledger scripts target.</summary>
    public static string PowerShellPath =>
        Path.Combine(
            Environment.GetEnvironmentVariable("WINDIR") ?? @"C:\Windows",
            "System32",
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");

    /// <summary>
    /// Runs <paramref name="scriptPath"/> with -NoProfile -ExecutionPolicy Bypass
    /// -File and the given extra arguments. Completes on exit or after
    /// <paramref name="timeout"/>.
    /// </summary>
    public static async Task<ScriptResult> RunAsync(
        string scriptPath,
        IEnumerable<string> extraArguments,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = PowerShellPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(scriptPath) ?? Environment.CurrentDirectory,
        };

        // ArgumentList, not a hand-quoted command line: the paths can contain
        // spaces and manual quoting is how that goes wrong.
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-ExecutionPolicy");
        psi.ArgumentList.Add("Bypass");
        psi.ArgumentList.Add("-File");
        psi.ArgumentList.Add(scriptPath);
        foreach (var arg in extraArguments)
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = new Process { StartInfo = psi };

        var stdout = new List<string>();
        var stderr = new List<string>();

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                lock (stdout) { stdout.Add(e.Data); }
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                lock (stderr) { stderr.Add(e.Data); }
            }
        };

        if (!process.Start())
        {
            return new ScriptResult(false, -1, ["Could not start " + PowerShellPath]);
        }

        // Job object first, so a timeout can take the tree down rather than just
        // the direct child (powershell.exe may have spawned children of its own).
        var job = Native.CreateKillOnCloseJob();
        var inJob = Native.Assign(job, process);

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        var exitCode = -1;
        var timedOut = false;
        try
        {
            // WaitForExitAsync(CancellationToken) returns a non-generic Task, so
            // the code is read from ExitCode afterwards (the synchronous
            // WaitForExit(int) is the one that returns the code).
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            exitCode = process.ExitCode;
        }
        catch (OperationCanceledException)
        {
            timedOut = true;
            exitCode = -1;
        }

        if (timedOut)
        {
            if (inJob)
            {
                // TerminateProcessOnJobClose: closing the handle kills every
                // process in the job.
                Native.CloseHandle(job);
                job = IntPtr.Zero;
            }
            else
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // Already gone.
                }
            }

            try
            {
                // Drain whatever the pipes still hold so the stderr tail is real.
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // Ignore: the kill path is best-effort.
            }
        }

        if (job != IntPtr.Zero)
        {
            Native.CloseHandle(job);
        }

        lock (stderr)
        {
            return new ScriptResult(timedOut, exitCode, stderr.ToArray());
        }
    }

    /// <summary>Runs Update-TokenLedger.ps1 for a ledger directory.</summary>
    public static Task<ScriptResult> RefreshAsync(string scriptsDir, string ledgerDir, TimeSpan timeout)
    {
        return RunAsync(
            Path.Combine(scriptsDir, "Update-TokenLedger.ps1"),
            ["-LedgerDir", ledgerDir],
            timeout);
    }

    /// <summary>Runs Add-PlanUsage.ps1 with the dialog's values.</summary>
    public static Task<ScriptResult> PlanUsageAsync(
        string scriptsDir,
        string ledgerDir,
        int sessionPct,
        int weeklyPct,
        string note)
    {
        var args = new List<string>
        {
            "-SessionPct",
            sessionPct.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "-WeeklyPct",
            weeklyPct.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };

        // -Note is optional; an empty note is omitted rather than passed blank.
        if (!string.IsNullOrWhiteSpace(note))
        {
            args.Add("-Note");
            args.Add(note.Trim());
        }

        args.Add("-LedgerDir");
        args.Add(ledgerDir);

        return RunAsync(Path.Combine(scriptsDir, "Add-PlanUsage.ps1"), args, TimeSpan.FromMinutes(1));
    }

    /// <summary>Joins stderr tail into a short single-line message for a balloon.</summary>
    public static string DescribeFailure(ScriptResult result, string what)
    {
        var sb = new StringBuilder();
        if (result.TimedOut)
        {
            sb.Append(what).Append(": timed out");
        }
        else
        {
            sb.Append(what).Append(": exit code ").Append(result.ExitCode);
        }

        var last = result.LastStderrLine;
        if (last.Length > 0)
        {
            sb.Append(" | ").Append(last);
        }

        return LedgerSummary.Clamp(sb.ToString());
    }
}
