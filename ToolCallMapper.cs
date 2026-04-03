// File: ToolCallMapper.cs  v7.1
// Copyright (c) iOnline Consulting LLC. All rights reserved.

using System.Collections.Generic;
using System.Text;

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

                case "patch_file":
                    {
                        // Reconstruct the PATCH text format expected by ApplyPatchAsync.
                        // Format: PATCH filename\nFIND:\n{find}\nREPLACE:\n{replace}\nEND_PATCH
                        string filename = GetArg(tc, "filename");
                        string find = GetArg(tc, "find");
                        string replace = GetArg(tc, "replace");

                        var sb = new StringBuilder();
                        sb.AppendLine($"PATCH {filename}");
                        sb.AppendLine("FIND:");
                        sb.AppendLine(find);
                        sb.AppendLine("REPLACE:");
                        sb.AppendLine(replace);
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
                    return new ResponseBlock
                    {
                        Type = BlockType.Shell,
                        Command = GetArg(tc, "command")
                    };

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
                    return new ResponseBlock
                    {
                        Type = BlockType.Test,
                        TestProject = GetArg(tc, "project"),
                        TestFilter = GetArg(tc, "filter")
                    };

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
