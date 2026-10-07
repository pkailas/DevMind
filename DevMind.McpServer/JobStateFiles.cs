// File: JobStateFiles.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// H-74: every job's state, visible to every server process. Live job state is in-memory
// per McpServer process, and Claude Desktop runs several — on Oct 7 2026 one conversation's
// devmind_task_list never showed job-2210, running from another, although its transcript
// was in the shared folder. Each job now writes {TranscriptDir}\<job_id>.state.json
// (job id, owning pid + process start, display state, queued/started/ended, prompt
// snippet, working dir, transcript, waiting_for) on every state change. devmind_task_list
// merges in other processes' jobs from these files; devmind_task_status / _result answer
// for another process's job id from them instead of "Unknown job_id".
//
// Writes are atomic (temp file + move) and best-effort: a state file must never fail a job.
// The files are not deleted — like result sidecars they are small and live with the
// transcripts. Readers decide relevance (live owner, or recently orphaned).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace DevMind.McpServer
{
    /// <summary>A parsed state file.</summary>
    internal sealed class JobStateRecord
    {
        public required string JobId { get; init; }
        public int Pid { get; init; }
        public DateTime? ProcessStartUtc { get; init; }
        public string State { get; init; } = "unknown";
        public string? QueuedUtc { get; init; }
        public string? StartedUtc { get; init; }
        public string? EndedUtc { get; init; }
        public string? PromptSnippet { get; init; }
        public string? WorkingDir { get; init; }
        public string? TranscriptPath { get; init; }
        public string? ParentJobId { get; init; }
        public string? Error { get; init; }
        /// <summary>The waiting_for object as written (null when not waiting).</summary>
        public JsonElement? WaitingFor { get; init; }
        public DateTime FileWrittenUtc { get; init; }

        public bool IsTerminal => State is not ("queued" or "running");

        public bool OwnerAlive => JobProcess.IsAlive(Pid, ProcessStartUtc);

        public bool IsOtherProcess => Pid != Environment.ProcessId;

        /// <summary>The state to report: "orphaned" for a queued/running job whose server is gone.</summary>
        public string EffectiveState => !IsTerminal && !OwnerAlive ? "orphaned" : State;
    }

    internal static class JobStateFiles
    {
        public const string Suffix = ".state.json";

        /// <summary>How long an orphaned job stays in other processes' devmind_task_list.</summary>
        public static readonly TimeSpan OrphanListWindow = TimeSpan.FromHours(24);

        public static string PathFor(string jobId) =>
            Path.Combine(AgentJobManager.TranscriptDir, jobId + Suffix);

        /// <summary>Writes the job's current state. Best-effort — never throws.</summary>
        public static void Write(AgentJob job)
        {
            try
            {
                Directory.CreateDirectory(AgentJobManager.TranscriptDir);
                string json = JsonSerializer.Serialize(new
                {
                    job_id = job.Id,
                    pid = Environment.ProcessId,
                    process_start_utc = JobProcess.OwnStartUtc is DateTime ps ? MachineJobSlot.Stamp(ps) : null,
                    state = job.DisplayState,
                    queued_utc = MachineJobSlot.Stamp(job.QueuedAtUtc),
                    started_utc = job.StartedAtUtc is DateTime s ? MachineJobSlot.Stamp(s) : null,
                    ended_utc = job.EndedAtUtc is DateTime e ? MachineJobSlot.Stamp(e) : null,
                    prompt_snippet = AgentJob.Snippet(job.Prompt, 120),
                    working_dir = job.WorkingDirectory,
                    transcript_path = job.TranscriptPath,
                    parent_job_id = job.ParentJobId,
                    error = job.Error,
                    waiting_for = job.WaitingFor?.Payload(),
                });
                string path = PathFor(job.Id);
                string tmp = $"{path}.{Guid.NewGuid():N}.tmp";
                File.WriteAllText(tmp, json);
                try { File.Move(tmp, path, overwrite: true); }
                catch { try { File.Delete(tmp); } catch { } throw; }
            }
            catch { /* best-effort */ }
        }

        public static JobStateRecord? Read(string jobId)
        {
            if (string.IsNullOrWhiteSpace(jobId) || jobId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                return null;
            return TryRead(PathFor(jobId));
        }

        /// <summary>Every readable state file in TranscriptDir.</summary>
        public static List<JobStateRecord> ReadAll()
        {
            string[] files;
            try { files = Directory.GetFiles(AgentJobManager.TranscriptDir, "*" + Suffix); }
            catch { return new List<JobStateRecord>(); }
            return files.Select(TryRead).Where(r => r != null).Select(r => r!).ToList();
        }

        /// <summary>
        /// The jobs another server process owns that devmind_task_list should show: queued or
        /// running under a live owner, finished under a live owner (that server still holds it
        /// in memory), or orphaned — queued/running under a dead owner — within the last
        /// <see cref="OrphanListWindow"/>. Newest queued first.
        /// </summary>
        public static List<JobStateRecord> OtherProcessJobs(ICollection<string> ownJobIds, int max = 30)
        {
            DateTime cutoff = DateTime.UtcNow - OrphanListWindow;
            return ReadAll()
                .Where(r => r.IsOtherProcess && !ownJobIds.Contains(r.JobId))
                .Where(r => r.OwnerAlive || (!r.IsTerminal && r.FileWrittenUtc >= cutoff))
                .OrderByDescending(r => r.QueuedUtc, StringComparer.Ordinal)
                .Take(max)
                .ToList();
        }

        private static JobStateRecord? TryRead(string path)
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    string text;
                    using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    using (var reader = new StreamReader(fs))
                        text = reader.ReadToEnd();
                    using var doc = JsonDocument.Parse(text);
                    JsonElement root = doc.RootElement;
                    string? Str(string name) =>
                        root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() : null;
                    string? id = Str("job_id");
                    if (string.IsNullOrWhiteSpace(id)) return null;
                    return new JobStateRecord
                    {
                        JobId = id,
                        Pid = root.TryGetProperty("pid", out var p) && p.TryGetInt32(out int pid) ? pid : 0,
                        ProcessStartUtc = JobProcess.ParseUtc(Str("process_start_utc")),
                        State = Str("state") ?? "unknown",
                        QueuedUtc = Str("queued_utc"),
                        StartedUtc = Str("started_utc"),
                        EndedUtc = Str("ended_utc"),
                        PromptSnippet = Str("prompt_snippet"),
                        WorkingDir = Str("working_dir"),
                        TranscriptPath = Str("transcript_path"),
                        ParentJobId = Str("parent_job_id"),
                        Error = Str("error"),
                        WaitingFor = root.TryGetProperty("waiting_for", out var w) && w.ValueKind == JsonValueKind.Object
                            ? w.Clone() : null,
                        FileWrittenUtc = File.GetLastWriteTimeUtc(path),
                    };
                }
                catch (FileNotFoundException) { return null; }
                catch (DirectoryNotFoundException) { return null; }
                catch (IOException) { System.Threading.Thread.Sleep(10); }   // being replaced — retry
                catch { return null; }                                      // malformed
            }
            return null;
        }
    }
}
