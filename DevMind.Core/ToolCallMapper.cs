// File: ToolCallMapper.cs  v7.4
// Copyright (c) iOnline Consulting LLC. All rights reserved.

using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json.Linq;

namespace DevMind
{
    /// <summary>
    /// Converts a list of <see cref="ToolCallResult"/> into <see cref="ResponseBlock"/> instances
    /// compatible with the existing classify → decide → execute pipeline.
    /// </summary>
    public static class ToolCallMapper
    {
        /// <summary>
        /// Maps tool calls to response blocks. The <paramref name="buildCommand"/> is substituted
        /// for <c>run_build</c> tool calls.
        /// </summary>
        public static List<ResponseBlock> Map(List<ToolCallResult> toolCalls, string buildCommand)
        {
            var blocks = new List<ResponseBlock>();

            foreach (var tc in toolCalls)
            {
                var block = MapSingle(tc, buildCommand);
                if (block != null)
                    blocks.Add(block);
            }

            return blocks;
        }

        private static ResponseBlock MapSingle(ToolCallResult tc, string buildCommand)
        {
            switch (tc.Name)
            {
                case "read_file":
                    return new ResponseBlock
                    {
                        Type = BlockType.ReadRequest,
                        FileName = GetArg(tc, "filename"),
                        RangeStart = GetIntArg(tc, "start_line"),
                        RangeEnd = GetIntArg(tc, "end_line"),
                        ForceFullRead = GetBoolArg(tc, "force_full"),
                        FromToolCall = true
                    };

                case "create_file":
                    return new ResponseBlock
                    {
                        Type = BlockType.File,
                        FileName = GetArg(tc, "filename"),
                        Content = GetArg(tc, "content"),
                        FromToolCall = true
                    };

                case "append_file":
                    return new ResponseBlock
                    {
                        Type = BlockType.AppendFile,
                        FileName = GetArg(tc, "filename"),
                        Content = GetArg(tc, "content"),
                        FromToolCall = true
                    };

                case "patch_file":
                    {
                        // Reconstruct the PATCH text format expected by ApplyPatchAsync. One PATCH block
                        // carries every find/replace pair; PatchEngine resolves/applies them atomically.
                        // Format: PATCH filename\n(FIND:\n{find}\nREPLACE:\n{replace}\n)+ END_PATCH
                        string filename = GetArg(tc, "filename");

                        // Use explicit '\n' (not AppendLine, which emits CRLF on Windows and leaks a
                        // stray '\r' into the parsed replace text). Content's own newlines are preserved.
                        var sb = new StringBuilder();
                        sb.Append("PATCH ").Append(filename).Append('\n');

                        int pairCount = 0;
                        string editsJson = GetArg(tc, "edits");
                        if (!string.IsNullOrWhiteSpace(editsJson))
                        {
                            try
                            {
                                foreach (var item in JArray.Parse(editsJson))
                                {
                                    string f = item?["find"]?.ToString();
                                    if (string.IsNullOrEmpty(f)) continue;   // skip empty; PatchEngine would reject anyway
                                    string r = item?["replace"]?.ToString() ?? "";
                                    sb.Append("FIND:\n").Append(f).Append('\n');
                                    sb.Append("REPLACE:\n").Append(r).Append('\n');
                                    pairCount++;
                                }
                            }
                            catch { /* malformed edits — fall through to single find/replace */ }
                        }

                        if (pairCount == 0)
                        {
                            // Single-edit form (or no usable edits): fall back to top-level find/replace.
                            sb.Append("FIND:\n").Append(GetArg(tc, "find")).Append('\n');
                            sb.Append("REPLACE:\n").Append(GetArg(tc, "replace")).Append('\n');
                        }

                        sb.Append("END_PATCH");

                        return new ResponseBlock
                        {
                            Type = BlockType.Patch,
                            FileName = filename,
                            Content = sb.ToString(),
                            FromToolCall = true
                        };
                    }

               case "run_shell":
                    {
                        int timeoutVal = GetIntArg(tc, "timeout_seconds");
                        return new ResponseBlock
                        {
                            Type = BlockType.Shell,
                            Command = GetArg(tc, "command"),
                            ShellTimeoutSeconds = timeoutVal > 0 ? (int?)timeoutVal : null
                        };
                    }

                case "grep_file":
                    return new ResponseBlock
                    {
                        Type = BlockType.Grep,
                        Pattern = GetArg(tc, "pattern"),
                        FileName = GetArg(tc, "filename"),
                        RangeStart = GetIntArg(tc, "start_line"),
                        RangeEnd = GetIntArg(tc, "end_line")
                    };

                case "find_in_files":
                    return new ResponseBlock
                    {
                        Type = BlockType.Find,
                        Pattern = GetArg(tc, "pattern"),
                        GlobPattern = GetArg(tc, "glob"),
                        RangeStart = GetIntArg(tc, "start_line"),
                        RangeEnd = GetIntArg(tc, "end_line")
                    };

                case "delete_file":
                    return new ResponseBlock
                    {
                        Type = BlockType.Delete,
                        FileName = GetArg(tc, "filename")
                    };

                case "rename_file":
                    return new ResponseBlock
                    {
                        Type = BlockType.Rename,
                        RenameFrom = GetArg(tc, "old_filename"),
                        RenameTo = GetArg(tc, "new_filename")
                    };

                case "diff_file":
                    return new ResponseBlock
                    {
                        Type = BlockType.Diff,
                        FileName = GetArg(tc, "filename")
                    };

               case "run_tests":
                    {
                        int testTimeoutVal = GetIntArg(tc, "timeout_seconds");
                        return new ResponseBlock
                        {
                            Type = BlockType.Test,
                            TestProject = GetArg(tc, "project"),
                            TestFilter = GetArg(tc, "filter"),
                            TestTimeoutSeconds = testTimeoutVal > 0 ? (int?)testTimeoutVal : null
                        };
                    }

                case "scratchpad":
                    return new ResponseBlock
                    {
                        Type = BlockType.Scratchpad,
                        Content = GetArg(tc, "content")
                    };

                case "task_done":
                    return new ResponseBlock
                    {
                        Type = BlockType.Done,
                        Content = GetArg(tc, "summary")
                    };

                case "ask_caller":
                    return new ResponseBlock
                    {
                        Type = BlockType.NeedsInput,
                        Content = "NEEDS INPUT — questions for the caller:\n"
                                  + GetArg(tc, "questions")
                                  + "\n\nAlready tried:\n"
                                  + GetArg(tc, "tried")
                    };

                case "run_build":
                    return new ResponseBlock
                    {
                        Type = BlockType.Shell,
                        Command = buildCommand
                    };

                case "recall_memory":
                    return new ResponseBlock
                    {
                        Type = BlockType.RecallMemory,
                        MemoryTopic = GetArg(tc, "topic")
                    };

                case "save_memory":
                    return new ResponseBlock
                    {
                        Type = BlockType.SaveMemory,
                        MemoryTopic = GetArg(tc, "topic"),
                        MemoryContent = GetArg(tc, "content"),
                        MemoryDescription = GetArg(tc, "description")
                    };

                case "list_memory_topics":
                    return new ResponseBlock
                    {
                        Type = BlockType.ListMemory
                    };

                case "search_memory":
                    return new ResponseBlock
                    {
                        Type = BlockType.SearchMemory,
                        MemorySearchPattern = GetArg(tc, "pattern")
                    };

                case "query_library":
                    return new ResponseBlock
                    {
                        Type = BlockType.QueryLibrary,
                        LibraryQuestion = GetArg(tc, "question"),
                        LibraryTopK = GetIntArg(tc, "top_k")
                    };

                case "list_files":
                    return new ResponseBlock
                    {
                        Type = BlockType.ListFiles,
                        ListFilesGlob = GetArg(tc, "glob"),
                        ListFilesRecursive = tc.Arguments?.ContainsKey("recursive") != true || GetBoolArg(tc, "recursive")
                    };

                case "get_diagnostics":
                    return new ResponseBlock
                    {
                        Type = BlockType.GetDiagnostics,
                        FileName = GetArg(tc, "filename")
                    };

                case "go_to_definition":
                    return new ResponseBlock
                    {
                        Type = BlockType.GoToDefinition,
                        FileName = GetArg(tc, "filename"),
                        LspLine = GetIntArg(tc, "line"),
                        LspCharacter = GetIntArg(tc, "character")
                    };

                case "find_references":
                    return new ResponseBlock
                    {
                        Type = BlockType.FindReferences,
                        FileName = GetArg(tc, "filename"),
                        LspLine = GetIntArg(tc, "line"),
                        LspCharacter = GetIntArg(tc, "character")
                    };

                case "hover":
                    return new ResponseBlock
                    {
                        Type = BlockType.Hover,
                        FileName = GetArg(tc, "filename"),
                        LspLine = GetIntArg(tc, "line"),
                        LspCharacter = GetIntArg(tc, "character")
                    };

                case "find_symbol":
                    return new ResponseBlock
                    {
                        Type = BlockType.FindSymbol,
                        Pattern = GetArg(tc, "query"),
                        MaxResults = GetIntArg(tc, "max_results"),
                        Language = GetArg(tc, "language")
                    };

                case "web_search":
                    return new ResponseBlock
                    {
                        Type = BlockType.WebSearch,
                        Pattern = GetArg(tc, "query"),
                        MaxResults = GetIntArg(tc, "max_results")
                    };

               case "web_fetch":
                    return new ResponseBlock
                    {
                        Type = BlockType.WebFetch,
                        Url = GetArg(tc, "url")
                    };

                case "run_sql":
                    {
                        int maxRowsVal = GetIntArg(tc, "max_rows");
                        int timeoutVal = GetIntArg(tc, "command_timeout");
                        return new ResponseBlock
                        {
                            Type = BlockType.RunSql,
                            SqlQuery = GetArg(tc, "query"),
                            SqlConnString = GetArg(tc, "connection_string"),
                            SqlConnName = GetArg(tc, "connection_name"),
                            SqlAllowWrite = GetBoolArg(tc, "allow_write"),
                            SqlMaxRows = maxRowsVal > 0 ? maxRowsVal : 100,
                            SqlCommandTimeout = timeoutVal > 0 ? timeoutVal : 30
                        };
                    }

                case "debug":
                    {
                        // The `args` tool parameter is a nested object; LlmClient flattens it into
                        // Arguments["args"] as its JSON string (prop.Value.ToString()). Re-parse it
                        // into a flat command-arg map the host can translate into /debug argv.
                        var dbgArgs = new Dictionary<string, string>();
                        string argsJson = GetArg(tc, "args");
                        if (!string.IsNullOrWhiteSpace(argsJson))
                        {
                            try
                            {
                                foreach (var p in JObject.Parse(argsJson).Properties())
                                    dbgArgs[p.Name] = p.Value?.ToString() ?? "";
                            }
                            catch { /* malformed args object — proceed with empty args */ }
                        }
                        return new ResponseBlock
                        {
                            Type = BlockType.Debug,
                            DebugCommand = GetArg(tc, "command"),
                            DebugArgs = dbgArgs
                        };
                    }

                case "recall_cache":
                    return new ResponseBlock
                    {
                        Type = BlockType.RecallCache,
                        RecallCacheCommand = GetArg(tc, "handle")
                    };

                default:
                    // Unknown tool — emit as text so it's visible in output
                    return new ResponseBlock
                    {
                        Type = BlockType.Text,
                        Content = $"[Unknown tool call: {tc.Name}]"
                    };
            }
        }

        private static string GetArg(ToolCallResult tc, string key)
        {
            if (tc.Arguments != null && tc.Arguments.TryGetValue(key, out string value))
                return value;
            return null;
        }

        private static int GetIntArg(ToolCallResult tc, string key)
        {
            string val = GetArg(tc, key);
            if (val != null && int.TryParse(val, out int result))
                return result;
            return 0;
        }

        private static bool GetBoolArg(ToolCallResult tc, string key)
        {
            string val = GetArg(tc, key);
            if (val != null && bool.TryParse(val, out bool result))
                return result;
            return false;
        }
    }
}
