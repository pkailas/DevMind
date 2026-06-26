// File: TuiAgenticHost.Debug.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// DAP debugging orchestration for the TUI host. Drives a Core DapClient
// (netcoredbg over stdio) and surfaces all output through AppendOutputLocal.
// Lives in the TUI skin so DevMind.Core stays UI-agnostic.

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DevMind
{
    public sealed partial class TuiAgenticHost
    {
        private DapClient _dap;
        // Desired breakpoints, kept independent of any session so they can be set
        // before a launch/attach and survive across sessions.
        private readonly DapSession _pendingBreaks = new DapSession();

        /// <summary>
        /// Entry point for the /debug slash command. Returns an immediate status
        /// line (rendered once by the dispatcher); breakpoint hits and debuggee
        /// output arrive asynchronously via AppendOutputLocal.
        /// </summary>
        public async Task<string> HandleDebugCommandAsync(string[] args)
        {
            if (args == null || args.Length == 0)
                return "Usage: /debug launch|attach|break|continue|step|stepin|stepout|inspect|stack|eval|detach|stop ...";

            string sub = args[0].Trim().ToLowerInvariant();
            try
            {
                switch (sub)
                {
                    case "launch": return await DebugLaunchAsync(args);
                    case "attach": return await DebugAttachAsync(args);
                    case "break": return await DebugBreakAsync(args);
                    case "continue": return await DebugResumeAsync("continue");
                    case "step": return await DebugResumeAsync("step");
                    case "stepin": return await DebugResumeAsync("stepin");
                    case "stepout": return await DebugResumeAsync("stepout");
                    case "inspect": return await DebugInspectAsync(args);
                    case "stack": return await DebugStackAsync();
                    case "eval": return await DebugEvalAsync(args);
                    case "detach": return await DebugStopAsync(terminate: false);
                    case "stop": return await DebugStopAsync(terminate: true);
                    default: return $"Unknown /debug subcommand: {sub}. Try /help.";
                }
            }
            catch (Exception ex)
            {
                return $"[DEBUG ERROR] {ex.Message}";
            }
        }

        // -- launch / attach ------------------------------------------------------

        private async Task<string> DebugLaunchAsync(string[] args)
        {
            if (args.Length < 2) return "Usage: /debug launch <project-path>";
            if (_dap != null && _dap.IsActive) return "A debug session is already active. Use /debug stop first.";

            string raw = string.Join(" ", args.Skip(1));
            string proj = ResolveProjectPath(raw);
            if (proj == null) return $"Project not found: {raw}";

            var (dll, err) = await BuildForDebugAsync(proj);
            if (dll == null)
            {
                AppendOutputLocal($"[DEBUG] {err}\n", OutputColor.Error);
                return "Launch aborted — build failed.";
            }

            var client = NewDapClient();
            await client.InitializeAsync();
            AppendOutputLocal($"[DEBUG] Launching {Path.GetFileName(dll)} under netcoredbg...\n", OutputColor.Dim);
            string launchErr = await client.LaunchAsync(dll, Path.GetDirectoryName(dll), null, stopAtEntry: false);
            if (launchErr != null)
            {
                AppendOutputLocal($"[DEBUG] launch failed: {launchErr}\n", OutputColor.Error);
                CleanupDap();
                return "Launch failed.";
            }
            return $"Debug session launched: {Path.GetFileName(dll)} (running).";
        }

        private async Task<string> DebugAttachAsync(string[] args)
        {
            if (args.Length < 2) return "Usage: /debug attach <pid|process-name>";
            if (_dap != null && _dap.IsActive) return "A debug session is already active. Use /debug stop first.";

            string target = args[1].Trim();
            if (!int.TryParse(target, out int pid))
            {
                string name = target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                    ? target.Substring(0, target.Length - 4) : target;
                var procs = Process.GetProcessesByName(name);
                if (procs.Length == 0) return $"No running process named '{target}'.";
                if (procs.Length > 1)
                    return $"Ambiguous: {procs.Length} processes named '{target}' (PIDs: {string.Join(", ", procs.Select(p => p.Id))}). Use a PID.";
                pid = procs[0].Id;
            }

            var client = NewDapClient();
            await client.InitializeAsync();
            AppendOutputLocal($"[DEBUG] Attaching to PID {pid}...\n", OutputColor.Dim);
            string err = await client.AttachAsync(pid);
            if (err != null)
            {
                AppendOutputLocal($"[DEBUG] attach failed: {err}\n", OutputColor.Error);
                CleanupDap();
                return "Attach failed.";
            }
            return $"Attached to PID {pid}. Set breakpoints with /debug break <file> <line>.";
        }

        // -- breakpoints ----------------------------------------------------------

        private async Task<string> DebugBreakAsync(string[] args)
        {
            bool clear = args.Length >= 2 && args[1].Equals("clear", StringComparison.OrdinalIgnoreCase);
            int fileIdx = clear ? 2 : 1;
            int lineIdx = clear ? 3 : 2;
            if (args.Length <= lineIdx)
                return clear ? "Usage: /debug break clear <file> <line>" : "Usage: /debug break <file> <line>";
            if (!int.TryParse(args[lineIdx], out int line) || line < 1)
                return "Line must be a positive integer.";

            string file = ResolveSourcePath(args[fileIdx]);
            var d = _dap;
            bool active = d != null && d.IsActive;

            if (clear)
            {
                _pendingBreaks.RemoveBreakpoint(file, line);
                if (active) await d.ClearBreakpointAsync(file, line);
                return $"Breakpoint cleared: {Path.GetFileName(file)}:{line}";
            }

            _pendingBreaks.AddBreakpoint(file, line);
            if (active) await d.SetBreakpointAsync(file, line);
            return $"Breakpoint set: {Path.GetFileName(file)}:{line}" + (active ? "" : " (applies on launch/attach)");
        }

        // -- execution control ----------------------------------------------------

        private async Task<string> DebugResumeAsync(string kind)
        {
            var d = _dap;
            if (d == null || !d.IsActive) return "No active debug session.";
            if (d.Session.GetStoppedThread() == null && kind != "continue")
                return "Not stopped — nothing to step.";

            switch (kind)
            {
                case "continue": await d.ContinueAsync(); return "Continuing...";
                case "step": await d.StepOverAsync(); return "Stepping over...";
                case "stepin": await d.StepInAsync(); return "Stepping in...";
                case "stepout": await d.StepOutAsync(); return "Stepping out...";
                default: return "Unknown step.";
            }
        }

        // -- inspection -----------------------------------------------------------

        private async Task<string> DebugInspectAsync(string[] args)
        {
            if (args.Length < 2) return "Usage: /debug inspect <variable>";
            var d = _dap;
            if (d == null || !d.IsActive) return "No active debug session.";
            if (d.Session.GetCurrentFrame() == null) return "Not stopped — no current frame to inspect.";

            string name = string.Join(" ", args.Skip(1));
            var insp = await d.InspectVariableAsync(name);
            if (!insp.Found) return $"'{name}' not found in the current frame.";

            var sb = new StringBuilder();
            sb.Append($"{insp.Name} = {insp.Value}");
            if (!string.IsNullOrEmpty(insp.Type)) sb.Append($"  ({insp.Type})");
            foreach (var c in insp.Children.Take(40))
                sb.Append($"\n    {c.Name} = {c.Value}" + (string.IsNullOrEmpty(c.Type) ? "" : $"  ({c.Type})"));
            return sb.ToString();
        }

        private async Task<string> DebugStackAsync()
        {
            var d = _dap;
            if (d == null || !d.IsActive) return "No active debug session.";
            int? tid = d.Session.GetStoppedThread();
            if (tid == null) return "Not stopped — no stack to show.";

            var stack = await d.StackTraceAsync(tid.Value, 50);
            if (stack.Frames.Count == 0) return "(empty stack)";

            var sb = new StringBuilder("Call stack:");
            foreach (var f in stack.Frames)
            {
                string where = f.File != null ? $"  {Path.GetFileName(f.File)}:{f.Line}" : "";
                sb.Append($"\n  #{f.Id} {f.Name}{where}");
            }
            return sb.ToString();
        }

        private async Task<string> DebugEvalAsync(string[] args)
        {
            if (args.Length < 2) return "Usage: /debug eval <expression>";
            var d = _dap;
            if (d == null || !d.IsActive) return "No active debug session.";

            string expr = string.Join(" ", args.Skip(1));
            var ev = await d.EvaluateAsync(expr);
            if (!ev.Success) return $"eval error: {ev.Error}";
            return $"{ev.Expression} => {ev.Result}" + (string.IsNullOrEmpty(ev.Type) ? "" : $"  ({ev.Type})");
        }

        private async Task<string> DebugStopAsync(bool terminate)
        {
            var d = _dap;
            if (d == null) return "No debug session to stop.";
            try { await d.DisconnectAsync(terminate); } catch { /* adapter may already be gone */ }
            CleanupDap();
            return terminate ? "Debug session stopped (debuggee terminated)." : "Detached from debuggee.";
        }

        // -- adapter wiring & rendering ------------------------------------------

        private DapClient NewDapClient()
        {
            CleanupDap();
            var client = new DapClient(DapClient.ResolveAdapterPath());
            client.OnOutput = o => AppendOutputLocal(
                o.Text ?? "",
                string.Equals(o.Category, "stderr", StringComparison.OrdinalIgnoreCase) ? OutputColor.Error : OutputColor.Dim);
            client.OnLog = m => AppendOutputLocal(m.EndsWith("\n", StringComparison.Ordinal) ? m : m + "\n", OutputColor.Dim);
            client.OnStopped = hit => { _ = RenderStopAsync(hit); };
            client.OnTerminated = code => AppendOutputLocal(
                $"[DEBUG] Debuggee {(code.HasValue ? $"exited (code {code.Value})" : "terminated")}.\n", OutputColor.Warning);

            // Seed the new session with the desired breakpoints.
            foreach (var f in _pendingBreaks.GetBreakpointFiles())
                foreach (var ln in _pendingBreaks.GetBreakpoints(f))
                    client.Session.AddBreakpoint(f, ln);

            _dap = client;
            return client;
        }

        private async Task RenderStopAsync(BreakpointHit hit)
        {
            try
            {
                var d = _dap;
                if (d == null) return;

                string loc = hit.File != null ? $"{Path.GetFileName(hit.File)}:{hit.Line}" : "(no source)";
                string desc = string.IsNullOrEmpty(hit.Description) ? "" : $" — {hit.Description}";
                AppendOutputLocal($"\n[DEBUG] Stopped ({hit.Reason}) at {loc}{desc}\n", OutputColor.Warning);

                var stack = await d.StackTraceAsync(hit.ThreadId, 20);
                if (stack.Frames.Count > 0)
                {
                    AppendOutputLocal("  Call stack:\n", OutputColor.Dim);
                    foreach (var f in stack.Frames)
                    {
                        string where = f.File != null ? $"  {Path.GetFileName(f.File)}:{f.Line}" : "";
                        AppendOutputLocal($"    #{f.Id} {f.Name}{where}\n", OutputColor.Dim);
                    }
                }

                int? frame = d.Session.GetCurrentFrame();
                if (frame != null)
                {
                    foreach (var sc in await d.ScopesAsync(frame.Value))
                    {
                        if (sc.VariablesReference <= 0) continue;
                        var vars = await d.VariablesAsync(sc.VariablesReference);
                        if (vars.Count == 0) continue;
                        AppendOutputLocal($"  {sc.Name}:\n", OutputColor.Dim);
                        foreach (var v in vars.Take(40))
                            AppendOutputLocal($"    {v.Name} = {v.Value}" + (string.IsNullOrEmpty(v.Type) ? "" : $"  ({v.Type})") + "\n", OutputColor.Normal);
                    }
                }

                AppendOutputLocal("  → /debug continue | step | stepin | stepout | inspect <var> | eval <expr> | stack\n", OutputColor.Dim);
            }
            catch (Exception ex)
            {
                AppendOutputLocal($"[DEBUG] stop-render error: {ex.Message}\n", OutputColor.Error);
            }
        }

        private void CleanupDap()
        {
            try { _dap?.Dispose(); } catch { /* best effort */ }
            _dap = null;
        }

        // -- helpers --------------------------------------------------------------

        private async Task<(string dll, string error)> BuildForDebugAsync(string projectPath)
        {
            string cwd = _shellRunner.WorkingDirectory;
            AppendOutputLocal($"[DEBUG] Building {Path.GetFileName(projectPath)} (Debug)...\n", OutputColor.Dim);

            // --getProperty:TargetPath builds and prints the output assembly path.
            var (code, output) = await RunCapturedAsync("dotnet",
                $"build \"{projectPath}\" -c Debug --getProperty:TargetPath", cwd);
            if (code != 0)
                return (null, "build failed:\n" + TailLines(output, 20));

            string dll = output
                .Replace("\r\n", "\n").Split('\n')
                .Select(s => s.Trim())
                .LastOrDefault(s => s.Length > 0) ?? "";
            if (string.IsNullOrEmpty(dll) || !File.Exists(dll))
                return (null, $"build succeeded but could not resolve TargetPath (got '{dll}').");
            return (dll, null);
        }

        private static async Task<(int code, string output)> RunCapturedAsync(string fileName, string arguments, string cwd)
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                WorkingDirectory = cwd,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            string stdout = await p.StandardOutput.ReadToEndAsync();
            string stderr = await p.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync();
            return (p.ExitCode, string.IsNullOrEmpty(stderr) ? stdout : stdout + "\n" + stderr);
        }

        private string ResolveProjectPath(string raw)
        {
            raw = raw.Trim().Trim('"');
            string p = Path.IsPathRooted(raw) ? raw : Path.Combine(_shellRunner.WorkingDirectory, raw);
            if (File.Exists(p)) return Path.GetFullPath(p);
            if (Directory.Exists(p))
            {
                var proj = Directory.GetFiles(p, "*.csproj").FirstOrDefault()
                           ?? Directory.GetFiles(p, "*.fsproj").FirstOrDefault();
                return proj != null ? Path.GetFullPath(proj) : null;
            }
            return null;
        }

        private string ResolveSourcePath(string raw)
        {
            raw = raw.Trim().Trim('"');
            string p = Path.IsPathRooted(raw) ? raw : Path.Combine(_shellRunner.WorkingDirectory, raw);
            return Path.GetFullPath(p);
        }

        private static string TailLines(string s, int n)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var lines = s.Replace("\r\n", "\n").Split('\n');
            return string.Join("\n", lines.Reverse().Take(n).Reverse());
        }
    }
}
