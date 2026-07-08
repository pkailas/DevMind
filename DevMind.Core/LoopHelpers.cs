// File: LoopHelpers.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace DevMind
{
    /// <summary>
    /// Pure static helpers extracted from the agentic loop driver.
    /// No WPF or VS-SDK dependencies — safe for use in DevMind.Core.
    /// </summary>
    public static class LoopHelpers
    {
        private static string _cachedMSBuildPath;

        /// <summary>
        /// Returns true if the shell command is a run/exec invocation (not a build command).
        /// These commands complete a task when they exit 0 and should not trigger agentic continuation.
        /// </summary>
        public static bool IsRunOrExecCommand(string command)
        {
            if (string.IsNullOrEmpty(command))
                return false;
            string cmd = command.Trim().ToLowerInvariant();
            return cmd.Contains("dotnet run")
                || cmd.Contains("dotnet exec")
                || (cmd.EndsWith(".exe") && !cmd.Contains("msbuild") && !cmd.Contains("dotnet build"));
        }

        /// <summary>
        /// After the executor processes tool calls, injects tool result messages into
        /// conversation history so the model sees the outcome on the next turn.
        /// </summary>
        public static void InjectToolResultMessages(ILlmClient llmClient,
            List<ToolCallResult> toolCalls, ExecutionResult result, List<ResponseBlock> executedBlocks)
        {
            foreach (var tc in toolCalls)
            {
                string resultContent = BuildToolResultContent(tc, result, executedBlocks);
                llmClient.AddToolResultMessage(tc.Id, resultContent, tc.Name);
            }
        }

        /// <summary>
        /// Builds the result content string for a single tool call based on the execution result.
        /// </summary>
        public static string BuildToolResultContent(ToolCallResult tc, ExecutionResult result,
            List<ResponseBlock> executedBlocks)
        {
            switch (tc.Name)
            {
                case "read_file":
                case "grep_file":
                case "diff_file":
                    {
                        string key = tc.Arguments?.TryGetValue("filename", out string fn) == true ? fn : null;
                        if (key != null && result.ToolResultContents != null &&
                            result.ToolResultContents.TryGetValue(key, out string fileContent))
                            return fileContent;
                        return "[File content not available]";
                    }

                case "find_in_files":
                    {
                        string key = tc.Arguments?.TryGetValue("glob", out string g) == true ? g : null;
                        if (key != null && result.ToolResultContents != null &&
                            result.ToolResultContents.TryGetValue(key, out string findContent))
                            return findContent;
                        return "[Search results not available]";
                    }

                case "list_files":
                    {
                        string key = tc.Arguments?.TryGetValue("glob", out string lg) == true ? lg : null;
                        if (key != null && result.ToolResultContents != null &&
                            result.ToolResultContents.TryGetValue(key, out string listContent))
                            return listContent;
                        return "[ERROR: list_files result not captured]";
                    }

                case "patch_file":
                    if (result.PatchedPaths != null && result.PatchedPaths.Count > 0)
                    {
                        string header = $"[PATCH applied to {string.Join(", ", result.PatchedPaths)}]";
                        // Append any post-patch context echoes (fresh view around each edit) so the
                        // model can patch again without a separate READ. Keyed by full path.
                        var echoes = new StringBuilder();
                        foreach (var p in result.PatchedPaths.Distinct())
                            if (result.ToolResultContents != null &&
                                result.ToolResultContents.TryGetValue(p, out string ctx) &&
                                !string.IsNullOrEmpty(ctx))
                                echoes.Append('\n').Append(ctx);
                        return echoes.Length > 0 ? header + "\n" + echoes.ToString().TrimStart('\n') : header;
                    }
                    if (result.Errors != null && result.Errors.Count > 0)
                        return $"[PATCH-FAILED: {string.Join("; ", result.Errors)}]";
                    return "[PATCH processed]";

                case "create_file":
                    if (result.FilesCreated != null && result.FilesCreated.Count > 0)
                        return $"[File created: {string.Join(", ", result.FilesCreated)}]";
                    return "[File created]";

                case "append_file":
                    if (result.FilesAppended != null && result.FilesAppended.Count > 0)
                        return $"[Content appended to {string.Join(", ", result.FilesAppended)}]";
                    return "[Content appended]";

                case "run_shell":
                case "run_build":
                    if (!string.IsNullOrEmpty(result.ShellOutput))
                        return result.ShellOutput;
                    if (result.Errors != null && result.Errors.Count > 0)
                        return $"[Shell failed: {string.Join("; ", result.Errors)}]";
                    return result.ShellExitCode.HasValue
                        ? $"[Shell exited with code {result.ShellExitCode}]"
                        : "[Shell command executed]";

                case "run_tests":
                    return !string.IsNullOrEmpty(result.ShellOutput)
                        ? result.ShellOutput
                        : "[Tests executed]";

                case "delete_file":
                    if (result.FilesDeleted != null && result.FilesDeleted.Count > 0)
                        return $"[Deleted: {string.Join(", ", result.FilesDeleted)}]";
                    return "[File deleted]";

                case "rename_file":
                    if (result.FilesRenamed != null && result.FilesRenamed.Count > 0)
                        return $"[Renamed: {string.Join(", ", result.FilesRenamed)}]";
                    return "[File renamed]";

                case "scratchpad":
                    return "[Scratchpad updated]";

                case "task_done":
                    return "[Task complete]";

                case "recall_memory":
                    {
                        var memBlock = executedBlocks?.FirstOrDefault(b => b.Type == BlockType.RecallMemory);
                        return memBlock?.MemoryContent ?? "[Topic not found]";
                    }

                case "save_memory":
                    {
                        var memBlock = executedBlocks?.FirstOrDefault(b => b.Type == BlockType.SaveMemory);
                        return memBlock?.MemoryDescription ?? "[Memory saved]";
                    }

                case "list_memory_topics":
                    {
                        var memBlock = executedBlocks?.FirstOrDefault(b => b.Type == BlockType.ListMemory);
                        return memBlock?.MemoryContent ?? "[No memory topics found]";
                    }

                case "get_diagnostics":
                case "go_to_definition":
                case "find_references":
                case "hover":
                    {
                        string key = tc.Arguments?.TryGetValue("filename", out string lf) == true ? lf : null;
                        if (key != null && result.ToolResultContents != null &&
                            result.ToolResultContents.TryGetValue(key, out string lspContent))
                            return lspContent;
                        return "[LSP result not available]";
                    }

                case "find_symbol":
                case "web_search":
                    {
                        string key = tc.Arguments?.TryGetValue("query", out string q) == true ? q : null;
                        if (key != null && result.ToolResultContents != null &&
                            result.ToolResultContents.TryGetValue(key, out string queryContent))
                            return queryContent;
                        return "[Search results not available]";
                    }

                case "web_fetch":
                    {
                        string key = tc.Arguments?.TryGetValue("url", out string u) == true ? u : null;
                        if (key != null && result.ToolResultContents != null &&
                            result.ToolResultContents.TryGetValue(key, out string fetchContent))
                            return fetchContent;
                        return "[Fetched content not available]";
                    }

                case "run_sql":
                    {
                        // AgenticExecutor stores the rendered table / file-spill message / error text
                        // under the literal "run_sql" key. Hand that back as the tool_result so the
                        // model actually receives the rows (mirrors run_shell returning ShellOutput),
                        // instead of the generic "[Executed]" that made it think queries returned nothing.
                        if (result.ToolResultContents != null &&
                            result.ToolResultContents.TryGetValue("run_sql", out string sqlContent) &&
                            !string.IsNullOrEmpty(sqlContent))
                            return sqlContent;
                        if (result.Errors != null && result.Errors.Count > 0)
                            return $"[SQL failed: {string.Join("; ", result.Errors)}]";
                        return "[SQL result not available]";
                    }

                case "search_memory":
                    {
                        if (result.ToolResultContents != null &&
                            result.ToolResultContents.TryGetValue("search_memory", out string searchContent) &&
                            !string.IsNullOrEmpty(searchContent))
                            return searchContent;
                        return "[Memory search result not available]";
                    }

                case "query_library":
                    {
                        if (result.ToolResultContents != null &&
                            result.ToolResultContents.TryGetValue("query_library", out string libraryContent) &&
                            !string.IsNullOrEmpty(libraryContent))
                            return libraryContent;
                        return "[Library query result not available]";
                    }

                case "recall_cache":
                    {
                        if (result.ToolResultContents != null &&
                            result.ToolResultContents.TryGetValue("recall_cache", out string recallContent) &&
                            !string.IsNullOrEmpty(recallContent))
                            return recallContent;
                        if (result.Errors != null && result.Errors.Count > 0)
                            return $"[Recall failed: {string.Join("; ", result.Errors)}]";
                        return "[Recall result not available]";
                    }

                case "debug":
                    {
                        // AgenticExecutor stores the debug status/output line under the "debug" key.
                        // Hand it back as the tool_result so the model sees the breakpoint/stack/eval
                        // output instead of a generic "[Executed]".
                        if (result.ToolResultContents != null &&
                            result.ToolResultContents.TryGetValue("debug", out string debugContent) &&
                            !string.IsNullOrEmpty(debugContent))
                            return debugContent;
                        if (result.Errors != null && result.Errors.Count > 0)
                            return $"[Debug failed: {string.Join("; ", result.Errors)}]";
                        return "[Debug result not available]";
                    }

                default:
                    return "[Executed]";
            }
        }

        /// <summary>
        /// Builds the runtime tool-use section of the system prompt.
        /// </summary>
        public static string BuildToolUsePrompt(string buildCommand, string projectNamespace)
        {
            var sb = new StringBuilder();

            sb.Append("## Tool Catalog\n");
            sb.Append("Reach for the right tool for each step:\n");
            sb.Append("- Discovery (\"what files exist?\"): list_files with a glob pattern\n");
            sb.Append("- Content search across files: find_in_files\n");
            sb.Append("- Content search in a known file: grep_file\n");
            sb.Append("- Reading a file: read_file (large files return an outline first; use start_line/end_line for targeted reads)\n");
            sb.Append("- Cumulative diff of changes made to a file during the current session: diff_file\n");
            sb.Append("- Editing an existing file: patch_file\n");
            sb.Append("- Creating a new file: create_file\n");
            sb.Append("- Running build: run_build\n");
            sb.Append("- Running tests: run_tests\n");
            sb.Append("- Interactive debugging (DAP via netcoredbg): debug — command launch|attach|break|clear_breaks|continue|step|stepin|stepout|inspect|stack|eval|detach|stop, with command-specific args (also the /debug slash command)\n");
            sb.Append("- Tracking state: scratchpad\n");
            sb.Append("- Saving cross-session knowledge: save_memory / recall_memory / list_memory_topics / search_memory (grep across all topics)\n");
            sb.Append("- Reference lookup (RAG over ingested docs): query_library — framework/API questions (hooks rules, TS patterns) before guessing\n");
            sb.Append("- Code intelligence (semantic): get_diagnostics (errors without a build), go_to_definition, find_references, hover, find_symbol (solution-wide symbol search)\n");
            sb.Append("- Web: web_search (docs, APIs, error messages), web_fetch (read a URL as text)\n");
            sb.Append("- Finishing: task_done\n\n");

            sb.Append("## Termination Contract\n");
            sb.Append("Every response must either contain a tool call OR call task_done. Free-form prose without a tool call is never a valid completion.\n");
            sb.Append("After you have an answer or have finished a code change, your final action must be task_done with the answer or summary in the summary parameter.\n");
            sb.Append("Do not type a final answer as prose and stop — that is an abandoned task, not a completion.\n\n");

            sb.Append("## Path Format\n");
            sb.Append("All file-path arguments to read_file, patch_file, create_file, grep_file, delete_file, diff_file, append_file, and rename_file must be absolute paths.\n");
            sb.Append("When list_files or find_in_files returns paths, pass those exact strings to read_file — do not shorten to just the filename.\n\n");

            sb.Append("## Large Output & Reports\n");
            sb.Append("When you need to emit a large report, analysis, log, or scratch artifact that is NOT source code belonging in the project, write it with create_file under the dedicated output directory:\n");
            sb.Append($"  {BufferedAgenticHost.OutputDirectory}\n");
            sb.Append("Never write such files into the working tree — they get committed as clutter. State the written path in your task_done summary so the operator can retrieve it.\n\n");

            sb.Append("## Build Verification\n");
            if (!string.IsNullOrWhiteSpace(buildCommand))
            {
                sb.Append($"Build command: {buildCommand}\n");
                sb.Append("After ANY code change (patch_file or create_file), call run_build to verify the build still passes.\n\n");
            }
            else
            {
                sb.Append("Build command: none detected in the working directory — run_build is unavailable.\n");
                sb.Append("After ANY code change (patch_file or create_file), verify the build with run_shell and an explicit build command if one applies.\n\n");
            }

            sb.Append("## Editing Workflow\n");
            sb.Append("Call read_file before patch_file if you have not seen the file. The find argument to patch_file must be copied verbatim from read_file output — never reconstructed from memory.\n");
            sb.Append("Do not call read_file on the same file multiple times. If you have an outline and a line range, that is sufficient context to write a patch_file. Act immediately.\n\n");

            sb.Append("## Large File Strategy\n");
            sb.Append("For files over 100 lines, read_file returns an outline (types, methods, signatures with line numbers) instead of full content.\n");
            sb.Append("1. First read_file gets the outline.\n");
            sb.Append("2. Use the outline to identify the exact line range you need.\n");
            sb.Append("3. Call read_file with start_line and end_line for just that section.\n");
            sb.Append("4. Call patch_file using only the content from that range.\n");
            sb.Append("Do not set force_full=true on a large file unless explicitly asked. Work outline → range → patch.\n\n");

            sb.Append("## Error Handling\n");
            sb.Append("When a tool returns an error, read the error message before retrying. Do not retry the same call with minor variations of the same argument — diagnose first, then either fix the argument substantively or switch to a different tool.\n\n");

            sb.Append("## Action Discipline\n");
            sb.Append("After read_file, find_in_files, or grep_file returns content, act on it in the same overall task. Never call only read-style tools and stop without progress.\n");
            sb.Append("Every turn during a code-change task must include at least one mutating tool call (patch_file, create_file, run_build, run_tests, run_shell) unless you are answering a question that requires no code change.\n");

            sb.Append("\n## Scratchpad Discipline\n");
            sb.Append("During any multi-step agentic task, update the scratchpad at every major decision point with: current goal, key decisions made so far, next planned step, and any open questions. Do this before and after each significant tool call sequence. The scratchpad is your memory across compaction — treat it as the source of truth for task state.\n");
            sb.Append("Before any context compaction or when context usage exceeds 70%, write a compact task checkpoint to the scratchpad summarizing: what you were asked to do, what is done, what remains, and any critical constraints discovered.\n");

            if (!string.IsNullOrEmpty(projectNamespace))
                sb.Append($"\n## Namespace\nWhen creating new files, use the namespace '{projectNamespace}'.");

            return sb.ToString();
        }

        /// <summary>
        /// Discovers MSBuild.exe at runtime. Checks VSINSTALLDIR env var, then vswhere.exe,
        /// then known VS installation directories. Caches the result for the session.
        /// </summary>
        public static string FindMSBuildPath()
        {
            if (_cachedMSBuildPath != null)
                return _cachedMSBuildPath;

            // 1. Check VSINSTALLDIR environment variable
            var vsInstallDir = Environment.GetEnvironmentVariable("VSINSTALLDIR");
            if (!string.IsNullOrEmpty(vsInstallDir))
            {
                var candidate = System.IO.Path.Combine(vsInstallDir, "MSBuild", "Current", "Bin", "MSBuild.exe");
                if (System.IO.File.Exists(candidate))
                {
                    _cachedMSBuildPath = candidate;
                    return _cachedMSBuildPath;
                }
            }

            // 2. Try vswhere.exe
            var vswherePath = @"C:\Program Files (x86)\Microsoft Visual Studio\Installer\vswhere.exe";
            if (System.IO.File.Exists(vswherePath))
            {
                try
                {
                    var psi = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = vswherePath,
                        Arguments = "-latest -requires Microsoft.Component.MSBuild -find MSBuild\\**\\Bin\\MSBuild.exe",
                        RedirectStandardOutput = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    using (var proc = System.Diagnostics.Process.Start(psi))
                    {
                        var output = proc.StandardOutput.ReadToEnd();
                        proc.WaitForExit(5000);
                        var firstLine = output?.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                        if (!string.IsNullOrEmpty(firstLine) && System.IO.File.Exists(firstLine))
                        {
                            _cachedMSBuildPath = firstLine;
                            return _cachedMSBuildPath;
                        }
                    }
                }
                catch
                {
                    // vswhere failed — fall through to directory scan
                }
            }

            // 3. Scan known VS installation directories
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var versions = new[] { "18", "2022", "2019" };
            var editions = new[] { "Enterprise", "Professional", "Community", "BuildTools" };
            foreach (var ver in versions)
            {
                foreach (var edition in editions)
                {
                    var candidate = System.IO.Path.Combine(programFiles, "Microsoft Visual Studio",
                        ver, edition, "MSBuild", "Current", "Bin", "MSBuild.exe");
                    if (System.IO.File.Exists(candidate))
                    {
                        _cachedMSBuildPath = candidate;
                        return _cachedMSBuildPath;
                    }
                }
            }

            // 4. Last resort — bare msbuild, hope it's on PATH
            _cachedMSBuildPath = "msbuild";
            return _cachedMSBuildPath;
        }
    }
}
