// Diagnostic probe: does setting StandardOutputEncoding fix
// native-exe output capture under powershell.exe with redirected
// stdio? Mirrors the exact ProcessStartInfo flag set used by
// DevMind.Core.ShellRunner.ExecuteAsync (lines 85-93 of
// ShellRunner.cs v1.4) and runs the same command twice — once
// without StandardOutputEncoding (control), once with UTF-8 (fix
// candidate) — printing captured byte counts and content for each.

using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace DevMind.ShellEncodingProbe;

internal static class Program
{
    private const string Shell = "powershell.exe";
    private const string ShellArgs =
        "-NoProfile -NonInteractive -Command \"git status\"";

    private static async Task<int> Main()
    {
        // Run 1: no encoding override (reproduces ShellRunner today).
        await RunProbeAsync(
            runNumber: 1,
            description: "default (StandardOutputEncoding NOT set)",
            applyEncoding: false);

        Console.WriteLine();

        // Run 2: explicit UTF-8 on both redirected streams.
        await RunProbeAsync(
            runNumber: 2,
            description: "StandardOutputEncoding = StandardErrorEncoding = UTF8",
            applyEncoding: true);

        return 0;
    }

    private static async Task RunProbeAsync(
        int runNumber,
        string description,
        bool applyEncoding)
    {
        // Reproduce ShellRunner.cs:85-93 verbatim — same flags, same
        // positional ctor args, same property settings. The only
        // intentional divergence is the conditional encoding block
        // below, which is the variable under test.
        var psi = new ProcessStartInfo(Shell, ShellArgs)
        {
            WorkingDirectory       = Directory.GetCurrentDirectory(),
            UseShellExecute        = false,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            CreateNoWindow         = true,
            WindowStyle            = ProcessWindowStyle.Hidden,
        };

        if (applyEncoding)
        {
            psi.StandardOutputEncoding = Encoding.UTF8;
            psi.StandardErrorEncoding  = Encoding.UTF8;
        }

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var exitTcs = new TaskCompletionSource<bool>();

        using var proc = new Process();
        proc.StartInfo = psi;
        proc.EnableRaisingEvents = true;
        proc.Exited             += (_, _) => exitTcs.TrySetResult(true);
        proc.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null) stdout.AppendLine(e.Data);
        };
        proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null) stderr.AppendLine(e.Data);
        };

        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        // Cap at 30s — git --version returns instantly; anything longer
        // means something's wrong with the spawn itself, not encoding.
        var timeout = Task.Delay(TimeSpan.FromSeconds(30));
        var winner  = await Task.WhenAny(exitTcs.Task, timeout);
        bool timedOut = winner == timeout;

        // Drain pending events.
        await Task.Run(() => proc.WaitForExit(5_000));

        int exitCode;
        try   { exitCode = timedOut ? -1 : proc.ExitCode; }
        catch { exitCode = -1; }

        string stdoutText = stdout.ToString().TrimEnd();
        string stderrText = stderr.ToString().TrimEnd();
        int stdoutBytes = Encoding.UTF8.GetByteCount(stdoutText);
        int stderrBytes = Encoding.UTF8.GetByteCount(stderrText);

        Console.WriteLine($"=== Run {runNumber} (encoding: {description}) ===");
        Console.WriteLine($"exit code: {exitCode}{(timedOut ? " (TIMED OUT)" : "")}");
        Console.WriteLine($"stdout bytes captured: {stdoutBytes}");
        Console.WriteLine($"stderr bytes captured: {stderrBytes}");
        Console.WriteLine($"stdout content: {(stdoutBytes == 0 ? "(empty)" : stdoutText)}");
        Console.WriteLine($"stderr content: {(stderrBytes == 0 ? "(empty)" : stderrText)}");
    }
}
