// File: SessionImageStore.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Saves every user-attached image (TUI auto-attach, /image, /image <pdf> pages) to a
// per-session folder on disk, so a later step can drop the base64 from conversation
// history and point at the saved file instead. Layout:
//
//   %LOCALAPPDATA%\devmind\images\<SessionId>\NNN-<label>.<ext>
//   %LOCALAPPDATA%\devmind\images\<SessionId>\.owner        (pid of the writing process)
//
// Saving is strictly best-effort: every failure is logged and swallowed, and the caller
// still attaches the image. Library-ingest page images are NOT user attachments and
// never come through here.

using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace DevMind
{
    /// <summary>
    /// Per-session on-disk store for user-attached images. One instance per TUI session.
    /// Thread-safe: every member takes the instance lock.
    /// </summary>
    public sealed class SessionImageStore
    {
        /// <summary>Folders untouched for longer than this are swept regardless of their pid
        /// (guards against pid reuse keeping a dead session's folder alive forever).</summary>
        public static readonly TimeSpan MaxAge = TimeSpan.FromDays(7);

        /// <summary>Name of the marker file holding the pid of the process that owns a folder.
        /// Needed because the folder name's pid is not always the writer's: a resumed session
        /// adopts an id minted by an earlier (now dead) process, and DEVMIND_TRACE_RUN_ID ids
        /// carry no pid at all.</summary>
        internal const string OwnerFileName = ".owner";

        /// <summary>Longest sanitized label kept in a file name.</summary>
        internal const int MaxLabelLength = 60;

        static readonly Regex PidInName = new Regex(@"-pid(\d+)", RegexOptions.CultureInvariant);

        readonly string _root;
        readonly Func<string> _sessionId;
        readonly object _gate = new object();

        string _counterSessionId; // session whose folder _counter belongs to
        int _counter;

        /// <param name="root">Root folder; null = <see cref="DevMindPaths.SessionImagesDir"/>
        /// (resolved per call, so its env-var seam is honoured live). Test seam.</param>
        /// <param name="sessionIdProvider">Current session id; null = <see cref="SessionId.Get"/>.
        /// Resolved per call, so a /new (SessionId.Reset) moves later saves to the new folder.</param>
        public SessionImageStore(string root = null, Func<string> sessionIdProvider = null)
        {
            _root = root;
            _sessionId = sessionIdProvider ?? SessionId.Get;
        }

        /// <summary>Root folder holding one sub-folder per session.</summary>
        public string Root => _root ?? DevMindPaths.SessionImagesDir;

        /// <summary>Folder for <paramref name="sessionId"/> (not created).</summary>
        public string GetSessionFolder(string sessionId) => Path.Combine(Root, sessionId);

        /// <summary>
        /// Decodes <paramref name="dataUri"/> (a <c>data:&lt;mime&gt;;base64,</c> URI, or raw
        /// base64) and writes it to the current session's folder as
        /// <c>NNN-&lt;sanitized label&gt;.&lt;ext&gt;</c>, NNN being a per-session counter.
        /// </summary>
        /// <returns>The saved file's absolute path, or null on any failure (logged, never thrown).</returns>
        public string Save(string dataUri, string label)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(dataUri))
                    return null;

                if (!TryDecode(dataUri, out string mime, out byte[] bytes))
                {
                    DevMindLog.Write("[SessionImageStore] Save skipped: payload is not decodable base64.");
                    return null;
                }

                string ext = ExtensionFor(mime, bytes);
                string name = SanitizeLabel(label);

                lock (_gate)
                {
                    string sid = _sessionId();
                    if (string.IsNullOrWhiteSpace(sid) || !IsSafeFolderName(sid))
                    {
                        DevMindLog.Write($"[SessionImageStore] Save skipped: unusable session id '{sid}'.");
                        return null;
                    }

                    string dir = GetSessionFolder(sid);
                    Directory.CreateDirectory(dir);
                    WriteOwnerMarker(dir);

                    if (!string.Equals(_counterSessionId, sid, StringComparison.Ordinal))
                    {
                        _counterSessionId = sid;
                        _counter = HighestCounter(dir);
                    }

                    // CreateNew never overwrites: a collision (another writer, a stale
                    // counter) just moves on to the next number.
                    for (int attempt = 0; attempt < 100; attempt++)
                    {
                        _counter++;
                        string path = Path.Combine(dir,
                            string.Format(CultureInfo.InvariantCulture, "{0:D3}-{1}{2}", _counter, name, ext));
                        try
                        {
                            using (var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                                fs.Write(bytes, 0, bytes.Length);
                            return Path.GetFullPath(path);
                        }
                        catch (IOException) when (File.Exists(path))
                        {
                            // taken — try the next number
                        }
                    }

                    DevMindLog.Write($"[SessionImageStore] Save failed: no free file name in {dir}.");
                    return null;
                }
            }
            catch (Exception ex)
            {
                DevMindLog.Write($"[SessionImageStore] Save failed: {ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }

        /// <summary>Deletes <paramref name="sessionId"/>'s folder recursively. Errors are logged and swallowed.</summary>
        public void DeleteSession(string sessionId)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(sessionId) || !IsSafeFolderName(sessionId))
                    return;

                lock (_gate)
                {
                    if (string.Equals(_counterSessionId, sessionId, StringComparison.Ordinal))
                        _counterSessionId = null; // recompute from disk on the next save

                    string dir = GetSessionFolder(sessionId);
                    if (Directory.Exists(dir))
                        Directory.Delete(dir, recursive: true);
                }
            }
            catch (Exception ex)
            {
                DevMindLog.Write($"[SessionImageStore] DeleteSession({sessionId}) failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>
        /// Startup cleanup. Deletes every session folder under <see cref="Root"/> except the
        /// current session's, when EITHER it was last written more than <see cref="MaxAge"/> ago,
        /// OR its owning pid (the <c>.owner</c> marker, else the <c>-pid&lt;N&gt;</c> in its name)
        /// is not a running process. A folder with no determinable pid is subject to the age
        /// rule only. Another live DevMind's folder is kept: its pid is running. Never throws.
        /// </summary>
        public void SweepOrphans()
        {
            try
            {
                string root = Root;
                if (!Directory.Exists(root))
                    return;

                string current = null;
                try { current = _sessionId(); } catch { /* fall through: still never delete an unknown current */ }
                DateTime cutoff = DateTime.UtcNow - MaxAge;

                foreach (string dir in Directory.GetDirectories(root))
                {
                    try
                    {
                        string name = Path.GetFileName(dir);
                        if (current == null || string.Equals(name, current, StringComparison.OrdinalIgnoreCase))
                            continue;

                        bool tooOld = Directory.GetLastWriteTimeUtc(dir) < cutoff;
                        bool dead = false;
                        if (!tooOld)
                        {
                            int? pid = ReadOwnerPid(dir) ?? ParsePidFromName(name);
                            dead = pid.HasValue && !IsProcessAlive(pid.Value);
                        }

                        if (tooOld || dead)
                        {
                            lock (_gate)
                                Directory.Delete(dir, recursive: true);
                        }
                    }
                    catch (Exception ex)
                    {
                        DevMindLog.Write($"[SessionImageStore] Sweep could not delete {dir}: {ex.GetType().Name}: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                DevMindLog.Write($"[SessionImageStore] Sweep failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        /// <summary>Splits a data URI into MIME + bytes; raw base64 yields a null MIME.</summary>
        internal static bool TryDecode(string dataUri, out string mime, out byte[] bytes)
        {
            mime = null;
            bytes = null;
            string payload = dataUri.Trim();
            if (payload.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                int comma = payload.IndexOf(',');
                if (comma < 0) return false;
                string header = payload.Substring(5, comma - 5);
                if (header.IndexOf(";base64", StringComparison.OrdinalIgnoreCase) < 0) return false;
                int semi = header.IndexOf(';');
                mime = (semi >= 0 ? header.Substring(0, semi) : header).Trim().ToLowerInvariant();
                payload = payload.Substring(comma + 1);
            }
            try
            {
                bytes = Convert.FromBase64String(payload);
                return true;
            }
            catch (FormatException)
            {
                return false;
            }
        }

        /// <summary>File extension for a MIME type; falls back to magic-byte sniffing, then ".img".</summary>
        internal static string ExtensionFor(string mime, byte[] bytes)
        {
            switch (mime)
            {
                case "image/png": return ".png";
                case "image/jpeg":
                case "image/jpg": return ".jpg";
                case "image/gif": return ".gif";
                case "image/webp": return ".webp";
                case "image/bmp":
                case "image/x-ms-bmp": return ".bmp";
            }
            if (bytes != null)
            {
                if (bytes.Length >= 4 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47) return ".png";
                if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF) return ".jpg";
                if (bytes.Length >= 3 && bytes[0] == (byte)'G' && bytes[1] == (byte)'I' && bytes[2] == (byte)'F') return ".gif";
                if (bytes.Length >= 2 && bytes[0] == (byte)'B' && bytes[1] == (byte)'M') return ".bmp";
                if (bytes.Length >= 12 && bytes[8] == (byte)'W' && bytes[9] == (byte)'E' && bytes[10] == (byte)'B' && bytes[11] == (byte)'P') return ".webp";
            }
            return ".img";
        }

        /// <summary>
        /// Reduces a label to safe file-name characters: letters, digits, '.', '-' and '_'
        /// kept; everything else becomes '_' (runs collapsed). A trailing image extension is
        /// dropped (the saved file gets its own from the MIME). Capped at
        /// <see cref="MaxLabelLength"/>; empty becomes "image".
        /// </summary>
        internal static string SanitizeLabel(string label)
        {
            string s = (label ?? "").Trim();
            string ext = Path.GetExtension(s).ToLowerInvariant();
            if (ext == ".png" || ext == ".jpg" || ext == ".jpeg" || ext == ".gif" || ext == ".webp" || ext == ".bmp")
                s = s.Substring(0, s.Length - ext.Length);

            var sb = new StringBuilder(s.Length);
            foreach (char c in s)
            {
                bool ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9')
                          || c == '.' || c == '-' || c == '_';
                char o = ok ? c : '_';
                if (o == '_' && sb.Length > 0 && sb[sb.Length - 1] == '_') continue;
                sb.Append(o);
            }

            string result = sb.ToString().Trim('_', '.');
            if (result.Length > MaxLabelLength)
                result = result.Substring(0, MaxLabelLength).TrimEnd('_', '.');
            return result.Length == 0 ? "image" : result;
        }

        /// <summary>A session id usable as one folder name (no separators, no traversal).</summary>
        static bool IsSafeFolderName(string name)
            => name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && name != "." && name != "..";

        /// <summary>Highest NNN prefix already in <paramref name="dir"/> (0 when none).</summary>
        static int HighestCounter(string dir)
        {
            int max = 0;
            foreach (string f in Directory.GetFiles(dir))
            {
                string n = Path.GetFileName(f);
                int dash = n.IndexOf('-');
                if (dash > 0 && int.TryParse(n.Substring(0, dash), NumberStyles.None, CultureInfo.InvariantCulture, out int v) && v > max)
                    max = v;
            }
            return max;
        }

        static void WriteOwnerMarker(string dir)
        {
            string marker = Path.Combine(dir, OwnerFileName);
            string pid = Environment.ProcessId.ToString(CultureInfo.InvariantCulture);
            try
            {
                if (!File.Exists(marker) || File.ReadAllText(marker).Trim() != pid)
                    File.WriteAllText(marker, pid);
            }
            catch (Exception ex)
            {
                // The name's pid (or the age rule) still governs the sweep.
                DevMindLog.Write($"[SessionImageStore] Could not write owner marker in {dir}: {ex.Message}");
            }
        }

        static int? ReadOwnerPid(string dir)
        {
            try
            {
                string marker = Path.Combine(dir, OwnerFileName);
                if (File.Exists(marker)
                    && int.TryParse(File.ReadAllText(marker).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int pid))
                    return pid;
            }
            catch { /* unreadable marker: fall back to the name */ }
            return null;
        }

        internal static int? ParsePidFromName(string name)
        {
            var m = PidInName.Match(name ?? "");
            if (m.Success && int.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int pid))
                return pid;
            return null;
        }

        static bool IsProcessAlive(int pid)
        {
            if (pid == Environment.ProcessId) return true;
            try
            {
                using (var p = Process.GetProcessById(pid))
                    return !p.HasExited;
            }
            catch (ArgumentException)
            {
                return false; // no such process
            }
            catch
            {
                return true; // can't tell (access denied etc.) — keep the folder; the age rule still applies
            }
        }
    }
}
