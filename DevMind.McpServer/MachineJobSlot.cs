// File: MachineJobSlot.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// H-74: ONE headless job on the model at a time across EVERY DevMind.McpServer process on
// the machine. Claude Desktop runs several servers (one per conversation, plus the Cowork
// pool); each had its own in-process queue, so two conversations ran two jobs at once on
// the single GPU behind DEVMIND_ENDPOINT (Oct 7 2026: MCP calls timed out, and neither
// server's devmind_task_list showed the other's job).
//
// The slot is an exclusive handle on a lock file (DevMindPaths.JobSlotLockPath): opened
// ReadWrite with FileShare.Read, so a second ReadWrite open anywhere on the machine fails
// with a sharing violation — "busy". The holder writes an owner record into the file
// (pid, process start, job id, prompt snippet, working dir, acquired time) that others read
// with FileAccess.Read + FileShare.ReadWrite. Release = dispose the handle. The OS closes
// the handle when the process dies, so a crashed or killed server can never leave the slot
// stuck — unlike a named Semaphore, whose count dies with nobody. A named Mutex is not an
// option either: it is thread-affine, and a job runs across async continuations.
//
// Fairness (cross-process FIFO): polling alone favours the process that just released the
// slot — its next job tries at once while every other waiter is mid-sleep. So the job at
// the head of each process's queue also holds a TICKET while it waits: a file in
// "<lock>.tickets\" named "<queued ticks>-<pid>-<job id>.ticket", opened with
// FileOptions.DeleteOnClose (a dead waiter's ticket vanishes with its handle). Only the
// waiter whose ticket sorts first may take the slot, which gives FIFO by queue time across
// processes. Tickets whose pid is dead are ignored and deleted (belt and braces — a ticket
// left behind by a crash should already be gone).
//
// Failure policy: a lock path that cannot be used at all (permissions, bad path) must not
// park every job forever, so it degrades to "no machine slot" with a stderr line and a
// transcript note; only a sharing / lock violation means "busy".

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DevMind.McpServer
{
    /// <summary>Who holds (or wants) the machine-wide job slot.</summary>
    internal sealed class JobSlotOwner
    {
        public string? JobId { get; init; }
        public int Pid { get; init; }
        public DateTime? ProcessStartUtc { get; init; }
        public string? PromptSnippet { get; init; }
        public string? WorkingDir { get; init; }
        public string? AcquiredUtc { get; init; }
    }

    /// <summary>What a queued job is waiting for: devmind_task_status "waiting_for".</summary>
    internal sealed class JobSlotWait
    {
        /// <summary>"machine_slot_held" (another job is running on the model) or
        /// "earlier_job_waiting" (another process's job queued earlier goes first).</summary>
        public required string Reason { get; init; }
        public string? JobId { get; init; }
        public int Pid { get; init; }
        /// <summary>UTC "yyyy-MM-dd HH:mm:ss": when the holder took the slot, or when the
        /// earlier waiter was queued. Null when the lock file's record could not be read.</summary>
        public string? Since { get; init; }
        /// <summary>Waiting jobs from any process ahead of this one in the machine queue.</summary>
        public int Ahead { get; init; }

        public bool SameAs(JobSlotWait? other) =>
            other != null && other.Reason == Reason && other.JobId == JobId && other.Pid == Pid && other.Ahead == Ahead;

        public object Payload() => new
        {
            reason = Reason,
            job_id = JobId,
            pid = Pid,
            since = Since,
            other_process = Pid != Environment.ProcessId,
            ahead = Ahead,
        };
    }

    internal sealed class MachineJobSlot : IDisposable
    {
        private FileStream? _handle;

        public string LockPath { get; }

        /// <summary>Set when the slot could not be used at all and the job runs without it.</summary>
        public string? DegradedReason { get; }

        private MachineJobSlot(string lockPath, FileStream? handle, string? degradedReason)
        {
            LockPath = lockPath;
            _handle = handle;
            DegradedReason = degradedReason;
        }

        public static string TicketsDir(string lockPath) => lockPath + ".tickets";

        internal static string Stamp(DateTime utc) => utc.ToString("yyyy-MM-dd HH:mm:ss");

        /// <summary>
        /// One attempt: the held slot, or null when another handle holds it (busy). Throws for
        /// anything that is not a sharing/lock violation — the caller degrades on those.
        /// </summary>
        public static MachineJobSlot? TryAcquire(string lockPath, JobSlotOwner owner)
        {
            string? dir = Path.GetDirectoryName(lockPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            FileStream fs;
            try
            {
                fs = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
            }
            catch (IOException ex) when (IsSharingViolation(ex))
            {
                return null;
            }

            try
            {
                byte[] record = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
                {
                    pid = owner.Pid,
                    process_start_utc = owner.ProcessStartUtc is DateTime ps ? Stamp(ps) : null,
                    job_id = owner.JobId,
                    prompt_snippet = owner.PromptSnippet,
                    working_dir = owner.WorkingDir,
                    acquired_utc = owner.AcquiredUtc ?? Stamp(DateTime.UtcNow),
                }));
                fs.SetLength(0);
                fs.Write(record, 0, record.Length);
                fs.Flush(flushToDisk: true);
            }
            catch { /* the handle is the lock; the record is best-effort */ }

            return new MachineJobSlot(lockPath, fs, null);
        }

        /// <summary>ERROR_SHARING_VIOLATION (32) or ERROR_LOCK_VIOLATION (33).</summary>
        private static bool IsSharingViolation(IOException ex) => (ex.HResult & 0xFFFF) is 32 or 33;

        /// <summary>
        /// The holder's record, or null when the file is missing, empty (released cleanly) or
        /// unreadable. A stale record from a crashed holder is still returned — only a failed
        /// <see cref="TryAcquire"/> says the slot is actually held.
        /// </summary>
        public static JobSlotOwner? ReadOwner(string lockPath)
        {
            try
            {
                string text;
                using (var fs = new FileStream(lockPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new StreamReader(fs, Encoding.UTF8))
                    text = reader.ReadToEnd();
                if (string.IsNullOrWhiteSpace(text)) return null;

                using var doc = JsonDocument.Parse(text);
                JsonElement root = doc.RootElement;
                string? Str(string name) =>
                    root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() : null;
                return new JobSlotOwner
                {
                    JobId = Str("job_id"),
                    Pid = root.TryGetProperty("pid", out var p) && p.TryGetInt32(out int pid) ? pid : 0,
                    ProcessStartUtc = JobProcess.ParseUtc(Str("process_start_utc")),
                    PromptSnippet = Str("prompt_snippet"),
                    WorkingDir = Str("working_dir"),
                    AcquiredUtc = Str("acquired_utc"),
                };
            }
            catch
            {
                return null; // missing, mid-write, or malformed
            }
        }

        /// <summary>
        /// Waits for the machine slot: takes a ticket, then polls every <paramref name="poll"/>
        /// until this job's ticket is first and the lock opens. <paramref name="onWait"/> gets
        /// what the job is waiting for on every poll that does not acquire, and null once it
        /// has. Cancellation ends the wait with OperationCanceledException, slot not taken.
        /// </summary>
        public static async Task<MachineJobSlot> AcquireAsync(
            string lockPath, JobSlotOwner owner, DateTime queuedUtc, TimeSpan poll,
            Action<JobSlotWait?> onWait, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Ticket? ticket = null;
            try
            {
                try { ticket = Ticket.Take(lockPath, owner.JobId ?? "job", queuedUtc); }
                catch (Exception ex)
                {
                    // No ticket = no cross-process ordering, but still mutual exclusion.
                    Console.Error.WriteLine($"[MachineJobSlot] ticket unavailable ({ex.Message}) - waiting without FIFO order");
                }

                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    List<TicketInfo> ahead = ticket == null ? new List<TicketInfo>() : ticket.Ahead();
                    if (ahead.Count == 0)
                    {
                        MachineJobSlot? slot;
                        try { slot = TryAcquire(lockPath, owner); }
                        catch (Exception ex)
                        {
                            string reason = $"machine job slot unusable at {lockPath} ({ex.Message}) - running without it";
                            Console.Error.WriteLine($"[MachineJobSlot] {reason}");
                            onWait(null);
                            return new MachineJobSlot(lockPath, null, reason);
                        }
                        if (slot != null)
                        {
                            onWait(null);
                            return slot;
                        }
                    }

                    onWait(DescribeWait(lockPath, ahead));
                    await Task.Delay(poll, ct).ConfigureAwait(false);
                }
            }
            finally
            {
                ticket?.Dispose();
            }
        }

        private static JobSlotWait DescribeWait(string lockPath, List<TicketInfo> ahead)
        {
            JobSlotOwner? holder = ReadOwner(lockPath);
            // The slot holder is the better answer when there is one: it is what is actually
            // on the model. A record whose pid is dead is stale (the slot is between jobs).
            if (holder != null && JobProcess.IsAlive(holder.Pid, holder.ProcessStartUtc))
                return new JobSlotWait
                {
                    Reason = "machine_slot_held",
                    JobId = holder.JobId,
                    Pid = holder.Pid,
                    Since = holder.AcquiredUtc,
                    Ahead = ahead.Count,
                };
            if (ahead.Count > 0)
                return new JobSlotWait
                {
                    Reason = "earlier_job_waiting",
                    JobId = ahead[0].JobId,
                    Pid = ahead[0].Pid,
                    Since = Stamp(ahead[0].QueuedUtc),
                    Ahead = ahead.Count,
                };
            // Busy, but the record is unreadable (mid-write) — still say what we know.
            return new JobSlotWait { Reason = "machine_slot_held", JobId = holder?.JobId, Pid = holder?.Pid ?? 0, Since = holder?.AcquiredUtc };
        }

        /// <summary>Releases the slot: clears the owner record (an empty file reads as free),
        /// then closes the handle. Idempotent; never throws.</summary>
        public void Dispose()
        {
            FileStream? fs = Interlocked.Exchange(ref _handle, null);
            if (fs == null) return;
            try { fs.SetLength(0); fs.Flush(); } catch { /* the close below is what releases */ }
            try { fs.Dispose(); } catch { }
        }

        // ── Tickets ─────────────────────────────────────────────────────────────

        internal readonly record struct TicketInfo(string Name, string Path, DateTime QueuedUtc, int Pid, string JobId);

        /// <summary>A waiter's place in the machine queue; deleted when its handle closes.</summary>
        internal sealed class Ticket : IDisposable
        {
            private readonly FileStream _handle;
            private readonly string _dir;
            public string Name { get; }

            private Ticket(FileStream handle, string dir, string name)
            {
                _handle = handle;
                _dir = dir;
                Name = name;
            }

            public static Ticket Take(string lockPath, string jobId, DateTime queuedUtc)
            {
                string dir = TicketsDir(lockPath);
                Directory.CreateDirectory(dir);
                // Fixed-width ticks and pid so ordinal order is queue order; the job id breaks ties.
                string name = $"{queuedUtc.ToUniversalTime().Ticks:D19}-{Environment.ProcessId:D10}-{jobId}.ticket";
                var fs = new FileStream(Path.Combine(dir, name), FileMode.Create, FileAccess.ReadWrite,
                    FileShare.Read | FileShare.Delete, 1, FileOptions.DeleteOnClose);
                return new Ticket(fs, dir, name);
            }

            /// <summary>Live tickets that sort before this one, oldest first.</summary>
            public List<TicketInfo> Ahead()
            {
                var ahead = new List<TicketInfo>();
                foreach (TicketInfo t in ReadAll(_dir))
                {
                    if (string.CompareOrdinal(t.Name, Name) >= 0) continue;
                    if (!JobProcess.IsAlive(t.Pid, null))
                    {
                        try { File.Delete(t.Path); } catch { /* still open somewhere, or gone */ }
                        continue;
                    }
                    ahead.Add(t);
                }
                ahead.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
                return ahead;
            }

            public void Dispose()
            {
                try { _handle.Dispose(); } catch { }
            }
        }

        internal static List<TicketInfo> ReadAll(string ticketsDir)
        {
            var list = new List<TicketInfo>();
            string[] files;
            try { files = Directory.GetFiles(ticketsDir, "*.ticket"); }
            catch { return list; }
            foreach (string file in files)
            {
                string name = Path.GetFileName(file);
                string stem = Path.GetFileNameWithoutExtension(name);
                string[] parts = stem.Split('-', 3);
                if (parts.Length != 3
                    || !long.TryParse(parts[0], out long ticks) || ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks
                    || !int.TryParse(parts[1], out int pid))
                    continue;
                list.Add(new TicketInfo(name, file, new DateTime(ticks, DateTimeKind.Utc), pid, parts[2]));
            }
            return list;
        }
    }

    /// <summary>Process identity checks shared by the job slot, the state files and the
    /// active-job markers: a pid is "the same process" only while it is alive and, when a
    /// start time was recorded, its start time still matches (pid reuse).</summary>
    internal static class JobProcess
    {
        private static readonly TimeSpan StartTimeTolerance = TimeSpan.FromSeconds(2);

        private static readonly Lazy<DateTime?> OwnStart = new(() =>
        {
            try { return Process.GetCurrentProcess().StartTime.ToUniversalTime(); }
            catch { return null; }
        });

        public static DateTime? OwnStartUtc => OwnStart.Value;

        public static bool IsAlive(int pid, DateTime? recordedStartUtc)
        {
            if (pid <= 0) return false;
            try
            {
                using var process = Process.GetProcessById(pid);
                if (process.HasExited) return false;
                if (recordedStartUtc is DateTime recorded)
                {
                    DateTime actual = process.StartTime.ToUniversalTime();
                    if ((actual - recorded).Duration() > StartTimeTolerance) return false;
                }
                return true;
            }
            catch
            {
                return false; // GetProcessById throws when no process holds the pid
            }
        }

        public static DateTime? ParseUtc(string? text) =>
            DateTime.TryParse(text, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
                out DateTime value) ? value : null;
    }
}
