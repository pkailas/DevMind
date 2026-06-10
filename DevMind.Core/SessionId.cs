// File: SessionId.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Single source of truth for the session/run ID and machine name used
// across DevMind (history, trace, training logger).

using System;

namespace DevMind
{
    /// <summary>
    /// Session ID and machine name management. Matches the TS shell's algorithm:
    ///   - DEVMIND_TRACE_RUN_ID env var if set and non-empty
    ///   - Otherwise: YYYY-MM-DDTHHmmssZ-pid{Process.Id}
    /// </summary>
    public static class SessionId
    {
        static string _sessionId;
        static string _machineName;

        /// <summary>Return the session/run ID for this process. Computed once, cached.</summary>
        public static string Get()
        {
            if (_sessionId == null)
            {
                _sessionId = ComputeSessionId();
            }
            return _sessionId;
        }

        /// <summary>Reset the cached session ID so the next call computes a fresh one.</summary>
        public static void Reset()
        {
            _sessionId = null;
        }

        /// <summary>Return the machine hostname. Computed once, cached.</summary>
        public static string GetMachineName()
        {
            if (_machineName == null)
            {
                _machineName = Environment.MachineName;
            }
            return _machineName;
        }

        static string ComputeSessionId()
        {
            string inherited = Environment.GetEnvironmentVariable("DEVMIND_TRACE_RUN_ID");
            if (!string.IsNullOrEmpty(inherited)) return inherited;

            string stamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHHmmssZ").Replace(":", "");
            return $"{stamp}-pid{System.Diagnostics.Process.GetCurrentProcess().Id}";
        }
    }
}
