// File: ActiveJobMarkers.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// One "job is running" marker per job: {TranscriptDir}\_active_<job_id>.json.
//
// The marker is the positive "a delegated job is executing" signal for external tooling
// (deploy.ps1 refuses to stop a server with a live one; dm-watch shows BUSY) and for this
// process's own cleanup (Program.cs skips the patch-backup sweep while another agent is
// mid-job). Transcript silence and CPU load both lie; the marker plus a live pid does not.
//
// H-66: there used to be ONE marker, _active.json, shared by every McpServer process on
// the machine (several run at once, all on the same TranscriptDir). Process A finishing a
// job deleted process B's marker, so B's running job read as "not active elsewhere" — and
// a job that died after its marker was clobbered was never detected. Now each job writes
// and clears only its own file. A legacy _active.json from an older server is still READ
// (treated like any other marker) but never written.
//
// H-03: a marker whose owning process is gone means the server died while the job ran.
// SweepDeadMarkers writes that job a result sidecar (stopped_incomplete / server_restart)
// when it has none, then removes the marker, so devmind_task_status / _result can say what
// happened instead of "unknown job". A marker whose owner is alive is never touched.
//
// Pid reuse: a marker records its process's start time (process_start_utc). A live pid
// whose start time does not match is an unrelated process that inherited the number after
// a crash, and the marker counts as dead. Markers without the field (legacy) fall back to
// the pid check alone.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DevMind.McpServer
{
    /// <summary>A parsed active-job marker.</summary>
    internal sealed class ActiveJobMarker
    {
        public required string Path { get; init; }
        public string? JobId { get; init; }
        public int Pid { get; init; }
        public string? StartedAtUtc { get; init; }
        public string? WorkingDir { get; init; }
        public string? Transcript { get; init; }
        /// <summary>The owning process's start time, when the marker recorded it.</summary>
        public DateTime? ProcessStartUtc { get; init; }
    }

    internal static class ActiveJobMarkers
    {
        /// <summary>The single shared marker older servers wrote. Read, never written.</summary>
        public const string LegacyFileName = "_active.json";

        /// <summary>The incomplete_reasons code for a job whose server died under it.</summary>
        public const string ServerRestartReason = "server_restart";

        // How far a recorded process start time may differ from the live one and still be
        // the same process (the value is written with whole-second precision).
        private static readonly TimeSpan StartTimeTolerance = TimeSpan.FromSeconds(2);

        private static readonly Lazy<DateTime?> OwnStartUtc = new(() =>
        {
            try { return Process.GetCurrentProcess().StartTime.ToUniversalTime(); }
            catch { return null; }
        });

        // LoopDriver's per-iteration line: "[AGENTIC] Iteration 7/40 — 12,345 / 262,144 (4%)".
        // The full shape is required — a looser match could read the model's own prose.
        private static readonly Regex IterationLine = new(
            @"^\[AGENTIC\] Iteration (\d+)(?:/\d+)? — [\d.,   ]+ / [\d.,   ]+ \(\d+%\)\r?$",
            RegexOptions.Compiled | RegexOptions.Multiline | RegexOptions.CultureInvariant);

        public static string PathFor(string jobId) =>
            System.IO.Path.Combine(AgentJobManager.TranscriptDir, $"_active_{jobId}.json");

        /// <summary>Writes this job's marker. Best-effort — never fails the job.</summary>
        public static void Write(AgentJob job, string transcriptPath)
        {
            try
            {
                Directory.CreateDirectory(AgentJobManager.TranscriptDir);
                File.WriteAllText(PathFor(job.Id), JsonSerializer.Serialize(new
                {
                    job_id = job.Id,
                    state = "running",
                    pid = Environment.ProcessId,
                    process_start_utc = OwnStartUtc.Value?.ToString("yyyy-MM-dd HH:mm:ss"),
                    working_dir = job.WorkingDirectory,
                    started_at_utc = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"),
                    transcript = transcriptPath,
                }));
            }
            catch { /* marker is best-effort */ }
        }

        /// <summary>Removes this job's marker — and only this job's.</summary>
        public static void Clear(string jobId)
        {
            try { File.Delete(PathFor(jobId)); } catch { /* best-effort */ }
        }

        /// <summary>Every readable marker in TranscriptDir, the legacy one included.</summary>
        public static List<ActiveJobMarker> ReadAll()
        {
            var markers = new List<ActiveJobMarker>();
            string[] files;
            try { files = Directory.GetFiles(AgentJobManager.TranscriptDir, "_active*.json"); }
            catch { return markers; }

            foreach (string file in files)
            {
                var marker = TryRead(file);
                if (marker != null) markers.Add(marker);
            }
            return markers;
        }

        private static ActiveJobMarker? TryRead(string file)
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                JsonElement root = doc.RootElement;
                string? Str(string name) =>
                    root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() : null;

                int pid = root.TryGetProperty("pid", out var pidEl) && pidEl.TryGetInt32(out int p) ? p : 0;
                DateTime? processStart = DateTime.TryParse(Str("process_start_utc"),
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
                    out DateTime ps) ? ps : null;

                return new ActiveJobMarker
                {
                    Path = file,
                    JobId = Str("job_id"),
                    Pid = pid,
                    StartedAtUtc = Str("started_at_utc"),
                    WorkingDir = Str("working_dir"),
                    Transcript = Str("transcript"),
                    ProcessStartUtc = processStart,
                };
            }
            catch
            {
                return null; // unreadable or malformed: not a marker anyone can act on
            }
        }

        /// <summary>
        /// Whether the process that wrote the marker is still the one running: the pid is
        /// alive and, when the marker recorded it, its start time matches (pid reuse).
        /// </summary>
        public static bool IsOwnerAlive(ActiveJobMarker marker)
        {
            if (marker.Pid <= 0) return false;
            try
            {
                using var owner = Process.GetProcessById(marker.Pid);
                if (owner.HasExited) return false;
                if (marker.ProcessStartUtc is DateTime recorded)
                {
                    DateTime actual = owner.StartTime.ToUniversalTime();
                    if ((actual - recorded).Duration() > StartTimeTolerance) return false;
                }
                return true;
            }
            catch
            {
                return false; // GetProcessById throws when no process holds the pid
            }
        }

        /// <summary>True when any marker names a live process other than this one.</summary>
        public static bool IsJobActiveElsewhere()
            => ReadAll().Any(m => m.Pid != Environment.ProcessId && IsOwnerAlive(m));

        /// <summary>
        /// H-03: for every marker whose owner is dead, write the job a server_restart result
        /// sidecar if it has none, then delete the marker. Live owners' markers — another
        /// process's running job — are never touched. Returns the job ids found dead.
        /// </summary>
        public static IReadOnlyList<string> SweepDeadMarkers()
        {
            var dead = new List<string>();
            foreach (ActiveJobMarker marker in ReadAll())
            {
                if (IsOwnerAlive(marker)) continue;
                if (!string.IsNullOrWhiteSpace(marker.JobId))
                {
                    WriteDiedSidecarIfMissing(marker);
                    dead.Add(marker.JobId!);
                }
                try { File.Delete(marker.Path); } catch { /* best-effort; retried next sweep */ }
            }
            return dead;
        }

        public static string SidecarPath(string jobId) =>
            System.IO.Path.Combine(AgentJobManager.TranscriptDir, $"{jobId}.result.json");

        private static void WriteDiedSidecarIfMissing(ActiveJobMarker marker)
        {
            string path = SidecarPath(marker.JobId!);
            if (File.Exists(path)) return;   // the job finished and wrote its own result
            try
            {
                string json = JsonSerializer.Serialize(new
                {
                    job_id = marker.JobId,
                    state = "stopped_incomplete",
                    incomplete_reasons = new[] { ServerRestartReason },
                    error = $"server process {marker.Pid} exited while the job was running",
                    answer = "",
                    actions = Array.Empty<object>(),
                    iterations = LastIteration(marker.Transcript),
                    iterations_note = "last \"[AGENTIC] Iteration N\" line in the transcript; null when none could be read",
                    elapsed_seconds = (double?)null,
                    started_at_utc = marker.StartedAtUtc,
                    ended_at_utc = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"),
                    ended_at_note = "when the dead server was detected, not when it died",
                    transcript_path = marker.Transcript,
                    working_dir = marker.WorkingDir,
                    can_continue = false,
                });
                File.WriteAllText(path, json);
            }
            catch { /* best-effort; the marker is still removed so the sweep does not loop */ }
        }

        /// <summary>
        /// The last iteration number LoopDriver recorded in a transcript, or null when the
        /// transcript is missing or holds no line of the exact iteration shape.
        /// </summary>
        public static int? LastIteration(string? transcriptPath)
        {
            if (string.IsNullOrWhiteSpace(transcriptPath)) return null;
            try
            {
                if (!File.Exists(transcriptPath)) return null;
                string text;
                using (var stream = new FileStream(transcriptPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new StreamReader(stream))
                    text = reader.ReadToEnd();
                return ParseLastIteration(text);
            }
            catch { return null; }
        }

        public static int? ParseLastIteration(string transcript)
        {
            if (string.IsNullOrEmpty(transcript)) return null;
            MatchCollection matches = IterationLine.Matches(transcript);
            if (matches.Count == 0) return null;
            return int.TryParse(matches[^1].Groups[1].Value, out int n) ? n : null;
        }
    }
}
