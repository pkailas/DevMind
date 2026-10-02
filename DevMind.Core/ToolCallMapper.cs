// File: ToolCallMapper.cs  v7.4
// Copyright (c) iOnline Consulting LLC. All rights reserved.

using System;
using System.Collections.Generic;
using System.Linq;
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

            AssignFallbackIds(toolCalls);

            foreach (var tc in toolCalls)
            {
                // H-53/H-54: a call that cannot run as sent is not mapped to an executable
                // block. Its ArgumentError becomes its tool result (LoopHelpers), and the
                // transcript shows the same text.
                tc.ArgumentError = ValidateArguments(tc);
                if (tc.ArgumentError != null)
                {
                    blocks.Add(new ResponseBlock { Type = BlockType.Text, Content = $"[TOOL ERROR] {tc.ArgumentError}" });
                    continue;
                }

                var block = MapSingle(tc, buildCommand);
                if (block != null)
                    blocks.Add(block);
            }

            return blocks;
        }

        /// <summary>
        /// Gives a call a <see cref="ToolCallResult.FallbackId"/> when its id is missing or
        /// repeats an earlier call's in the same turn. MCP results are keyed by call id
        /// (<see cref="McpToolName.ResultKey"/>), so a shared or empty id would hand one call's
        /// result to both. The first holder of an id keeps it. Idempotent: a call that already
        /// has a fallback keeps it, so mapping the same list twice cannot re-key it.
        /// </summary>
        internal static void AssignFallbackIds(List<ToolCallResult> toolCalls)
        {
            if (toolCalls == null) return;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < toolCalls.Count; i++)
            {
                var tc = toolCalls[i];
                if (tc == null || tc.FallbackId != null) continue;
                if (!string.IsNullOrEmpty(tc.Id) && seen.Add(tc.Id)) continue;
                tc.FallbackId = $"dm_call_{i}_{Guid.NewGuid():N}";
                seen.Add(tc.FallbackId);
            }
        }

        // ── Argument validation (H-53 / H-54) ──────────────────────────────────────
        // job-1971..1977: read_file/grep_file sent with the path under the wrong key reached
        // the executor with a null FileName and came back as "Value cannot be null.
        // (Parameter 'key')"; patch_file edits keyed old_text/new_text were skipped one by one
        // and the call became an empty FIND. The model had to guess what it did wrong.

        /// <summary>Tools whose required arguments are checked before anything runs. The
        /// required keys themselves come from <see cref="ToolRegistry"/>'s schemas.</summary>
        private static readonly string[] ValidatedTools =
        {
            "read_file", "grep_file", "find_in_files", "create_file", "append_file", "patch_file",
            "delete_file", "rename_file", "diff_file", "list_files", "run_shell",
        };

        /// <summary>Arguments that may legitimately be an empty string (an empty file).
        /// Missing is still an error for them; blank is not.</summary>
        private static readonly HashSet<string> BlankAllowed = new HashSet<string>(StringComparer.Ordinal) { "content" };

        private sealed class ToolSchema
        {
            public List<string> Required = new List<string>();
            public List<string> All = new List<string>();
        }

        private static readonly Lazy<Dictionary<string, ToolSchema>> s_schemas =
            new Lazy<Dictionary<string, ToolSchema>>(BuildSchemas);

        private static Dictionary<string, ToolSchema> BuildSchemas()
        {
            var map = new Dictionary<string, ToolSchema>(StringComparer.Ordinal);
            var wanted = new HashSet<string>(ValidatedTools, StringComparer.Ordinal);
            foreach (JToken tool in ToolRegistry.BuildToolsArray())
            {
                string name = tool["function"]?["name"]?.ToString();
                if (name == null || !wanted.Contains(name)) continue;
                var parameters = tool["function"]["parameters"];
                var schema = new ToolSchema();
                if (parameters?["properties"] is JObject props)
                    foreach (var p in props.Properties()) schema.All.Add(p.Name);
                if (parameters?["required"] is JArray req)
                    foreach (var r in req) schema.Required.Add(r.ToString());
                map[name] = schema;
            }
            return map;
        }

        /// <summary>
        /// Why <paramref name="tc"/> cannot run as sent, or null when it can. Checks the
        /// schema's required arguments for the file/shell tools, patch_file's find/replace
        /// shape, and unknown tool names. Other tools are not checked here.
        /// </summary>
        internal static string ValidateArguments(ToolCallResult tc)
        {
            if (tc == null || string.IsNullOrEmpty(tc.Name)) return "tool call has no name.";
            if (McpToolName.TryParse(tc.Name, out _, out _)) return null;

            if (s_schemas.Value.TryGetValue(tc.Name, out ToolSchema schema))
            {
                foreach (string key in schema.Required)
                {
                    bool present = tc.Arguments != null && tc.Arguments.TryGetValue(key, out string value)
                        && value != null && (BlankAllowed.Contains(key) || !string.IsNullOrWhiteSpace(value));
                    if (!present)
                        return $"{tc.Name}: missing required argument '{key}'. Received: {DescribeKeys(tc.Arguments?.Keys)}. " +
                               $"Expected: {DescribeExpected(schema)}.";
                }
                if (tc.Name == "patch_file") return ValidatePatchEdits(tc);
            }
            return null;
        }

        /// <summary>
        /// patch_file's find/replace shape. An edits array, when given, must be usable as a whole:
        /// an item without 'find' or 'replace' is an error, never a skip, and there is no fallback
        /// to the top-level find/replace (job-1973 sent {old_text,new_text} items and got an empty
        /// FIND). Without edits, the top-level find and replace are both required.
        /// </summary>
        private static string ValidatePatchEdits(ToolCallResult tc)
        {
            const string shape = "Each edit must be {\"find\": ..., \"replace\": ...}.";
            string editsJson = GetArg(tc, "edits");
            if (!string.IsNullOrWhiteSpace(editsJson))
            {
                JArray edits;
                try { edits = JArray.Parse(editsJson); }
                catch (Exception ex) { return $"patch_file: 'edits' is not a JSON array ({ex.Message}). {shape}"; }
                if (edits.Count == 0) return $"patch_file: 'edits' is empty. {shape}";

                for (int i = 0; i < edits.Count; i++)
                {
                    if (!(edits[i] is JObject item))
                        return $"patch_file: edit {i + 1} is not an object. {shape}";
                    string keys = DescribeKeys(item.Properties().Select(p => p.Name));
                    JToken find = item["find"];
                    if (find == null || find.Type == JTokenType.Null)
                        return $"patch_file: edit {i + 1} has no 'find' key (keys: {keys}). {shape}";
                    if (find.ToString().Length == 0)
                        return $"patch_file: edit {i + 1} has an empty 'find'. Copy the exact text to replace from read_file output.";
                    JToken replace = item["replace"];
                    if (replace == null || replace.Type == JTokenType.Null)
                        return $"patch_file: edit {i + 1} has no 'replace' key (keys: {keys}). {shape} Use \"replace\": \"\" to delete.";
                }
                return null;
            }

            string received = DescribeKeys(tc.Arguments?.Keys);
            if (string.IsNullOrEmpty(GetArg(tc, "find")))
                return $"patch_file: no 'find' and no 'edits'. Received: {received}. " +
                       "Send find + replace for one edit, or edits: [{\"find\": ..., \"replace\": ...}].";
            if (GetArg(tc, "replace") == null)
                return $"patch_file: 'find' was given without 'replace'. Received: {received}. Use \"replace\": \"\" to delete.";
            return null;
        }

        private static string DescribeKeys(IEnumerable<string> keys)
        {
            var list = keys?.ToList();
            return list == null || list.Count == 0 ? "no arguments" : string.Join(", ", list);
        }

        private static string DescribeExpected(ToolSchema schema)
            => string.Join(", ", schema.All.Select(k => schema.Required.Contains(k) ? k + " (required)" : k));

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
                        ToolCallId = tc.ResultId,
                        FromToolCall = true
                    };

                case "create_file":
                    return new ResponseBlock
                    {
                        Type = BlockType.File,
                        FileName = GetArg(tc, "filename"),
                        Content = GetArg(tc, "content"),
                        ToolCallId = tc.ResultId,
                        FromToolCall = true
                    };

                case "append_file":
                    return new ResponseBlock
                    {
                        Type = BlockType.AppendFile,
                        FileName = GetArg(tc, "filename"),
                        Content = GetArg(tc, "content"),
                        ToolCallId = tc.ResultId,
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

                        // ValidatePatchEdits already ran: when edits is given, every item has a
                        // non-empty find and a replace. No skipping, and no fallback to the
                        // top-level find/replace when edits was given (H-54).
                        string editsJson = GetArg(tc, "edits");
                        if (!string.IsNullOrWhiteSpace(editsJson))
                        {
                            foreach (var item in JArray.Parse(editsJson))
                            {
                                sb.Append("FIND:\n").Append(item["find"].ToString()).Append('\n');
                                sb.Append("REPLACE:\n").Append(item["replace"].ToString()).Append('\n');
                            }
                        }
                        else
                        {
                            // Single-edit form: top-level find/replace.
                            sb.Append("FIND:\n").Append(GetArg(tc, "find")).Append('\n');
                            sb.Append("REPLACE:\n").Append(GetArg(tc, "replace")).Append('\n');
                        }

                        sb.Append("END_PATCH");

                        return new ResponseBlock
                        {
                            Type = BlockType.Patch,
                            FileName = filename,
                            Content = sb.ToString(),
                            ToolCallId = tc.ResultId,
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
                            ShellTimeoutSeconds = timeoutVal > 0 ? (int?)timeoutVal : null,
                            ShellDetach = GetBoolArg(tc, "detach")
                        };
                    }

                case "grep_file":
                    return new ResponseBlock
                    {
                        Type = BlockType.Grep,
                        Pattern = GetArg(tc, "pattern"),
                        FileName = GetArg(tc, "filename"),
                        RangeStart = GetIntArg(tc, "start_line"),
                        RangeEnd = GetIntArg(tc, "end_line"),
                        ToolCallId = tc.ResultId
                    };

                case "find_in_files":
                    return new ResponseBlock
                    {
                        Type = BlockType.Find,
                        Pattern = GetArg(tc, "pattern"),
                        GlobPattern = GetArg(tc, "glob"),
                        RangeStart = GetIntArg(tc, "start_line"),
                        RangeEnd = GetIntArg(tc, "end_line"),
                        ToolCallId = tc.ResultId
                    };

                case "delete_file":
                    return new ResponseBlock
                    {
                        Type = BlockType.Delete,
                        FileName = GetArg(tc, "filename"),
                        ToolCallId = tc.ResultId
                    };

                case "rename_file":
                    return new ResponseBlock
                    {
                        Type = BlockType.Rename,
                        RenameFrom = GetArg(tc, "old_filename"),
                        RenameTo = GetArg(tc, "new_filename"),
                        ToolCallId = tc.ResultId
                    };

                case "diff_file":
                    return new ResponseBlock
                    {
                        Type = BlockType.Diff,
                        FileName = GetArg(tc, "filename"),
                        ToolCallId = tc.ResultId
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
                        Command = buildCommand,
                        // The build command is configurable (DEVMIND_BUILD_COMMAND may be a
                        // script ResolveTimeout cannot recognise as a build) — always the long budget.
                        ShellTimeoutSeconds = ShellRunner.ResolveLongTimeout()
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
                        LibraryTopK = GetIntArg(tc, "top_k"),
                        LibraryDocFilter = GetArg(tc, "doc_filter")
                    };

                case "list_files":
                    return new ResponseBlock
                    {
                        Type = BlockType.ListFiles,
                        ListFilesGlob = GetArg(tc, "glob"),
                        ListFilesRecursive = tc.Arguments?.ContainsKey("recursive") != true || GetBoolArg(tc, "recursive"),
                        ToolCallId = tc.ResultId
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
                        Language = GetArg(tc, "language"),
                        // Reuses FileName: for find_symbol it is the optional scope hint
                        // (a file OR a directory inside the solution to search).
                        FileName = GetArg(tc, "path")
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

                case "learn_search":
                    return new ResponseBlock
                    {
                        Type = BlockType.LearnSearch,
                        Pattern = GetArg(tc, "query"),
                        MaxResults = GetIntArg(tc, "max_results")
                    };

                case "learn_fetch":
                    return new ResponseBlock
                    {
                        Type = BlockType.LearnFetch,
                        Url = GetArg(tc, "url")
                    };

                case "learn_code_search":
                    return new ResponseBlock
                    {
                        Type = BlockType.LearnCodeSearch,
                        Pattern = GetArg(tc, "query"),
                        MaxResults = GetIntArg(tc, "max_results")
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

                case "list_cache":
                    return new ResponseBlock
                    {
                        Type = BlockType.ListCache
                    };

                default:
                    // External MCP tool: mcp__<server>__<tool>. A malformed mcp__ name fails the
                    // parse and falls through to the unknown-tool text below, like any other.
                    if (McpToolName.TryParse(tc.Name, out string mcpServer, out string mcpTool))
                    {
                        return new ResponseBlock
                        {
                            Type = BlockType.McpCall,
                            McpServer = mcpServer,
                            McpTool = mcpTool,
                            McpArguments = McpArguments(tc),
                            ToolCallId = tc.ResultId,
                            FromToolCall = true
                        };
                    }

                    // Unknown tool — emit as text so it's visible in output, and say so in the
                    // tool result too (it used to come back as a bare "[Executed]").
                    tc.ArgumentError = $"Unknown tool call: {tc.Name}";
                    return new ResponseBlock
                    {
                        Type = BlockType.Text,
                        Content = $"[Unknown tool call: {tc.Name}]"
                    };
            }
        }

        /// <summary>
        /// The typed argument object for an MCP call. LlmClient sets <see cref="ToolCallResult.RawArguments"/>
        /// after the repair ladder (syntax-only, so schema-agnostic and safe for any tool). A
        /// producer that did not set it gets the flattened string map back as an object of
        /// strings — lossy for non-string values, but the server's schema check then says so.
        /// </summary>
        private static JObject McpArguments(ToolCallResult tc)
        {
            if (tc.RawArguments != null)
                return (JObject)tc.RawArguments.DeepClone();
            var obj = new JObject();
            if (tc.Arguments != null)
                foreach (var kv in tc.Arguments)
                    obj[kv.Key] = kv.Value;
            return obj;
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
