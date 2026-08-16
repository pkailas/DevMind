// File: ShellRunnerJobObjectTests.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Regression tests for per-command Windows Job Object containment
// (ShellRunner.RunProcessAsync + WindowsJobObject.cs, v1.11):
//
//   TEST 1 (the observable-effect test): a command spawned through ExecuteAsync
//     is EITHER a member of a job object (IsProcessInJob on the live child) OR
//     the run traces mcp.shell.job.degraded with a reason AND still completes
//     normally. The OR is not a hole — it mirrors the designed, approved
//     attempt-and-fallback semantics: when the DevMind process is itself a
//     member of a no-breakaway parent job (observed on this machine: the test
//     host runs inside one, so AssignProcessToJobObject fails with Win32 5),
//     containment degrades and the command runs with the pre-job behavior.
//     On a host NOT in a job the first branch is asserted. Both outcomes are
//     verified against the trace (mcp.shell.job vs mcp.shell.job.degraded) so
//     the outcome is evidence, not assumption. Fails without the change: no
//     job event and no degraded event can appear, and the child is in no job.
//
//   TEST 2: TryCreateAndAssignJob on a process that has already exited must
//     return a null handle and a non-empty reason, and must NOT throw — the
//     degradable contract.
//   TEST 3: TryCreateAndAssignJob(null) must return a null handle and a
//     non-empty reason, and must NOT throw.
//   TEST 4: guard-path no-op — on a non-Windows host the IsWindows() guard
//     short-circuits before any interop, so every call returns a null handle;
//     on Windows the degenerate inputs still never throw and never create a
//     job (same return path).
//
// Deliberately NOT tested (safe-to-skip, same standard as CHANGE 3 in
// ShellRunnerReapTests):
//   * The authoritative-kill-on-job-close path for a detached grandchild that
//     outlives taskkill /F /T. Inducing that requires spawning a double-forked
//     / detached daemon — exactly the runaway this feature exists to contain.
//     The ReapResult classification logic it relies on is already covered in
//     ShellRunnerReapTests.
//   * The nested-job access-denied trigger (Win32 5) cannot be induced
//     artificially: it requires the host process to already be a member of a
//     no-breakaway parent job, and wrapping the whole test assembly in such a
//     job would change the runtime environment for every other test. On this
//     machine TEST 1 exercises it end-to-end for free (host is in a job), and
//     the same null-handle contract it relies on is unit-covered by TESTS 2/3.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace DevMind.Core.Tests
{
    // [Collection] serializes this class's own tests against each other. That is NOT
    // what hermetizes the trace: DevMind.Trace is a process-global static that writes
    // every thread's events to whatever run id is current at the instant of the call,
    // so OTHER test classes running in parallel can interleave their records (web_search,
    // web_fetch, their own shell) into this test's run-id file. A [Collection] only
    // serializes tests WITHIN it, so it cannot shield this test from the other classes.
    // The real hermeticity is the PID filter on the positive-branch assertion below
    // (this child's mcp.shell.job {pid} cannot be a foreign record) plus the live
    // IsProcessInJob oracle on the marker-confirmed child PID (unique dir, uncontaminated).
    [Collection("ShellRunnerJobObject")]
    public class ShellRunnerJobObjectTests : IDisposable
    {
        private readonly string _dir;

        public ShellRunnerJobObjectTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), $"devmind_shrj_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch { }
        }

        // --- Test 1: observable effect — containment is either active or degraded-with-evidence ---

        [Fact]
        public async Task ExecuteAsync_EitherAssignsJobOrTracesDegradation_AndCommandAlwaysRuns()
        {
            if (!OperatingSystem.IsWindows())
                return; // containment is a Windows feature; no-op elsewhere by design

            // Enable trace capture for the duration of this command so the
            // outcome (job created vs degraded) is evidence in the JSONL file,
            // not an assumption. Save/restore so no other test sees this.
            // Trace.Init() is idempotent (first call wins), so also force a
            // re-init via its private flag AFTER setting the env vars.
            string traceDir = Path.Combine(_dir, "trace");
            Directory.CreateDirectory(traceDir);
            string[] envKeys = { "DEVMIND_TRACE_ENABLED", "DEVMIND_TRACE_LEVEL", "DEVMIND_TRACE_DIR", "DEVMIND_TRACE_RUN_ID" };
            string[] saved = {
                Environment.GetEnvironmentVariable(envKeys[0]),
                Environment.GetEnvironmentVariable(envKeys[1]),
                Environment.GetEnvironmentVariable(envKeys[2]),
                Environment.GetEnvironmentVariable(envKeys[3]),
            };
            string traceRunId = $"jobtest-{Guid.NewGuid():N}";
            string traceFile = Path.Combine(traceDir, $"{traceRunId}.mcp.jsonl");
            try
            {
                Environment.SetEnvironmentVariable(envKeys[0], "true");
                Environment.SetEnvironmentVariable(envKeys[1], "debug");
                Environment.SetEnvironmentVariable(envKeys[2], traceDir);
                Environment.SetEnvironmentVariable(envKeys[3], traceRunId);
                ResetTraceInitialized();

                // The child writes its own PID to a marker file, then sleeps.
                // The sleep is long enough that the child is still running
                // (and queryable via IsProcessInJob) when we check it, but
                // short enough that the 5-second timeout reaps it before the
                // test ends — nothing lingers past the test.
                //
                // Two PowerShell traps are worked around here, both of which
                // bit earlier drafts of this test:
                //  1. $PID, not %PID% — on this host ExecuteAsync wraps
                //     commands in powershell -Command, and '%' is the
                //     PowerShell modulo operator, so cmd-style variables
                //     never reach cmd and the command dies at parse time.
                //  2. The marker is a FILE, not stdout: a powershell
                //     -Command pipeline buffers its output until the WHOLE
                //     command finishes, so `echo ...; Start-Sleep 12` would
                //     not emit the marker while the child is alive.
                string markerPath = Path.Combine(_dir, $"child_{Guid.NewGuid():N}.pid");
                string command = $"[IO.File]::WriteAllText('{markerPath}', $PID); Start-Sleep 12";

                var runner = new ShellRunner(_dir);
                var executeTask = runner.ExecuteAsync(command, timeoutSeconds: 5);

                // Wait (bounded) for the marker file carrying the child's PID.
                // It is written AFTER the job assignment inside RunProcessAsync,
                // so by the time we observe it the child is definitively either
                // in the job or not.
                int childPid = 0;
                try
                {
                    var stopwatch = Stopwatch.StartNew();
                    while (stopwatch.ElapsedMilliseconds < 4000 && childPid == 0)
                    {
                        if (File.Exists(markerPath))
                        {
                            if (int.TryParse(File.ReadAllText(markerPath).Trim(), out var pid))
                                childPid = pid;
                        }
                        if (childPid == 0)
                            await Task.Delay(50);
                    }
                }
                finally
                {
                    try { File.Delete(markerPath); } catch { }
                }

                Assert.True(childPid > 0, "marker file with child PID was not observed within 4s — " +
                    "cannot verify the job outcome without a live child");

                // Membership is queried HERE — after the marker appears but
                // BEFORE the await below, while the child is demonstrably
                // alive. After the 5s timeout the await reaps the tree
                // (job kill-on-close and/or taskkill), so a post-await query
                // would be asking about a corpse and the answer would be
                // meaningless. The helper itself is also three-state (see
                // JobMembershipState) so a query failure can never silently
                // collapse into "not in a job" — it fails the test as
                 // inconclusive instead.
                JobMembershipResult childMembership = QueryJobMembership(childPid);

                // Let the timeout path complete so the child is reaped (job
                // kill-on-close and/or taskkill) and nothing survives the test.
                var (output, exitCode) = await executeTask;

                // NON-NEGOTIABLE invariant, asserted first: the command must
                // have run to its (timed-out) completion no matter what the
                // job layer did or failed to do.
                Assert.Equal(-1, exitCode);
                Assert.Contains("timed out", output, StringComparison.OrdinalIgnoreCase);

                // READ THE RAW TRACE FILE FOR THIS RUN ID. The run id names the file,
                // but DevMind.Trace is a process-global static that writes whichever
                // thread's events land while THIS run id is current, so under parallel
                // xUnit execution foreign records from other test classes can appear in
                // this file too. Do NOT rely on the run id for isolation: the positive
                // branch below filters the mcp.shell.job record by THIS child's PID,
                // and the degraded branch is corroborated by the live host membership
                // query (not by the trace alone).
                string trace = File.Exists(traceFile) ? File.ReadAllText(traceFile) : "";
                // The host (this test process) is alive for the whole test, so
                // querying it post-await is safe. It is the BRANCH SELECTOR:
                // is the host itself a member of a parent job? The query must
                // be three-state too — a CouldNotQuery here must fail loudly
                // rather than silently steering us into one of the two outcome
                // branches.
                JobMembershipResult hostMembership = QueryJobMembership(Environment.ProcessId);
                Assert.True(hostMembership.State != JobMembershipState.CouldNotQuery,
                    $"could not determine the host process's (PID {Environment.ProcessId}) job membership — " +
                    $"the degraded-vs-positive branch selection is inconclusive. detail=[{hostMembership.Detail}]");
                string diagTail = trace.Length > 1500 ? trace.Substring(trace.Length - 1500) : trace;

                // BRANCH ON THE TRACE OUTCOME FOR THIS CHILD, not on the host's own
                // job membership. Two reasons the old host-membership branch selector
                // was wrong:
                //  (1) The host being in a job does NOT determine the outcome — it
                //      depends on whether that job allows silent breakaway / nesting.
                //      A host in a breakaway-OK job still lets the per-command
                //      assignment succeed; only a no-breakaway host forces degradation.
                //  (2) The old degraded branch asserted "host in job ⇒ child NOT in
                //      ANY job", which is LOGICALLY IMPOSSIBLE: job membership is
                //      lineage-inherited, so a child of a job-member host is ALWAYS
                //      in a job (the host's), regardless of our assignment. That
                //      assertion could never pass on a host that is itself in a job.
                //
                // The authoritative, confound-immune proof is the production
                // mcp.shell.job record: it carries the child's pid (hermetic — a
                // foreign record has a different pid) AND assigned_immediately, which
                // is production's OWN specific-job re-query (IsProcessInJob against
                // THIS job handle, same thread/handle, immediately after
                // AssignProcessToJobObject). "Is the child in the job WE just made?"
                // is immune to the inheritance confound because it names a specific
                // job, not "any job".
                //
                // HERMETICITY — the run-id file can contain foreign records from
                // parallel test classes (DevMind.Trace is a process-global static; the
                // run id only names the file). The regex anchors on the event name
                // "mcp.shell.job" so it cannot match mcp.shell.spawn or
                // mcp.shell.job.degraded; the [^}]*? bounds each match to one record's
                // data object. We then require the pid to equal THIS child's pid.
                bool childHasJobRecord = false;
                bool childAssignedImmediately = false;
                var jobPids = new List<int>();
                var jobRecordRegex = new System.Text.RegularExpressions.Regex(
                    @"""event""\s*:\s*""mcp\.shell\.job""[^}]*?""pid""\s*:\s*(\d+)[^}]*?""assigned_immediately""\s*:\s*(true|false)");
                foreach (System.Text.RegularExpressions.Match m in jobRecordRegex.Matches(trace))
                {
                    if (!int.TryParse(m.Groups[1].Value, out int recPid))
                        continue;
                    jobPids.Add(recPid);
                    if (recPid == childPid)
                    {
                        childHasJobRecord = true;
                        childAssignedImmediately = m.Groups[2].Value == "true";
                    }
                }
                string pidsFound = jobPids.Count > 0 ? string.Join(",", jobPids) : "(none)";
                string degradedReason = ExtractReason(trace);

                if (childHasJobRecord)
                {
                    // ASSIGNMENT SUCCEEDED for this child: production created a specific
                    // per-command job and, immediately after AssignProcessToJobObject,
                    // re-queried membership against that SAME job handle. This is the
                    // definitive containment proof — it names a specific job, so it is
                    // immune to the lineage-inheritance confound that poisons an "any
                    // job" live query, and it carries the child's pid, so it is immune
                    // to interleaving.
                    Assert.True(childAssignedImmediately,
                        $"mcp.shell.job record for child PID {childPid} exists but " +
                        $"assigned_immediately is not true — production's own immediate " +
                        $"specific-job re-query (IsProcessInJob on the job it had just " +
                        $"assigned the child to) said the child was NOT a member. The " +
                        $"OS accepted AssignProcessToJobObject but did not record the " +
                        $"child in the job: containment is a NO-OP. " +
                        $"DIAG: traceTail=[{diagTail}]");

                    // CORROBORATION via the live any-job oracle. Its soundness depends on
                    // the host's own membership:
                    if (hostMembership.State == JobMembershipState.NotInJob)
                    {
                        // Host not in a job ⇒ the child's only possible job is DevMind's
                        // per-command job, so "in any job" == "in DevMind's job". The live
                        // query must agree with the trace proof.
                        Assert.True(childMembership.State == JobMembershipState.InJob,
                            $"live any-job oracle says child (PID {childPid}) is NOT in any job " +
                            $"(state={childMembership.State}, detail=[{childMembership.Detail}]) on a host " +
                            $"that is itself not in a job — contradicts the specific-job trace proof. " +
                            $"DIAG: traceTail=[{diagTail}]");
                    }
                    else
                    {
                        // Host IS in a job ⇒ the child inherits it, so "in any job" is
                        // TRUE regardless of our assignment and is UNINFORMATIVE about
                        // containment. We can only assert the necessary consequence of
                        // inheritance: the child must be InJob (never NotInJob, never
                        // CouldNotQuery). The containment proof itself is the
                        // assigned_immediately:true asserted above.
                        Assert.True(childMembership.State == JobMembershipState.InJob,
                            $"host (PID {Environment.ProcessId}) is in a job, so the child " +
                            $"(PID {childPid}) must at least be in the inherited job — but the " +
                            $"live query returned state={childMembership.State}, detail=" +
                            $"[{childMembership.Detail}], which contradicts job lineage " +
                            $"inheritance. DIAG: traceTail=[{diagTail}]");
                    }
                }
                else
                {
                    // NO mcp.shell.job record for this child ⇒ the per-command assignment
                    // DEGRADED: production could not place the child in a job (most
                    // commonly Win32 5 — the host is a member of a no-breakaway parent
                    // job) and fell back to the pre-job behavior. The design's contract
                    // is that this degradation is OBSERVABLE: a mcp.shell.job.degraded
                    // record with a reason. (The command still ran to completion — that
                    // non-negotiable invariant was asserted earlier: exitCode -1 + "timed out".)
                    Assert.True(trace.Contains("mcp.shell.job.degraded"),
                        $"no mcp.shell.job record for child PID {childPid} AND no " +
                        $"mcp.shell.job.degraded record — the job layer neither contained " +
                        $"this child nor recorded a degradation. This is the silent-failure " +
                        $"the feature must not exhibit. (mcp.shell.job pids in trace: " +
                        $"[{pidsFound}]; host membership: {hostMembership.State}; live child " +
                        $"membership: {childMembership.State}.) DIAG: traceTail=[{diagTail}]");
                    Assert.False(string.IsNullOrEmpty(degradedReason),
                        "mcp.shell.job.degraded lacks a reason — a silent degradation defeats " +
                        "the observability purpose");
                }
            }
            finally
            {
                for (int i = 0; i < envKeys.Length; i++)
                    Environment.SetEnvironmentVariable(envKeys[i], saved[i]);
                ResetTraceInitialized();
            }
        }

        // --- Test 2: already-exited process — null handle + reason, no throw ---

        [Fact]
        public void TryCreateAndAssignJob_AlreadyExitedProcess_ReturnsNullAndDoesNotThrow()
        {
            if (!OperatingSystem.IsWindows())
                return; // non-Windows is covered by test 4's guard contract

            // cmd /c exit 0 starts and finishes near-instantly. Waiting on it
            // guarantees the process is gone before we ask for containment —
            // the exact failure mode the degradable contract must absorb.
            using var proc = Process.Start(new ProcessStartInfo("cmd.exe", "/c exit 0")
            {
                UseShellExecute = false,
                CreateNoWindow  = true
            });
            Assert.NotNull(proc);
            proc.WaitForExit(5000);

            (IntPtr jobHandle, string reason) result = default;
            bool threw = false;
            try
            {
                result = WindowsJobObject.TryCreateAndAssignJob(proc);
            }
            catch
            {
                threw = true;
            }

            Assert.False(threw, "TryCreateAndAssignJob threw — it must be incapable of throwing");
            Assert.Equal(IntPtr.Zero, result.jobHandle);
            Assert.False(string.IsNullOrEmpty(result.reason),
                "a degraded result must carry a reason for the trace");
        }

        // --- Test 3: null process — null handle + reason, no throw ---

        [Fact]
        public void TryCreateAndAssignJob_NullProcess_ReturnsNullAndDoesNotThrow()
        {
            (IntPtr jobHandle, string reason) result = default;
            bool threw = false;
            try
            {
                result = WindowsJobObject.TryCreateAndAssignJob(null);
            }
            catch
            {
                threw = true;
            }

            Assert.False(threw, "TryCreateAndAssignJob(null) threw — it must be incapable of throwing");
            Assert.Equal(IntPtr.Zero, result.jobHandle);
            Assert.False(string.IsNullOrEmpty(result.reason));
        }

        // --- Test 4: guard path is a no-op off-Windows ---

        [Fact]
        public void TryCreateAndAssignJob_NonWindows_IsANoOpByConstruction()
        {
            // On Windows this asserts the degenerate inputs still never throw
            // and never create a job (the same contract the non-Windows guard
            // relies on, since both funnel through the null-handle return
            // path). On a non-Windows host the IsWindows() guard short-circuits
            // before any interop — every call must return a null handle.
            (IntPtr jobHandle, string reason) r1 = WindowsJobObject.TryCreateAndAssignJob(null);
            Assert.Equal(IntPtr.Zero, r1.jobHandle);

            if (!OperatingSystem.IsWindows())
            {
                // A real (already-exited) process on a non-Windows host must
                // still come back as a no-op — no interop may have been
                // attempted.
                using var proc = Process.Start(new ProcessStartInfo("sh", "-c true")
                {
                    UseShellExecute = false,
                    CreateNoWindow  = true
                });
                if (proc != null)
                {
                    proc.WaitForExit(5000);
                    var r2 = WindowsJobObject.TryCreateAndAssignJob(proc);
                    Assert.Equal(IntPtr.Zero, r2.jobHandle);
                }
            }
        }

        // --- Test 5: GROUND-TRUTH validation of the P/Invoke instrument ---
        //
        // This test does NOT go through ShellRunner — it spawns a long-lived
        // process directly, assigns it via the same WindowsJobObject.TryCreateAndAssignJob
        // production uses, and queries membership while the child is ALIVE (before the
        // job handle is closed and the child is reaped). It answers the only question
        // that matters: when AssignProcessToJobObject returns TRUE, does the OS
        // actually consider the process a member? If it does, the P/Invoke works and
        // any "NotInJob" in the ShellRunner test is a real feature bug (or a
        // ShellRunner-specific timing issue). If it does not, the P/Invoke is still
        // broken and the ShellRunner result is an instrument artifact.
        [Fact]
        public void GroundTruth_KnownAssignedProcess_IsReportedInJob()
        {
            if (!OperatingSystem.IsWindows())
                return; // containment is a Windows feature; no-op elsewhere by design

            // Spawn powershell.exe with a long sleep so the child is definitely
            // alive during the membership query. The sleep is 60s — far longer
            // than the test needs — so there is zero risk of the child exiting
            // before we query it. The job's KILL_ON_JOB_CLOSE (armed by
            // TryCreateAndAssignJob) guarantees the child is reaped when we
            // close the job handle in the finally, so nothing lingers past the
            // test regardless of the sleep length.
            using var proc = Process.Start(new ProcessStartInfo("powershell.exe",
                "-NoProfile -NonInteractive -Command Start-Sleep 60")
            {
                UseShellExecute = false,
                CreateNoWindow  = true
            });
            Assert.NotNull(proc);

            (IntPtr jobHandle, string reason) = WindowsJobObject.TryCreateAndAssignJob(proc);

            if (jobHandle == IntPtr.Zero)
            {
                // AssignProcessToJobObject failed (e.g. Win32 5 — this host is in a
                // no-breakaway parent job). The test is INCONCLUSIVE for the positive
                // case, but we still assert the contract: a failure must carry a
                // reason and must NOT have left a half-armed job behind.
                Assert.False(string.IsNullOrEmpty(reason),
                    $"assignment failed but no reason was given — the degradable contract is broken");
                return;
            }

            try
            {
                // THE child is alive (Start-Sleep 60, we are well within the first second).
                //
                // Query membership against the SPECIFIC job handle (not NULL/"any job").
                // This is the exact inverse of the AssignProcessToJobObject call we just
                // made, and — critically — it is IMMUNE to the lineage-inheritance
                // confound that poisons an "any job" query: if the host process (the test
                // runner) is itself in a parent job, the child inherits it, so "any job"
                // would be true regardless of whether OUR assignment succeeded. Asking
                // "is the child in THIS job we just created?" is the only question the
                // answer to which is not predetermined by the environment.
                //
                // Query via TWO independent process handles to prove the result is not a
                // function of a particular handle's access rights:
                //  1. proc.Handle — the handle CreateProcess returned (PROCESS_ALL_ACCESS),
                //     the same handle production's self-check uses.
                //  2. A freshly opened handle (PROCESS_QUERY_LIMITED_INFORMATION) — the
                //     handle test 1's oracle uses. A different access right, same child.
                bool memberViaSpawnHandle = false;
                bool queryOkViaSpawnHandle = NativeIsProcessInJob(proc.Handle, jobHandle, out memberViaSpawnHandle);

                IntPtr freshHandle = NativeOpenProcess(0x1000, /*inherit=*/false, proc.Id);
                bool memberViaFreshHandle = false;
                bool queryOkViaFreshHandle = false;
                if (freshHandle != IntPtr.Zero)
                {
                    try
                    {
                        queryOkViaFreshHandle = NativeIsProcessInJob(freshHandle, jobHandle, out memberViaFreshHandle);
                    }
                    finally
                    {
                        NativeCloseHandle(freshHandle);
                    }
                }

                Assert.True(queryOkViaSpawnHandle,
                    $"IsProcessInJob(spawnHandle, SPECIFIC job) FAILED (Win32 {Marshal.GetLastWin32Error()}) — " +
                    $"the instrument cannot distinguish 'query failed' from 'not in this job'. " +
                    $"INCONCLUSIVE.");
                Assert.True(queryOkViaFreshHandle,
                    $"IsProcessInJob(freshHandle, SPECIFIC job) FAILED (Win32 {Marshal.GetLastWin32Error()}) — " +
                    $"the instrument cannot distinguish 'query failed' from 'not in this job'. " +
                    $"INCONCLUSIVE.");

                // THE ASSERTION: a known-assigned, known-alive process MUST report as a
                // member of the SPECIFIC job it was just assigned to, on both handles.
                // If this passes, the P/Invoke instrument is validated (correct arg order,
                // out-param, access rights) AND the assignment genuinely took effect — the
                // OS records the child as a member of THIS job. If it fails, either the
                // P/Invoke is still broken OR the assignment is a no-op; the trace line
                // (assigned_immediately, same specific-job query) will say which.
                Assert.True(memberViaSpawnHandle && memberViaFreshHandle,
                    $"GROUND-TRUTH VALIDATION FAILED: a known-assigned, known-alive process " +
                    $"(PID {proc.Id}) does NOT report as a member of the specific job it was " +
                    $"assigned to. " +
                    $"spawnHandle: queryOk={queryOkViaSpawnHandle} member={memberViaSpawnHandle}; " +
                    $"freshHandle: queryOk={queryOkViaFreshHandle} member={memberViaFreshHandle}. " +
                    $"This means either the P/Invoke IsProcessInJob(process, job, out member) is " +
                    $"still broken, or the assignment did not take effect. Cross-check the " +
                    $"mcp.shell.job assigned_immediately trace line for the same answer.");
            }
            finally
            {
                // KILL_ON_JOB_CLOSE: closing the job handle tears down the entire
                // contained process tree (the powershell.exe + its Start-Sleep
                // grandchild). The 60s sleep is irrelevant — the child is reaped
                // here, not by the sleep expiring.
                WindowsJobObject.ReleaseJob(jobHandle);
                proc.Dispose();
            }
        }

        // --- Helpers ---

        /// <summary>
        /// Reset Trace's private _initialized/_enabled/_logPath state so the
        /// next Trace.Event re-runs Init() and picks up the env vars this test
        /// set. Test-only; DevMind.Core.Tests is InternalsVisibleTo-able, but
        /// the flag is private (not internal), hence reflection.
        /// </summary>
        private static void ResetTraceInitialized()
        {
            try
            {
                var traceType = typeof(DevMind.Trace);
                foreach (var name in new[] { "_initialized", "_enabled", "_shutdownFired", "_dirEnsured" })
                {
                    var f = traceType.GetField(name, BindingFlags.NonPublic | BindingFlags.Static);
                    if (f != null)
                        f.SetValue(null, false);
                }
                var logPath = traceType.GetField("_logPath", BindingFlags.NonPublic | BindingFlags.Static);
                logPath?.SetValue(null, "");
            }
            catch
            {
                // If the internal layout ever changes, the test still runs —
                // it just may capture no trace, in which case the trace-based
                // assertions below fail loudly rather than passing silently.
            }
        }

        private static string ExtractReason(string trace)
        {
            // The degraded record looks like:
            //   {"ts":...,"event":"mcp.shell.job.degraded","data":{"reason":"..."}}
            int idx = trace.IndexOf("mcp.shell.job.degraded", StringComparison.Ordinal);
            if (idx < 0)
                return null;
            string after = trace.Substring(idx);
            int rIdx = after.IndexOf("\"reason\"", StringComparison.Ordinal);
            if (rIdx < 0)
                return null;
            int colon = after.IndexOf(':', rIdx);
            if (colon < 0)
                return null;
            int qStart = after.IndexOf('"', colon + 1);
            if (qStart < 0)
                return null;
            int qEnd = after.IndexOf('"', qStart + 1);
            if (qEnd < 0)
                return null;
            return after.Substring(qStart + 1, qEnd - qStart - 1);
        }

        /// <summary>
        /// The three outcomes of a job-membership query. Deliberately NOT a
        /// bool: collapsing query failures (dead PID, access-denied, interop
        /// error) into "not in a job" is the same silent-collapse bug class
        /// as the production degradation path — a false negative would pass a
        /// test that should have failed (or fail one that should have passed).
        /// </summary>
        private enum JobMembershipState
        {
            InJob,        // IsProcessInJob returned true
            NotInJob,     // IsProcessInJob returned false with a valid handle
            CouldNotQuery // process not findable, not openable, or query failed
        }

        private readonly struct JobMembershipResult
        {
            public JobMembershipState State { get; }
            public string Detail { get; }
            public JobMembershipResult(JobMembershipState state, string detail = "")
            {
                State = state;
                Detail = detail;
            }
        }

        /// <summary>
        /// Query whether <paramref name="pid"/> is a member of any job object,
        /// distinguishing the three states so a query failure can never be
        /// read as "not in a job". Opens the process with
        /// PROCESS_QUERY_LIMITED_INFORMATION (the right and most permissive
        /// access right for this query on modern Windows) rather than
        /// PROCESS_QUERY_INFORMATION. The caller is responsible for the query
        /// happening while the process is demonstrably alive (for the child,
        /// that means BEFORE ExecuteAsync's timeout path reaps it).
        /// </summary>
        private static JobMembershipResult QueryJobMembership(int pid)
        {
            Process proc;
            try
            {
                proc = Process.GetProcessById(pid);
            }
            catch (Exception ex)
            {
                return new JobMembershipResult(JobMembershipState.CouldNotQuery,
                    $"GetProcessById({pid}) threw: {ex.GetType().Name}");
            }

            IntPtr handle = IntPtr.Zero;
            try
            {
                bool alive = false;
                try
                {
                    proc.Refresh();
                    alive = !proc.HasExited;
                }
                catch { /* refresh failed — HasExited unknown; carry on and let OpenProcess decide */ }

                handle = NativeOpenProcess(0x1000, /*inherit=*/false, pid); // PROCESS_QUERY_LIMITED_INFORMATION
                if (handle == IntPtr.Zero)
                    return new JobMembershipResult(JobMembershipState.CouldNotQuery,
                        $"OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, {pid}) failed (Win32 {Marshal.GetLastWin32Error()})"
                        + (alive ? "" : " (process already exited — queried a corpse)"));

                // 3-arg modern form: (process, job, out member). JobHandle NULL =
                // "is it in ANY job". The return value is "query succeeded"; the
                // membership answer arrives via the out param. A query failure is a
                // distinct, honest CouldNotQuery — it must never collapse into
                // NotInJob (the exact silent-collapse bug this three-state enum
                // exists to prevent).
                bool member;
                bool queryOk = NativeIsProcessInJob(handle, IntPtr.Zero, out member);
                if (!queryOk)
                    return new JobMembershipResult(JobMembershipState.CouldNotQuery,
                        $"IsProcessInJob({pid}, any-job) failed (Win32 {Marshal.GetLastWin32Error()})");
                return member
                    ? new JobMembershipResult(JobMembershipState.InJob)
                    : new JobMembershipResult(JobMembershipState.NotInJob);
            }
            finally
            {
                if (handle != IntPtr.Zero)
                {
                    try { NativeCloseHandle(handle); } catch { }
                }
                proc.Dispose();
            }
        }

        // Modern 3-argument form (Win XP+): (ProcessHandle, JobHandle, out Member).
        // JobHandle NULL = "any job". Return = query succeeded; membership via out.
        // The legacy 2-arg form reads the success bool as membership and passes a
        // garbage 3rd register — never use it. (EntryPoint must be the un-suffixed
        // name on this host — see the OpenProcess note below.)
        [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "IsProcessInJob")]
        private static extern bool NativeIsProcessInJob(IntPtr hProcess, IntPtr hJob, out bool bMember);

        // NOTE: on this host the kernel32 export that resolves is the
        // un-suffixed "OpenProcess" — "OpenProcessW" (and by extension the
        // plain A/W convention) throws EntryPointNotFoundException here, while
        // "OpenProcess" resolves (verified empirically). The signature is the
        // wide version's (uint access, bool inherit, int pid).
        [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "OpenProcess")]
        private static extern IntPtr NativeOpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

        // NOTE: the C# method name is NativeCloseHandle, but the real kernel32
        // export is "CloseHandle" — without an explicit EntryPoint the runtime
        // looks for "NativeCloseHandle" in kernel32.dll and throws
        // EntryPointNotFoundException. (The oracle's finally had a try/catch that
        // silently swallowed this, so it was a latent handle leak all along.)
        [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "CloseHandle")]
        private static extern bool NativeCloseHandle(IntPtr hObject);
    }
}
