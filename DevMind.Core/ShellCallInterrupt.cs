// File: ShellCallInterrupt.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// The per-call interrupt behind an OVERRIDE steer: a host opens a window around each
// run_shell / run_build / run_tests call (Begin), passes the call's token to
// ShellRunner.ExecuteAsync as its interruptToken (the turn/job token is passed unchanged),
// and Cancel — from the steering thread — ends that call only, so the loop reaches its
// next iteration boundary and folds the steer now instead of waiting out a runaway
// command. Shared by BufferedAgenticHost (headless, the driver steers) and
// TuiAgenticHost (the operator steers); each host adds its own journal/transcript lines.

using System;
using System.Threading;

namespace DevMind
{
    public sealed class ShellCallInterrupt
    {
        /// <summary>Tool result line when a headless job's driver steered.</summary>
        public const string DriverSteerMessage =
            "[SHELL] Cancelled by the driver's override steer — the steer follows at this iteration boundary.";

        /// <summary>Tool result line when the TUI operator steered.</summary>
        public const string OperatorSteerMessage =
            "[SHELL] Cancelled by the operator's override steer — the steer follows at this iteration boundary.";

        private readonly object _lock = new object();
        private Call _current;

        /// <summary>The line a cancelled call's result starts with (<see cref="ResultFor"/>).</summary>
        public string Message { get; }

        public ShellCallInterrupt(string message) => Message = message;

        /// <summary>
        /// Opens the interrupt window for one call. <paramref name="outer"/> is the turn/job
        /// token: the call's token is linked to it, and a cancel that came from it is NOT
        /// reported as a steer cancel.
        /// </summary>
        public Call Begin(CancellationToken outer)
        {
            var call = new Call(this, outer);
            lock (_lock) _current = call;
            return call;
        }

        /// <summary>
        /// Cancels the in-flight call, if there is one. Only the call — never the turn/job
        /// token. Returns whether a call was cancelled. Thread-safe.
        /// </summary>
        public bool Cancel(string reason)
        {
            Call call;
            lock (_lock)
            {
                call = _current;
                if (call == null || call.Ended || call.Cts.IsCancellationRequested) return false;
                call.Reason = reason ?? "";
            }
            // Outside the lock: Cancel runs the runner's kill callback synchronously.
            try { call.Cts.Cancel(); }
            catch (ObjectDisposedException) { return false; } // the call ended in between
            return true;
        }

        /// <summary>
        /// The tool result for a steer-cancelled call: <see cref="Message"/> in place of the
        /// runner's generic "[SHELL] Command cancelled." line, followed by whatever output the
        /// call produced before it was cancelled.
        /// </summary>
        public string ResultFor(string output)
        {
            string rest = (output ?? "").Replace("[SHELL] Command cancelled.", "").Trim();
            return rest.Length == 0 || rest == "(no output)" ? Message : Message + "\n" + rest;
        }

        private string EndCall(Call call)
        {
            lock (_lock)
            {
                if (call.Ended) return null;
                call.Ended = true;
                if (ReferenceEquals(_current, call)) _current = null;
                bool steerCancelled = call.Cts.IsCancellationRequested && !call.Outer.IsCancellationRequested;
                string reason = steerCancelled ? call.Reason : null;
                call.Cts.Dispose();
                return reason;
            }
        }

        /// <summary>One call's interrupt window. Dispose (or End) closes it.</summary>
        public sealed class Call : IDisposable
        {
            private readonly ShellCallInterrupt _owner;
            internal readonly CancellationTokenSource Cts;
            internal readonly CancellationToken Outer;
            internal string Reason;
            internal bool Ended;

            internal Call(ShellCallInterrupt owner, CancellationToken outer)
            {
                _owner = owner;
                Outer = outer;
                Cts = CancellationTokenSource.CreateLinkedTokenSource(outer);
                Token = Cts.Token;
            }

            /// <summary>Pass as ShellRunner.ExecuteAsync's interruptToken.</summary>
            public CancellationToken Token { get; }

            /// <summary>
            /// Closes the window. Returns the Cancel reason when THAT is what ended the call
            /// (so the caller reports it), or null — a turn/job cancel (which also trips the
            /// linked token) or a timeout keeps its ordinary result. Idempotent: later calls
            /// return null.
            /// </summary>
            public string End() => _owner.EndCall(this);

            public void Dispose() => End();
        }
    }
}
