// File: WindowsJobObject.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Per-command Windows Job Object containment for ShellRunner.RunProcessAsync.
//
// WHY: taskkill /F /T walks the CURRENT parent/child tree. A grandchild that
// orphaned or re-parented (an MSBuild node-reuse worker) is no longer in that
// tree, so the reap misses it and the survivor keeps growing (64 GB observed
// in the field). Job membership, by contrast, is inherited at process creation
// and is LINEAGE-based, not parent-based — an orphaned descendant of an in-job
// process stays in the job. With JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE, closing
// the job handle tears down the entire contained set in one OS operation,
// including exactly the escapees taskkill cannot reach.
//
// INVARIANTS (load-bearing — see ShellRunner.RunProcessAsync call site):
//   * TryCreateAndAssignJob is INCAPABLE of throwing. Every interop call is
//     guarded; any failure (non-Windows, API error, process already gone,
//     nested-job access-denied) returns (null, reason) and the command runs
//     exactly as it did before this feature: taskkill reap +
//     ClassifyReapResult + still-alive message. When in doubt, degrade.
//   * KILL_ON_JOB_CLOSE is armed BEFORE the child starts, so a child that
//     starts inside the job can never be a survivor even if we fail to
//     assign it explicitly.
//   * The returned handle must stay open for the lifetime of the command and
//     be closed (ReleaseJob) only after the existing wait/reap sequence has
//     run — closing it is the authoritative kill for anything still alive.

using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using DmTrace = DevMind.Trace;

namespace DevMind
{
    /// <summary>
    /// Creates a per-command Windows Job Object with KILL_ON_JOB_CLOSE and
    /// assigns a started process to it. Internal: the only consumer is
    /// <see cref="ShellRunner.RunProcessAsync"/>. Windows-only; every other
    /// platform is a no-op by construction.
    /// </summary>
    internal static class WindowsJobObject
    {
        /// <summary>
        /// JOBOBJECT_BASIC_LIMIT_INFORMATION — the first member of the extended
        /// struct below. Layout verified byte-for-byte against the Win32 SDK
        /// (64 bytes on x64: LimitFlags at offset 16, SchedulingClass at 60).
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct JobObjectBasicLimitInfo
        {
            public IntPtr PerProcessUserTimeLimit;
            public IntPtr PerJobUserTimeLimit;
            public uint   LimitFlags;
            public IntPtr MinimumWorkingSetSize;
            public IntPtr MaximumWorkingSetSize;
            public uint   ActiveProcessLimit;
            public ulong  Affinity;
            public uint   PriorityClass;
            public uint   SchedulingClass;
        }

        /// <summary>IO_COUNTERS — 6 x UInt64 (the second member of the extended struct).</summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct IoCounters
        {
            public nuint ReadOperationCount;
            public nuint WriteOperationCount;
            public nuint OtherOperationCount;
            public nuint ReadTransferCount;
            public nuint WriteTransferCount;
            public nuint OtherTransferCount;
        }

        /// <summary>
        /// JOBOBJECT_EXTENDED_LIMIT_INFORMATION (information class 9) — the ONLY
        /// layout through which JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE may be set.
        /// The basic struct (class 2) rejects KILL_ON_JOB_CLOSE with
        /// ERROR_INVALID_PARAMETER (Win32 87), which is exactly why the naive
        /// "basic struct + class 2" approach silently armed nothing. Do NOT
        /// hardcode the size: Marshal.SizeOf keeps x64/x86 correct.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct JobObjectExtendedLimitInfo
        {
            public JobObjectBasicLimitInfo BasicLimitInformation;
            public IoCounters              IoInfo;
            public nuint ProcessMemoryLimit;
            public nuint JobMemoryLimit;
            public nuint PeakProcessMemoryUsed;
            public nuint PeakJobMemoryUsed;
        }

        private const uint JobObjectExtendedLimitInformation = 9;
        private const uint JobObjectLimitKillOnJobClose      = 0x2000;
        private const uint JobObjectLimitSilentBreakawayOk   = 0x1000;

        // Classic DllImport P/Invoke (not LibraryImport source generation): the latter
        // hard-requires <AllowUnsafeBlocks> in the csproj (SYSLIB1062), which would add the
        // unsafe flag project-wide. DllImport needs no unsafe and no csproj change, so the
        // whole feature stays confined to this one file.
        [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "CreateJobObjectW", CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string lpName);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetInformationJobObject(
            IntPtr hJob,
            uint jobObjectInformationClass,
            IntPtr lpJobObjectInformation,
            uint jobObjectInformationLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

        // Modern 3-argument form (Win XP+): (ProcessHandle, JobHandle, out Member).
        // JobHandle NULL = "is it in ANY job". Return value = query succeeded;
        // membership arrives via the out param. The legacy 2-arg form reads the
        // success bool as membership and passes a garbage 3rd register — never use it.
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool IsProcessInJob(IntPtr hProcess, IntPtr hJob, out bool bMember);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        /// <summary>
        /// Create a kill-on-close job, assign <paramref name="proc"/> to it,
        /// and return the job handle. NEVER throws — any failure (including
        /// any Win32 exception from the interop calls) returns a null handle
        /// plus a human-readable <paramref name="reason"/>, and the caller
        /// falls back to the pre-job behavior.
        /// <para>
        /// <paramref name="allowChildBreakaway"/> (run_shell detach=true) also arms
        /// JOB_OBJECT_LIMIT_SILENT_BREAKAWAY_OK: <paramref name="proc"/> itself stays in the
        /// job — its timeout/cancel kill is unchanged — but every process it creates from
        /// then on is left out of the job, so closing the job does not terminate them. If
        /// the host sits in a parent job that forbids breakaway, the children stay in that
        /// parent job (they outlive the call, not the host).
        /// </para>
        /// </summary>
        public static (IntPtr jobHandle, string reason) TryCreateAndAssignJob(Process proc, bool allowChildBreakaway = false)
        {
            IntPtr job = IntPtr.Zero;
            try
            {
                if (!OperatingSystem.IsWindows())
                    return (IntPtr.Zero, "not_windows");
                if (proc == null)
                    return (IntPtr.Zero, "null_process");

                job = CreateJobObject(IntPtr.Zero, null);
                if (job == IntPtr.Zero)
                    return (IntPtr.Zero, $"CreateJobObject failed (Win32 {Marshal.GetLastWin32Error()})");

                // Arm KILL_ON_JOB_CLOSE before the child starts (the caller assigns
                // immediately after proc.Start(), so from here on the child is in a
                // job whose close kills the whole contained tree).
                //
                // KILL_ON_JOB_CLOSE can ONLY be set via the EXTENDED struct
                // (class 9); setting it through the basic struct (class 2) fails
                // with ERROR_INVALID_PARAMETER (Win32 87) and arms nothing. All
                // fields except LimitFlags stay zero (no memory/time caps).
                var limit = new JobObjectExtendedLimitInfo();
                limit.BasicLimitInformation.LimitFlags = JobObjectLimitKillOnJobClose
                    | (allowChildBreakaway ? JobObjectLimitSilentBreakawayOk : 0);
                IntPtr infoPtr = Marshal.AllocHGlobal(Marshal.SizeOf<JobObjectExtendedLimitInfo>());
                bool setOk = false;
                try
                {
                    Marshal.StructureToPtr(limit, infoPtr, fDeleteOld: false);
                    setOk = SetInformationJobObject(job, JobObjectExtendedLimitInformation,
                        infoPtr, (uint)Marshal.SizeOf<JobObjectExtendedLimitInfo>());
                }
                finally
                {
                    Marshal.FreeHGlobal(infoPtr);
                }
                if (!setOk)
                {
                    CloseHandle(job);
                    job = IntPtr.Zero;
                    return (IntPtr.Zero, $"SetInformationJobObject failed (Win32 {Marshal.GetLastWin32Error()})");
                }

                IntPtr processHandle;
                try { processHandle = proc.Handle; }
                catch (Exception ex)
                {
                    CloseHandle(job);
                    job = IntPtr.Zero;
                    return (IntPtr.Zero, $"process handle unavailable: {ex.Message}");
                }

                if (!AssignProcessToJobObject(job, processHandle))
                {
                    int err = Marshal.GetLastWin32Error();
                    CloseHandle(job);
                    job = IntPtr.Zero;
                    // 5 = ERROR_ACCESS_DENIED: DevMind itself is a member of a parent
                    // job without silent-breakaway (CI runners, job-contained hosts).
                    // Expected and benign — the command runs with the legacy reap.
                    return (IntPtr.Zero, $"AssignProcessToJobObject failed (Win32 {err})");
                }

                // Post-assignment self-check (observability, not a gate): confirm
                // the OS accepted the assignment from INSIDE the assign call, on the
                // very same process handle, at the very moment of assignment.
                // AssignProcessToJobObject returning true is authoritative, but a
                // cross-thread / cross-instant re-query (e.g. the regression test
                // querying the marker PID a few milliseconds later) can in principle
                // disagree; this line makes any such disagreement diagnosable from
                // the trace rather than silent. Never gates — on a false reading we
                // still return the live handle and let the test's live query decide.
                bool justAssigned;
                try
                {
                    // 3-arg form: (process, job, out member). Pass the SPECIFIC job
                    // handle (not NULL) so this is the precise "is the child in THIS
                    // job" check — the exact inverse of the AssignProcessToJobObject
                    // call we just made. NULL ("any job") would be a confounded
                    // false-positive on a host that is itself in a parent job, because
                    // job membership is lineage-inherited: the child inherits the
                    // host's parent job regardless of whether our per-command
                    // assignment succeeded.
                    bool member;
                    bool queryOk = IsProcessInJob(processHandle, job, out member);
                    justAssigned = queryOk && member;
                }
                catch
                {
                    justAssigned = false;
                }

                DmTrace.Event("info", "mcp.shell.job", new System.Collections.Generic.Dictionary<string, object>
                {
                    ["pid"]                   = proc.Id,
                    ["assigned_immediately"]  = justAssigned,
                    ["child_breakaway"]       = allowChildBreakaway
                });
                return (job, null);
            }
            catch (Exception ex)
            {
                // Unreachable by the guards above; last line of defense so this
                // method can NEVER propagate into RunProcessAsync.
                if (job != IntPtr.Zero)
                {
                    try { CloseHandle(job); } catch { }
                }
                return (IntPtr.Zero, $"unexpected exception: {ex.Message}");
            }
        }

        /// <summary>
        /// Close the job handle. With KILL_ON_JOB_CLOSE armed, this tears down
        /// the entire contained process tree — the authoritative kill for any
        /// child that outlived its command. Never throws.
        /// </summary>
        public static void ReleaseJob(IntPtr jobHandle)
        {
            if (jobHandle == IntPtr.Zero)
                return;
            try { CloseHandle(jobHandle); }
            catch { /* closing a job must never break the command's teardown */ }
        }
    }
}
