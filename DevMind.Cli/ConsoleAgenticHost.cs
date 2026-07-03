// File: ConsoleAgenticHost.cs  v2.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Console skin of BufferedAgenticHost (Core) for the DevMind CLI REPL. All host logic
// lives in the base class; this subclass supplies only the interactive pieces:
//   * ANSI-colored Console output sink (plain text when redirected)
//   * write-guard y/N prompt
//   * agentic continue/stop prompt
//   * four-way y/n/a/q patch approval prompt

using System;
using System.Threading.Tasks;

namespace DevMind
{
    /// <summary>Interactive console implementation of <see cref="IAgenticHost"/>.</summary>
    public sealed class ConsoleAgenticHost : BufferedAgenticHost
    {
        public ConsoleAgenticHost(string workingDirectory, Action cancelTurn = null)
            : base(workingDirectory, cancelTurn, WriteConsole)
        {
        }

        private static void WriteConsole(string text, OutputColor color)
        {
            if (Console.IsOutputRedirected)
            {
                Console.Write(text);
                return;
            }
            Console.Write(ColorToAnsi(color) + text + "\x1b[0m");
        }

        private static string ColorToAnsi(OutputColor color) => color switch
        {
            OutputColor.Dim      => "\x1b[90m",
            OutputColor.Input    => "\x1b[36m",
            OutputColor.Error    => "\x1b[91m",
            OutputColor.Success  => "\x1b[92m",
            OutputColor.Thinking => "\x1b[35m",
            OutputColor.Warning  => "\x1b[93m",
            _                    => "\x1b[0m",
        };

        // ── Interactive prompt overrides ──────────────────────────────────────────

        protected override Task<bool> ConfirmContinueCoreAsync(string message)
        {
            // 'c'/'y' (or empty) continues; anything else stops. A non-interactive run
            // (no console input) continues so it doesn't hang.
            AppendOutput($"\n[AGENTIC] {message} [C]ontinue / [S]top: ", OutputColor.Warning);
            string answer;
            try { answer = Console.ReadLine(); }
            catch { return Task.FromResult(true); }
            if (answer == null) return Task.FromResult(true);
            string a = answer.Trim().ToLowerInvariant();
            bool cont = a.Length == 0 || a.StartsWith("c") || a.StartsWith("y");
            return Task.FromResult(cont);
        }

        protected override Task<bool> ConfirmUnreadFileWriteAsync(string fileNameOnly)
        {
            AppendOutput(
                $"[WRITE GUARD] \"{fileNameOnly}\" was not read during this task. Allow write? [y/N] ",
                OutputColor.Warning);
            string answer = (Console.ReadLine() ?? "").Trim();
            return Task.FromResult(answer.Equals("y", StringComparison.OrdinalIgnoreCase));
        }

        // Four-way prompt: y = apply, n = skip, a = apply + auto-approve rest, q = cancel turn.
        // Repeats on unrecognized input.
        protected override Task<PatchDecision> PromptPatchDecisionAsync(PatchResolveResult resolved)
        {
            while (true)
            {
                AppendOutput($"Apply patch to {resolved.FileName}? [y/n/a/q] ", OutputColor.Warning);
                string answer = (Console.ReadLine() ?? "").Trim().ToLowerInvariant();

                if (answer == "y") return Task.FromResult(PatchDecision.Approve);
                if (answer == "n") return Task.FromResult(PatchDecision.Skip);
                if (answer == "a") return Task.FromResult(PatchDecision.ApproveAll);
                if (answer == "q") return Task.FromResult(PatchDecision.CancelTurn);
                AppendOutput("Please enter y, n, a, or q.\n", OutputColor.Dim);
            }
        }
    }
}
