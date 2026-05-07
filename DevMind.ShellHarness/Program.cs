// File: Program.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// DevMind.ShellHarness — isolation harness for diagnosing silent output-loss bug.
// Runs the same PowerShell command through three invocation paths and prints results
// to the terminal. No tracing, no DI, no logging framework.

using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DevMind;

namespace DevMind.ShellHarness
{
    internal static class Program
    {
        private static async Task<int> Main(string[] args)
        {
            string command = ParseCommandArg(args)
                ?? "& \"C:\\Program Files\\dotnet\\dotnet.exe\" --version";

            Console.WriteLine("════════════════════════════════════════════");
            Console.WriteLine("DevMind.ShellHarness");
            Console.WriteLine("════════════════════════════════════════════");
            Console.WriteLine($"Command: {command}");
            Console.WriteLine($"Working dir: {Environment.CurrentDirectory}");
            Console.WriteLine($"PID: {Process.GetCurrentProcess().Id}");
            Console.WriteLine();

            await RunPath1_ShellRunner(command);
            await RunPath2_BareRedirected(command);
            RunPath3_BareInherited(command);

            return 0;
        }

        private static string ParseCommandArg(string[] args)
        {
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], "--command", StringComparison.OrdinalIgnoreCase))
                    return args[i + 1];
            }
            return null;
        }

        // ── PATH 1: ShellRunner.ExecuteAsync ──────────────────────────────
        private static async Task RunPath1_ShellRunner(string command)
        {
            Console.WriteLine("--- PATH 1: ShellRunner.ExecuteAsync ---");
            var sw = Stopwatch.StartNew();
            try
            {
                var runner = new ShellRunner(Environment.CurrentDirectory);
                var (output, exitCode) = await runner.ExecuteAsync(command);
                sw.Stop();

                Console.WriteLine($"  exit: {exitCode}");
                Console.WriteLine($"  duration: {sw.ElapsedMilliseconds}ms");
                Console.WriteLine($"  output length: {output?.Length ?? 0} chars");
                Console.WriteLine($"  output:");
                Console.WriteLine(IndentLines(output ?? "(null)", "    "));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  EXCEPTION: {ex.GetType().Name}: {ex.Message}");
            }
            Console.WriteLine();
        }

        // ── PATH 2: Bare Process + redirected pipes ───────────────────────
        private static async Task RunPath2_BareRedirected(string command)
        {
            Console.WriteLine("--- PATH 2: Bare Process, redirected pipes ---");
            var sw = Stopwatch.StartNew();
            try
            {
                string sanitized = command.Replace("\"", "\\\"");
                string psArgs = $"-NoProfile -NonInteractive -Command \"{sanitized}\"";

                var psi = new ProcessStartInfo("powershell.exe", psArgs)
                {
                    WorkingDirectory = Environment.CurrentDirectory,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                var stdout = new System.Text.StringBuilder();
                var stderr = new System.Text.StringBuilder();

                using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
                proc.OutputDataReceived += (s, e) => { if (e.Data != null) stdout.AppendLine(e.Data); };
                proc.ErrorDataReceived  += (s, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };

                proc.Start();
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();

                await Task.Run(() => proc.WaitForExit(30_000));
                sw.Stop();

                Console.WriteLine($"  exit: {proc.ExitCode}");
                Console.WriteLine($"  duration: {sw.ElapsedMilliseconds}ms");
                Console.WriteLine($"  stdout length: {stdout.Length} chars");
                Console.WriteLine($"  stderr length: {stderr.Length} chars");
                Console.WriteLine($"  stdout:");
                Console.WriteLine(IndentLines(stdout.ToString(), "    "));
                Console.WriteLine($"  stderr:");
                Console.WriteLine(IndentLines(stderr.ToString(), "    "));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  EXCEPTION: {ex.GetType().Name}: {ex.Message}");
            }
            Console.WriteLine();
        }

        // ── PATH 3: Bare Process, INHERITED stdio (no redirection) ───────
        private static void RunPath3_BareInherited(string command)
        {
            Console.WriteLine("--- PATH 3: Bare Process, INHERITED stdio ---");
            Console.WriteLine("  (output below this line is from PowerShell directly,");
            Console.WriteLine("   written to this terminal's console — no capture)");
            Console.WriteLine("  ┌─────");
            var sw = Stopwatch.StartNew();
            try
            {
                string sanitized = command.Replace("\"", "\\\"");
                string psArgs = $"-NoProfile -NonInteractive -Command \"{sanitized}\"";

                var psi = new ProcessStartInfo("powershell.exe", psArgs)
                {
                    WorkingDirectory = Environment.CurrentDirectory,
                    UseShellExecute = false,
                    // NO RedirectStandardOutput
                    // NO RedirectStandardError
                    // NO CreateNoWindow
                };

                using var proc = Process.Start(psi);
                proc.WaitForExit(30_000);
                sw.Stop();

                Console.WriteLine("  └─────");
                Console.WriteLine($"  exit: {proc.ExitCode}");
                Console.WriteLine($"  duration: {sw.ElapsedMilliseconds}ms");
            }
            catch (Exception ex)
            {
                Console.WriteLine("  └─────");
                Console.WriteLine($"  EXCEPTION: {ex.GetType().Name}: {ex.Message}");
            }
            Console.WriteLine();
        }

        // ── helpers ───────────────────────────────────────────────────────
        private static string IndentLines(string text, string prefix)
        {
            if (string.IsNullOrEmpty(text)) return prefix + "(empty)";
            var lines = text.Split('\n');
            var sb = new System.Text.StringBuilder();
            foreach (var line in lines)
            {
                sb.Append(prefix);
                sb.AppendLine(line.TrimEnd('\r'));
            }
            return sb.ToString().TrimEnd();
        }
    }
}
