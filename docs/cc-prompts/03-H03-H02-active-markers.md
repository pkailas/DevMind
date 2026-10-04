# DevMind harness: H-03 (jobs that die with the server) + per-job active markers + close H-02

Repo: C:\Users\pkailas\source\repos\DevMind  (build: dotnet build "C:\Users\pkailas\source\repos\DevMind\DevMind.slnx" -t:Rebuild)
Read docs\harness-watchlist.md H-02 and H-03 first. Run only after prompt 02 has committed (both touch AgentJobManager.cs).

## Current code (verify before relying on it; line numbers are approximate — search for the code)
- DevMind.McpServer\AgentJobManager.cs ~L1145-1200: ONE marker file `_active.json` in TranscriptDir (job_id, pid, working_dir,
  started_at_utc, transcript). WriteActiveMarker at job start, ClearActiveMarker at job end and on graceful
  shutdown. IsJobActiveElsewhere() reads that single file and checks whether its pid is alive.
- Several McpServer processes run concurrently on one machine (see the comment on _idAllocLock: "three were live on
  BEAST at once"), all sharing TranscriptDir.
- AgentTaskTools.cs CheckStaleActiveMarker(jobId) and the result/status fallback: a job unknown to this process
  falls back to the sidecar and reports "NOT continuable across restarts"; a job that died mid-run has no sidecar.

## Bug: the marker is shared across processes
Process A finishing a job deletes process B's marker; B's running job then reads as "not active elsewhere" and A's cleanup
(anything gated on IsJobActiveElsewhere — find every caller) may touch files B owns. A job that dies after its marker was
clobbered is never detected.
Fix: one marker per job — `_active_<job_id>.json` (same fields). Write/clear only your own. IsJobActiveElsewhere() = any
marker whose pid is alive and != this process. Keep reading a legacy `_active.json` once for back-compat (treat it like any
other marker), but never write it.

## H-03: a job that dies with its server
On AgentJobManager startup (and lazily in CheckStaleActiveMarker), for each marker whose pid is dead:
- If no result sidecar exists for that job_id, write one: state "stopped_incomplete", incomplete_reasons ["server_restart"],
  error "server process <pid> exited while the job was running", started_at_utc from the marker, ended_at_utc = now (say
  it is the detection time, not the death time), working_dir, transcript_path, and `iterations` = the last iteration number
  found in the transcript if it can be parsed reliably (gray area: find the transcript's iteration marker; if it cannot be
  parsed reliably, leave null rather than guess).
- Then delete the dead marker.
- devmind_task_status / devmind_task_result for that job report it from the sidecar with a clear note: died with the server,
  not continuable, start a fresh task with a continuation brief.
Never touch a marker whose pid is alive (another process's running job).
Gray area: a pid reused by an unrelated process after a crash. Decide whether to also compare process start time to
started_at_utc (Process.StartTime); do it if it's cheap and testable, and say what you chose.

## H-02: close with evidence, add tracing
Investigation already done: TaskContinue (AgentTaskTools.cs) awaits only ProbeModelServerAsync (HttpClient Timeout
5 s) before AgentJobManager.Continue, which locks, sets parent.ContinuedByJobId and enqueues synchronously. The Sep 23
symptom (4-min timeout, parent continued_by null) means the call never reached the manager — consistent with the
client-side MCP stall seen when the Claude Desktop conversation is not in the foreground.
Do: add trace events at entry and exit of devmind_task_start and devmind_task_continue (tool name, job id or error, elapsed ms)
using the existing trace mechanism (find how McpServer traces today; don't invent a new one), so a future hang shows whether
the call arrived. Update H-02 in the watchlist: "not reproducible in current code; evidence above; tracing added".

## Tests
- Two managers sharing one TranscriptDir (simulate two pids where the code allows; use the existing DEVMIND_TASKS_DIR /
  env-seam test patterns, serial collection per H-44): job end in one never deletes the other's marker.
- IsJobActiveElsewhere: true for another live pid's marker, false for own pid, false for a dead pid.
- Startup sweep: dead-pid marker with no sidecar → sidecar written with server_restart, marker removed; live-pid marker untouched;
  dead-pid marker WITH an existing sidecar → sidecar unchanged, marker removed.
- Legacy `_active.json` is read but never written.
- Trace events emitted on start/continue (success and error paths).

## Done means
- Rebuild 0 errors / 0 warnings; dotnet test with --blame-hang --blame-hang-timeout 45s --blame-hang-dump-type none all green; report counts.
- Mutation check: make job end clear ALL markers again and confirm the cross-process test fails by name; restore.
- Watchlist: H-02 closed with the evidence, H-03 fixed, add a new H-item for the shared-marker bug (fixed in this commit).
- Update docs\cc-prompts\README.md: mark this prompt done with the commit hash.
- Commit. Do NOT deploy.
- Report: files changed, gray-area decisions, test counts.
