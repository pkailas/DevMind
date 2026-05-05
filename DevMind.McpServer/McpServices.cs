// File: McpServices.cs  v1.2
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Diagnostic policy: all Console.Write / Console.WriteLine calls in this project
// MUST target Console.Error (stderr). stdout is reserved exclusively for the
// JSON-RPC message framing of the stdio MCP transport — any write to stdout
// outside the transport layer will corrupt the client connection.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using DevMind;

namespace DevMind.McpServer
{
    /// <summary>
    /// Session-scoped DI container for MCP server state.
    /// One instance per server process (stdio = one client connection lifetime).
    /// Owns the ShellRunner, FileContentCache, MemoryManager, and session-level
    /// tracking dictionaries shared by all tool methods.
    /// </summary>
    internal sealed class McpServices
    {
        public string WorkingDirectory { get; }
        public MemoryManager          Memory    { get; }
        public FileContentCache       FileCache { get; }
        public ShellRunner            Shell     { get; }

        /// <summary>
        /// Tracks filenames (basename only) read during this session.
        /// Controls outline-vs-full behaviour: a re-read file gets an outline
        /// unless force_full is set.
        /// </summary>
        public HashSet<string> FilesRead { get; } =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Session-baseline snapshots: absolute path → original content captured at first read
        // or first patch. Powers diff_file. Locked for defensive thread safety even though
        // stdio MCP is single-threaded in practice.
        private readonly Dictionary<string, string> _fileSnapshots =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly object _snapshotLock = new object();

        public McpServices(string workingDirectory)
        {
            WorkingDirectory = string.IsNullOrWhiteSpace(workingDirectory)
                ? Environment.CurrentDirectory
                : Path.GetFullPath(workingDirectory);

            if (!Directory.Exists(WorkingDirectory))
                Console.Error.WriteLine(
                    $"[McpServer] Warning: working directory does not exist: {WorkingDirectory}");

            Memory    = new MemoryManager(WorkingDirectory);
            FileCache = new FileContentCache();
            Shell     = new ShellRunner(WorkingDirectory);
        }

        /// <summary>
        /// Captures the current on-disk content of <paramref name="fullPath"/> into
        /// _fileSnapshots only if it has not already been snapshotted this session.
        /// No-op if the file does not exist or cannot be read.
        /// </summary>
        public void TrySnapshot(string fullPath)
        {
            lock (_snapshotLock)
            {
                if (_fileSnapshots.ContainsKey(fullPath)) return;
            }

            string? content = null;
            try
            {
                if (File.Exists(fullPath))
                    content = File.ReadAllText(fullPath, Encoding.UTF8);
            }
            catch { }

            lock (_snapshotLock)
            {
                // Re-check inside lock in case another call raced us.
                if (!_fileSnapshots.ContainsKey(fullPath))
                    _fileSnapshots[fullPath] = content ?? string.Empty;
            }
        }

        /// <summary>
        /// Returns true and sets <paramref name="snapshot"/> if the file was snapshotted
        /// this session. Returns false if no snapshot exists.
        /// </summary>
        public bool TryGetSnapshot(string fullPath, out string snapshot)
        {
            lock (_snapshotLock)
            {
                return _fileSnapshots.TryGetValue(fullPath, out snapshot!);
            }
        }

        /// <summary>
        /// Moves a snapshot entry from <paramref name="oldPath"/> to <paramref name="newPath"/>.
        /// Called after rename_file to preserve diff history under the new name.
        /// </summary>
        public void MoveSnapshot(string oldPath, string newPath)
        {
            lock (_snapshotLock)
            {
                if (_fileSnapshots.TryGetValue(oldPath, out string? snap))
                {
                    _fileSnapshots.Remove(oldPath);
                    _fileSnapshots[newPath] = snap;
                }
            }
        }
    }
}
