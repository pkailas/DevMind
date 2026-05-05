// File: McpServices.cs  v1.1
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Diagnostic policy: all Console.Write / Console.WriteLine calls in this project
// MUST target Console.Error (stderr). stdout is reserved exclusively for the
// JSON-RPC message framing of the stdio MCP transport — any write to stdout
// outside the transport layer will corrupt the client connection.

using System;
using System.Collections.Generic;
using System.IO;
using DevMind;

namespace DevMind.McpServer
{
    /// <summary>
    /// Session-scoped DI container for MCP server state.
    /// One instance per server process (stdio = one client connection lifetime).
    /// Owns the ShellRunner, FileContentCache, and MemoryManager shared by all tool methods.
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
    }
}
