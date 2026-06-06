// File: Trace.cs  v1.2
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Schema: JSONL records with keys {ts, run_id, pid, role, level, event, data}.
// Filename: <runId>.<role>.jsonl
// I/O: Synchronous to ensure diagnostic records are flushed before crashes or next ops.
// Role: "mcp" identifies the McpServer side.
//
// This is the C# counterpart to src/util/trace.ts in DevMindShell. Both write to the
// same .dm-trace/ directory using a shared runId (via DEVMIND_TRACE_RUN_ID)
// to allow correlation of traces across the shell and server.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using Newtonsoft.Json;

namespace DevMind
{
    public static class Trace
    {
        private static bool   _initialized;
        private static bool   _shutdownFired;
        private static bool   _enabled;
        private static string _level   = "info";
        private static string _runId   = "";
        private static string _logPath = "";
        private static long   _initTime;
        private static bool   _dirEnsured;
        private static readonly object _initLock  = new object();
        private static readonly object _writeLock = new object();
private const  string  _role = "mcp"; // Distinguishes server side from shell side ("shell")

        // UTF-8 without BOM. Encoding.UTF8 emits a BOM (EF BB BF), which JSONL parsers
        // do not expect and will treat as part of the first record.
        private static readonly Encoding _utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        private static void Init()
        {
            if (_initialized) return;
            lock (_initLock)
            {
                if (_initialized) return;

                _enabled = ParseBool(Environment.GetEnvironmentVariable("DEVMIND_TRACE_ENABLED"));
                _level   = ParseLevel(Environment.GetEnvironmentVariable("DEVMIND_TRACE_LEVEL"));
                _runId   = ResolveRunId();

                string dir = Environment.GetEnvironmentVariable("DEVMIND_TRACE_DIR");
                if (_enabled && !string.IsNullOrWhiteSpace(dir))
                {
                    _logPath = Path.Combine(dir, $"{_runId}.{_role}.jsonl");
                }
                else
                {
                    // No trace dir → effectively disabled regardless of _enabled.
                    _enabled = false;
                }

                _initTime    = Stopwatch.GetTimestamp();
                _initialized = true;

                AppDomain.CurrentDomain.ProcessExit += (s, e) => Shutdown();
            }
        }

        private static string ResolveRunId()
        {
            string inherited = Environment.GetEnvironmentVariable("DEVMIND_TRACE_RUN_ID");
            if (!string.IsNullOrWhiteSpace(inherited)) return inherited;

            // Fresh: 2026-05-07T204638Z-pid12345
            // ISO-8601 UTC, colons stripped, milliseconds dropped.
            string stamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHHmmssZ",
                System.Globalization.CultureInfo.InvariantCulture);
            int pid = Environment.ProcessId;
            return $"{stamp}-pid{pid}";
        }

        private static bool ParseBool(string val)
        {
            if (val == null) return false;
            return string.Equals(val.Trim(), "true", StringComparison.OrdinalIgnoreCase);
        }

        private static string ParseLevel(string val)
        {
            if (val == null) return "info";
            string lower = val.Trim().ToLowerInvariant();
            if (lower == "info" || lower == "debug") return lower;
            return "info";
        }

        public static void Event(
            string level,
            string eventName,
            IDictionary<string, object> data = null)
        {
            Init();
            if (!_enabled) return;
            if (level == "debug" && _level == "info") return;

            var record = new Dictionary<string, object>
            {
                ["ts"]     = DateTime.UtcNow.ToString("o",
                                 System.Globalization.CultureInfo.InvariantCulture),
                ["run_id"] = _runId,
                ["pid"]    = Environment.ProcessId,
                ["role"]   = _role,
                ["level"]  = level,
                ["event"]  = eventName,
                ["data"]   = data ?? new Dictionary<string, object>(),
            };

            WriteRecord(record);
        }

        private static void WriteRecord(IDictionary<string, object> record)
        {
            lock (_writeLock)
            {
                if (!_dirEnsured)
                {
                    string dir = Path.GetDirectoryName(_logPath);
                    if (!string.IsNullOrEmpty(dir))
                        Directory.CreateDirectory(dir);
                    _dirEnsured = true;
                }

                string json = JsonConvert.SerializeObject(record);
File.AppendAllText(_logPath, json + "\n", _utf8NoBom);
            }
        }

        public static string RunId
        {
            get
            {
                Init();
                return _runId;
            }
        }

        public static void Shutdown(int? exitCode = null)
        {
            if (!_initialized) return;

            lock (_initLock)
            {
                if (_shutdownFired) return;
                _shutdownFired = true;
            }

            long elapsedTicks = Stopwatch.GetTimestamp() - _initTime;
            long elapsedMs    = elapsedTicks * 1000L / Stopwatch.Frequency;

            var data = new Dictionary<string, object>
            {
                ["exit_code"]   = exitCode.HasValue ? (object)exitCode.Value : null,
                ["duration_ms"] = elapsedMs,
            };
            Event("info", "mcp.exit", data);
        }
    }
}
