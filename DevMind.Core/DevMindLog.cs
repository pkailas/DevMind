// File: DevMindLog.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Diagnostics that survive a Release build.
//
// Debug.WriteLine carries [Conditional("DEBUG")], so the compiler removes the call —
// and its string literals — from every build without DEBUG defined. run-deploy.ps1
// publishes -c Release, so the deployed engine emitted nothing from any of its
// diagnostic sites. Almost all of them sit inside a catch that otherwise swallows the
// error, which left an unattended failure with no trail at all.
//
// This is the smallest thing that fixes that:
//
//   * File only, never stdout. DevMind.McpServer speaks the MCP stdio protocol on
//     stdout (which is why its Program.cs calls builder.Logging.ClearProviders()); a
//     stray write there corrupts the protocol and kills the client connection.
//   * Never throws. Every caller is inside a catch that is deliberately swallowing an
//     error; a logger that throws would turn a handled failure into a crash. Every
//     path here — resolve, create, rotate, sweep, write — is individually guarded, and
//     a failure to log is simply a lost line.
//   * Bounded. One file per process, capped by size with a single rotation, and a
//     startup sweep capped by BOTH age and file count. The worst case is arithmetic,
//     not a hope: MaxFiles * MaxFileBytes.
//   * No configuration file. On unless DEVMIND_LOG=off.
//
// Line format — one physical line per event, embedded newlines flattened, so a grep
// for a component prefix returns whole events:
//
//   2026-09-21 14:03:22.145Z  pid=12345  [NearlineCache] session dir delete failed: ...

using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace DevMind
{
    /// <summary>
    /// Best-effort diagnostic log under <c>%APPDATA%\devmind\logs\</c>, written by the
    /// engine's otherwise-silent failure paths. Always safe to call: it never throws and
    /// never writes to stdout.
    /// </summary>
    public static class DevMindLog
    {
        /// <summary>
        /// Size at which the active file is rotated to <c>.1</c> and a fresh one started.
        /// A line here is ~120 bytes and these sites fire once per session or on failure,
        /// so 2 MB is several times more than any real run produces — large enough that
        /// rotation is a safety net rather than routine, small enough that the arithmetic
        /// bound below stays comfortable.
        /// </summary>
        public const long MaxFileBytes = 2L * 1024 * 1024;

        /// <summary>
        /// How long a log file is kept. PatchBackupSweeper uses 24 hours because a patch
        /// backup is worthless once its turn ends; a diagnostic log is the opposite — it
        /// is read AFTER somebody notices something broke, which is routinely days later
        /// ("it died over the weekend"). A week covers that. A month would not make the
        /// log more useful, only the worst case larger.
        /// </summary>
        public static readonly TimeSpan MaxAge = TimeSpan.FromDays(7);

        /// <summary>
        /// Hard ceiling on the number of log files kept, enforced after the age sweep.
        /// Age alone cannot bound a burst — every delegated job starts a process, and a
        /// busy hour can start dozens within the age window. 20 is room for several
        /// concurrent processes plus a few days of history, and it is what makes the
        /// total bound a fixed number: at most <see cref="MaxFiles"/> files, each at most
        /// <see cref="MaxFileBytes"/> (plus the final line that tripped the rotation), so
        /// roughly 40 MB in the worst case and a few hundred KB in practice.
        /// </summary>
        public const int MaxFiles = 20;

        /// <summary>Search pattern matching every file this class owns, including rotations.</summary>
        internal const string FilePattern = "devmind-*.log*";

        // UTF-8 without a BOM: the file is appended to across processes and runs, and a
        // BOM would be re-emitted mid-file every time a fresh handle wrote the first bytes.
        private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        private static readonly object _gate = new object();

        // Path the retention sweep last ran for. Hooking the sweep to the first line written
        // rather than to each skin's startup keeps every entry point out of it, and means a
        // process that never logs never touches the directory. Keyed on the path rather than
        // a plain bool so a process that runs past midnight — whose active file changes with
        // the date — sweeps again for its new file instead of never sweeping again.
        private static string _sweptFor;

        /// <summary>
        /// True unless <c>DEVMIND_LOG</c> is set to <c>off</c>/<c>false</c>/<c>0</c>.
        /// <para>
        /// On by default deliberately: a log that is off when something breaks unattended
        /// is worth nothing, and the bounded retention above makes always-on cheap. The
        /// switch is an environment variable rather than a <c>devmind.json</c> key because
        /// only the TUI loads <see cref="TuiConfig"/> — a key there would be silently
        /// ignored by DevMind.Cli and DevMind.McpServer, which are exactly the unattended
        /// processes this exists for. Re-read on every call, like DevMindPaths.GlobalDir.
        /// </para>
        /// </summary>
        public static bool IsEnabled
        {
            get
            {
                try
                {
                    string raw = Environment.GetEnvironmentVariable("DEVMIND_LOG");
                    if (string.IsNullOrWhiteSpace(raw)) return true;
                    string v = raw.Trim();
                    return !(v.Equals("off", StringComparison.OrdinalIgnoreCase)
                          || v.Equals("false", StringComparison.OrdinalIgnoreCase)
                          || v.Equals("0", StringComparison.Ordinal));
                }
                catch
                {
                    // A blocked environment read must not decide the logger is off.
                    return true;
                }
            }
        }

        /// <summary>
        /// Absolute path of the file this process writes. One file per process: a shared
        /// file would need cross-process locking that can fail (and File.Append's default
        /// share mode throws outright while another writer holds the handle), while the
        /// pid in the name is exactly the disambiguator needed when two McpServer
        /// processes are running and a failure has to be pinned on one of them.
        /// Re-derived on every call so <c>DEVMIND_GLOBAL_DIR</c> is honoured live.
        /// </summary>
        public static string CurrentLogPath =>
            Path.Combine(
                DevMindPaths.GlobalLogsDir,
                string.Format(
                    CultureInfo.InvariantCulture,
                    "devmind-{0:yyyyMMdd}-pid{1}.log",
                    DateTime.Now,
                    SafeProcessId()));

        /// <summary>
        /// Appends one line to this process's log file. Safe to call from a catch block:
        /// it never throws, and a failure to write is simply a lost line.
        /// <para>
        /// Also emits the same text through <see cref="Debug.WriteLine(string)"/>, so a
        /// developer with a debugger attached still sees it in the Output window under a
        /// Debug build. Doing it here rather than leaving a second call at each site means
        /// the two can never drift apart.
        /// </para>
        /// </summary>
        /// <param name="message">
        /// The event, already carrying its <c>[Component]</c> prefix. Embedded newlines are
        /// flattened so one event is always one line.
        /// </param>
        public static void Write(string message)
        {
            // Unconditional (NOT [Conditional("DEBUG")]) — this is the half that has to
            // survive Release. The Debug.WriteLine below is the half that does not.
            Debug.WriteLine(message);

            try
            {
                if (!IsEnabled) return;
                if (string.IsNullOrEmpty(message)) return;

                string line = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0:yyyy-MM-dd HH:mm:ss.fff}Z  pid={1}  {2}{3}",
                    DateTime.UtcNow,
                    SafeProcessId(),
                    Flatten(message),
                    Environment.NewLine);

                lock (_gate)
                {
                    string path = CurrentLogPath;
                    string dir = Path.GetDirectoryName(path);
                    if (string.IsNullOrEmpty(dir)) return;

                    Directory.CreateDirectory(dir);

                    if (!string.Equals(_sweptFor, path, StringComparison.OrdinalIgnoreCase))
                    {
                        _sweptFor = path;            // set FIRST: a failing sweep must not retry forever
                        Sweep(dir, path);
                    }

                    RotateIfOversized(path);

                    // FileShare.ReadWrite so another handle on the same file — a tail, an
                    // editor, or this process's own reader in a test — cannot make the
                    // write fail. File.AppendAllText defaults to FileShare.Read and would.
                    using var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                    byte[] bytes = Utf8NoBom.GetBytes(line);
                    fs.Write(bytes, 0, bytes.Length);
                }
            }
            catch
            {
                // Swallowed completely, by design. Every caller is already inside a catch
                // that is handling something else; a logging failure must not displace it,
                // and there is nowhere safe to report it to (stdout belongs to the MCP
                // stdio protocol).
            }
        }

        /// <summary>
        /// Deletes log files older than <see cref="MaxAge"/>, then — if more than
        /// <see cref="MaxFiles"/> remain — the oldest until the count is back inside the
        /// cap. The active file is never a candidate. Each delete is in its own try/catch
        /// (the PatchBackupSweeper pattern), so a file locked by another process cannot
        /// abort the rest of the sweep.
        /// </summary>
        /// <param name="dir">Directory to sweep.</param>
        /// <param name="activePath">Path this process is writing; excluded from deletion.</param>
        internal static void Sweep(string dir, string activePath)
        {
            try
            {
                if (!Directory.Exists(dir)) return;

                var files = Directory.GetFiles(dir, FilePattern, SearchOption.TopDirectoryOnly)
                    .Where(p => !string.Equals(p, activePath, StringComparison.OrdinalIgnoreCase))
                    .Select(p => new FileInfo(p))
                    .ToList();

                DateTime cutoff = DateTime.Now - MaxAge;
                var survivors = new System.Collections.Generic.List<FileInfo>();

                foreach (FileInfo f in files)
                {
                    try
                    {
                        if (f.LastWriteTime <= cutoff) { f.Delete(); continue; }
                        survivors.Add(f);
                    }
                    catch
                    {
                        // Locked by a live process, or already gone. Either way it still
                        // occupies a slot, so it counts toward the cap below.
                        survivors.Add(f);
                    }
                }

                // The active file occupies one slot, so the others may hold MaxFiles - 1.
                int allowed = MaxFiles - 1;
                if (survivors.Count <= allowed) return;

                foreach (FileInfo f in survivors
                             .OrderBy(f => f.LastWriteTime)      // oldest first
                             .Take(survivors.Count - allowed))
                {
                    try { f.Delete(); } catch { /* locked or gone — skip, never abort the sweep */ }
                }
            }
            catch
            {
                // Best-effort. A directory that cannot be enumerated just does not get swept.
            }
        }

        /// <summary>
        /// Rotates <paramref name="path"/> to <c>&lt;path&gt;.1</c> once it reaches
        /// <see cref="MaxFileBytes"/>, replacing any previous rotation. Keeps the RECENT
        /// half of a runaway rather than the first half, which is the half worth having
        /// after a crash, and caps one process at two files.
        /// </summary>
        private static void RotateIfOversized(string path)
        {
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists || info.Length < MaxFileBytes) return;

                string rotated = path + ".1";
                File.Move(path, rotated, overwrite: true);
            }
            catch
            {
                // Rotation is best-effort: if the move fails the file simply keeps growing
                // until the next attempt succeeds, and the age/count sweep still reclaims it.
            }
        }

        private static string Flatten(string message)
            => message.Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ');

        private static int SafeProcessId()
        {
            try { return Environment.ProcessId; }
            catch { return 0; }
        }

        /// <summary>
        /// Test seam: forgets that the retention sweep has run, so a test that repoints
        /// <c>DEVMIND_GLOBAL_DIR</c> at a fresh directory gets the sweep again.
        /// Production never calls this — the sweep is once per process by design.
        /// </summary>
        internal static void ResetForTests()
        {
            lock (_gate) { _sweptFor = null; }
        }
    }
}
