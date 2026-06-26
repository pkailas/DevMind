// File: DapSession.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Mutable state for one active debug session. DAP events arrive on the
// DapClient reader thread while slash-command handlers read state on the UI
// thread, so every field is guarded by a single lock.

using System;
using System.Collections.Generic;
using System.Linq;

namespace DevMind
{
    /// <summary>
    /// Thread-safe state for a debug session: lifecycle state, the current
    /// stopped thread/frame context, and the set of desired breakpoints
    /// (kept independent of the adapter so they can be set before launch).
    /// </summary>
    public sealed class DapSession
    {
        private readonly object _gate = new object();
        private readonly Dictionary<string, HashSet<int>> _breakpoints =
            new Dictionary<string, HashSet<int>>(StringComparer.OrdinalIgnoreCase);

        private DebugSessionState _state = DebugSessionState.Inactive;
        private int? _stoppedThreadId;
        private int? _currentFrameId;
        private string _stoppedReason;

        /// <summary>True for launch mode, false for attach.</summary>
        public bool IsLaunch { get; set; }
        /// <summary>Launched/attached process id, when known.</summary>
        public int? ProcessId { get; set; }
        /// <summary>The launched program (dll/exe) path, when known.</summary>
        public string Program { get; set; }

        public DebugSessionState State { get { lock (_gate) return _state; } }
        public string StoppedReason { get { lock (_gate) return _stoppedReason; } }

        public void SetState(DebugSessionState s) { lock (_gate) _state = s; }

        public void SetStopped(int threadId, string reason)
        {
            lock (_gate)
            {
                _state = DebugSessionState.Stopped;
                _stoppedThreadId = threadId;
                _stoppedReason = reason;
                _currentFrameId = null;
            }
        }

        public void SetRunning()
        {
            lock (_gate)
            {
                _state = DebugSessionState.Running;
                _stoppedThreadId = null;
                _currentFrameId = null;
                _stoppedReason = null;
            }
        }

        public void SetCurrentFrame(int frameId) { lock (_gate) _currentFrameId = frameId; }
        public int? GetStoppedThread() { lock (_gate) return _stoppedThreadId; }
        public int? GetCurrentFrame() { lock (_gate) return _currentFrameId; }

        public void AddBreakpoint(string file, int line)
        {
            lock (_gate)
            {
                if (!_breakpoints.TryGetValue(file, out var lines))
                {
                    lines = new HashSet<int>();
                    _breakpoints[file] = lines;
                }
                lines.Add(line);
            }
        }

        public void RemoveBreakpoint(string file, int line)
        {
            lock (_gate)
            {
                if (_breakpoints.TryGetValue(file, out var lines))
                {
                    lines.Remove(line);
                    if (lines.Count == 0) _breakpoints.Remove(file);
                }
            }
        }

        public int[] GetBreakpoints(string file)
        {
            lock (_gate)
                return _breakpoints.TryGetValue(file, out var lines)
                    ? lines.OrderBy(l => l).ToArray()
                    : Array.Empty<int>();
        }

        public string[] GetBreakpointFiles()
        {
            lock (_gate) return _breakpoints.Keys.ToArray();
        }

        /// <summary>Reset to a fresh, inactive session (clears breakpoints + context).</summary>
        public void Reset()
        {
            lock (_gate)
            {
                _breakpoints.Clear();
                _state = DebugSessionState.Inactive;
                _stoppedThreadId = null;
                _currentFrameId = null;
                _stoppedReason = null;
                ProcessId = null;
                Program = null;
                IsLaunch = false;
            }
        }
    }
}
