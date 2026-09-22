// File: ServerRootArgs.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Command-line root parsing for the MCP server, split out of Program.cs so it can be
// tested without launching a host.
//
// Two distinct things used to be one: where the server is rooted — shell cwd, memory,
// FilePathResolver defaults, and the LSP solution scope — and what an agent may modify.
// The first --dir supplied both, so widening the root to get sane symbol scoping also
// widened write access.
//
//   --root <dir>   sets the working directory and NOTHING else.
//   --dir  <dir>   repeatable. Without --root the first one is also the working
//                  directory (the historical shape); with --root present every one of
//                  them is purely a write root.
//
// Backward compatibility: when --root is absent the result is byte-for-byte what the
// old inline parser produced, startup lines included.

#nullable enable

namespace DevMind.McpServer
{
    /// <summary>
    /// Outcome of parsing the root-related command line and environment.
    /// <see cref="Error"/> non-null means the process must print it and exit 2;
    /// every other member is meaningless in that case.
    /// </summary>
    internal sealed class ServerRootArgs
    {
        /// <summary>Primary working directory: shell cwd, memory, defaults, LSP scope.</summary>
        public string WorkingDirectory { get; }

        /// <summary>Write roots gathered from --dir and DEVMIND_ALLOWED_WRITE_ROOTS.</summary>
        public IReadOnlyList<string> AdditionalWriteRoots { get; }

        /// <summary>
        /// False only when --root was used: the working directory is then a location,
        /// not a permission, and must not enter the write-root baseline.
        /// </summary>
        public bool WorkingDirectoryIsWriteRoot { get; }

        /// <summary>Stderr lines to emit in order (warnings plus the startup banner).</summary>
        public IReadOnlyList<string> Messages { get; }

        /// <summary>Fatal parse error, or null when parsing succeeded.</summary>
        public string? Error { get; }

        private ServerRootArgs(string workingDirectory, IReadOnlyList<string> additionalWriteRoots,
            bool workingDirectoryIsWriteRoot, IReadOnlyList<string> messages, string? error)
        {
            WorkingDirectory = workingDirectory;
            AdditionalWriteRoots = additionalWriteRoots;
            WorkingDirectoryIsWriteRoot = workingDirectoryIsWriteRoot;
            Messages = messages;
            Error = error;
        }

        /// <summary>The effective write roots, working directory included when it grants write.</summary>
        public IReadOnlyList<string> EffectiveWriteRoots
        {
            get
            {
                var all = new List<string>();
                if (WorkingDirectoryIsWriteRoot)
                    all.Add(WorkingDirectory);
                all.AddRange(AdditionalWriteRoots);
                return all;
            }
        }

        private static ServerRootArgs Fail(string error) =>
            new ServerRootArgs(string.Empty, Array.Empty<string>(), true, Array.Empty<string>(), error);

        private static string NotAbsolute(string flag, string rawValue) =>
            $"[McpServer] Error: {flag} must be an absolute path. Received: '{rawValue}'. " +
            "Common cause: backslash escape issues in argument passing (e.g., MCP Inspector " +
            "on Windows may strip 'C:\\' before \\t). Try forward slashes (C:/path), " +
            "doubled backslashes (C:\\\\path), or wrapping the path in quotes.";

        /// <summary>
        /// Parses --root/--dir out of <paramref name="args"/> and merges the
        /// semicolon-separated <paramref name="envWriteRootsRaw"/>
        /// (DEVMIND_ALLOWED_WRITE_ROOTS) into the write roots.
        /// </summary>
        public static ServerRootArgs Parse(string[]? args, string? envWriteRootsRaw)
        {
            args ??= Array.Empty<string>();

            string? rootValue = null;
            var dirValues = new List<string>();

            for (int i = 0; i < args.Length - 1; i++)
            {
                bool isRoot = string.Equals(args[i], "--root", StringComparison.OrdinalIgnoreCase);
                bool isDir  = string.Equals(args[i], "--dir",  StringComparison.OrdinalIgnoreCase);
                if (!isRoot && !isDir)
                    continue;

                string rawValue = args[i + 1];
                string flag = isRoot ? "--root" : "--dir";

                if (!string.IsNullOrWhiteSpace(rawValue) && !Path.IsPathRooted(rawValue))
                    return Fail(NotAbsolute(flag, rawValue));

                if (isRoot)
                {
                    // A second --root names a second place to be rooted, and there is no
                    // sane tie-break: silently keeping one would root the server in a tree
                    // the operator did not mean. Refuse instead of guessing.
                    if (rootValue != null)
                        return Fail(
                            "[McpServer] Error: --root was given more than once " +
                            $"('{rootValue}' then '{rawValue}'). The server has exactly one working " +
                            "directory; pass --root once and use --dir for additional write roots.");
                    rootValue = rawValue;
                }
                else
                {
                    dirValues.Add(rawValue);
                }
            }

            string workingDirectory;
            bool workingDirectoryIsWriteRoot;
            var additionalWriteRoots = new List<string>();

            if (rootValue != null)
            {
                // --root claims the working directory, so no --dir is consumed as the
                // primary: every one of them stays a write root and nothing else.
                workingDirectory = rootValue;
                workingDirectoryIsWriteRoot = false;
                additionalWriteRoots.AddRange(dirValues);
            }
            else if (dirValues.Count > 0)
            {
                workingDirectory = dirValues[0];
                workingDirectoryIsWriteRoot = true;
                for (int i = 1; i < dirValues.Count; i++)
                    additionalWriteRoots.Add(dirValues[i]);
            }
            else
            {
                workingDirectory = Environment.CurrentDirectory;
                workingDirectoryIsWriteRoot = true;
            }

            var messages = new List<string>();

            // DEVMIND_ALLOWED_WRITE_ROOTS (semicolon-separated absolute paths).
            if (!string.IsNullOrWhiteSpace(envWriteRootsRaw))
            {
                var envEntries = envWriteRootsRaw.Split(';',
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                foreach (var entry in envEntries)
                {
                    if (string.IsNullOrWhiteSpace(entry))
                        continue;

                    if (!Path.IsPathRooted(entry))
                    {
                        messages.Add($"[McpServer] Warning: DEVMIND_ALLOWED_WRITE_ROOTS entry is not absolute, skipping: '{entry}'");
                        continue;
                    }

                    if (!Directory.Exists(entry) && !File.Exists(entry))
                    {
                        messages.Add($"[McpServer] Warning: DEVMIND_ALLOWED_WRITE_ROOTS entry does not exist, skipping: '{entry}'");
                        continue;
                    }

                    additionalWriteRoots.Add(entry);
                }
            }

            if (rootValue == null)
            {
                // Historical wording, unchanged: existing setups read these lines.
                if (additionalWriteRoots.Count > 0)
                    messages.Add($"[McpServer] Starting. Working directory: {workingDirectory} | Additional write roots: {string.Join("; ", additionalWriteRoots)}");
                else
                    messages.Add($"[McpServer] Starting. Working directory: {workingDirectory}");
            }
            else if (additionalWriteRoots.Count > 0)
            {
                messages.Add($"[McpServer] Starting. Working directory (--root, not writable): {workingDirectory} | Write roots: {string.Join("; ", additionalWriteRoots)}");
            }
            else
            {
                // A read-only server is a legitimate configuration — reads were never
                // restricted, so this is the only way to browse a tree without also being
                // able to change it. It just has to say so, because "every write is
                // refused" with no explanation is indistinguishable from a bug.
                messages.Add($"[McpServer] Starting. Working directory (--root, not writable): {workingDirectory}");
                messages.Add(
                    "[McpServer] READ-ONLY: --root was given with no --dir, so no write root came from the " +
                    "command line and every file create/modify/delete/rename will be refused. Reads are " +
                    "unaffected. To grant write access, pass --dir <absolute-path>, or add the directory to " +
                    "\"allowedWriteRoots\" in %APPDATA%\\devmind\\devmind.json (picked up at startup and by " +
                    "reload_write_roots).");
            }

            return new ServerRootArgs(workingDirectory, additionalWriteRoots,
                workingDirectoryIsWriteRoot, messages, error: null);
        }
    }
}
