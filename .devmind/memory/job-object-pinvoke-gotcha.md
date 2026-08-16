IsProcessInJob P/Invoke — three critical gotchas (learned the hard way, Aug 2025):

1. SIGNATURE: The modern (Win XP+) form is `BOOL IsProcessInJob(HANDLE ProcessHandle, HANDLE JobHandle, PBOOL Result)`. First arg = PROCESS, second = JOB (NULL = "any job"), third = out membership. The return value is "query succeeded", NOT membership. The legacy 2-arg form `bool IsProcessInJob(IntPtr hJob, IntPtr hProcess)` is WRONG TWICE: args swapped AND missing the 3rd out param — it reads the success bool as membership and passes a garbage 3rd register. On x64 this silently returns false without crashing.

2. SPECIFIC vs ANY job: JobHandle=NULL ("any job") is CONFOUNDED on a host that is itself in a parent job, because job membership is LINEAGE-INHERITED — the child inherits the host's job regardless of whether DevMind's per-command assignment succeeded. The precise, immune query is IsProcessInJob(processHandle, specificJobHandle, out member) — the exact inverse of AssignProcessToJobObject.

3. EntryPoint: The kernel32 export is "IsProcessInJob" (no W suffix), "OpenProcess" (not "OpenProcessW"), and "CloseHandle" (not the C# method name). DllImport without an explicit EntryPoint uses the C# method name as the export — so NativeCloseHandle without EntryPoint="CloseHandle" throws EntryPointNotFoundException. This was silently swallowed by a try/catch in the test's finally (latent handle leak).

Test 5 (GroundTruth_KnownAssignedProcess_IsReportedInJob) in ShellRunnerJobObjectTests.cs validates the instrument: creates a job, assigns a known-alive child, queries the specific job handle via two independent handles. Passes on this host.

Production trace line in WindowsJobObject.TryCreateAndAssignJob: uses the specific job handle (not IntPtr.Zero) for assigned_immediately. This is the only prod edit authorized in that file.