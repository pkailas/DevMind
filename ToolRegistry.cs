// File: ToolRegistry.cs  v7.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.

using Newtonsoft.Json.Linq;

namespace DevMind
{
    /// <summary>
    /// Builds the OpenAI-compatible <c>tools</c> JArray for structured tool calling.
    /// Each DevMind directive is represented as a function tool with JSON Schema parameters.
    /// </summary>
    internal static class ToolRegistry
    {
        /// <summary>
        /// Builds the complete tools array for inclusion in the chat completion request.
        /// </summary>
        public static JArray BuildToolsArray()
        {
            var tools = new JArray();

            // ── read_file ────────────────────────────────────────────────────
            tools.Add(MakeTool("read_file",
                "Read a file from the project. Files under 100 lines return full content; " +
                "larger files return an outline (class/method/property declarations with line numbers). " +
                "Use start_line/end_line for targeted reads after reviewing the outline. " +
                "Set force_full to true only when you need the entire file regardless of size. " +
                "READ the file before PATCHing — never patch a file you have not seen. " +
                "Git variants: set filename to 'git log' (recent commits) or 'git diff' (working changes). " +
                "For git log, use start_line as the count (default 10, max 50). " +
                "For git diff, append args to filename: 'git diff --staged', 'git diff HEAD~1', 'git diff filename.cs'.",
                Required("filename", "string", "File path relative to project root, or 'git log'/'git diff' for git operations"),
                Optional("start_line", "integer", "1-based start line for a targeted range read"),
                Optional("end_line", "integer", "1-based end line for a targeted range read"),
                Optional("force_full", "boolean", "When true, bypasses the outline threshold and returns the full file content")));

            // ── create_file ──────────────────────────────────────────────────
            tools.Add(MakeTool("create_file",
                "Create a new file with the given content. Use for brand-new files only — " +
                "use patch_file to edit existing files. Do not wrap content in code fences.",
                Required("filename", "string", "File path relative to project root"),
                Required("content", "string", "The complete source code for the new file")));

            // ── patch_file ───────────────────────────────────────────────────
            tools.Add(MakeTool("patch_file",
                "Edit an existing file by replacing exact text. The find text must be copied " +
                "verbatim from read_file output — never reconstructed from memory. " +
                "Whitespace-normalized matching is applied (CRLF and indentation differences ignored). " +
                "If find matches multiple locations, the patch is rejected — provide more context to disambiguate. " +
                "Always read_file first if you have not seen the file. " +
                "After any code change, call run_build to verify the build still passes.",
                Required("filename", "string", "File path relative to project root"),
                Required("find", "string", "Exact text to find in the file (verbatim from read_file output)"),
                Required("replace", "string", "Replacement text")));

            // ── run_shell ────────────────────────────────────────────────────
            tools.Add(MakeTool("run_shell",
                "Execute a shell command and return its output. " +
                "Commands run via powershell.exe with a 120-second timeout. " +
                "Use run_build instead of manually running build commands.",
                Required("command", "string", "The shell command to execute")));

            // ── grep_file ────────────────────────────────────────────────────
            tools.Add(MakeTool("grep_file",
                "Search a single file for lines matching a pattern (case-insensitive substring match). " +
                "Returns matching lines with 1-based line numbers. Capped at 50 matches. " +
                "Use grep_file to locate code, then read_file with a targeted range, then patch_file. " +
                "Prefer grep_file + targeted read_file over sequential full-file reads.",
                Required("pattern", "string", "Search pattern (case-insensitive substring match, not regex)"),
                Required("filename", "string", "File path relative to project root"),
                Optional("start_line", "integer", "1-based start line to restrict the search window"),
                Optional("end_line", "integer", "1-based end line to restrict the search window")));

            // ── find_in_files ────────────────────────────────────────────────
            tools.Add(MakeTool("find_in_files",
                "Search across multiple files by glob pattern for lines matching a text pattern. " +
                "Returns filename:line:content for each hit, capped at 100 results. " +
                "Use find_in_files when you need to know where something is used across the project. " +
                "Use grep_file when you already know which file to search.",
                Required("pattern", "string", "Search pattern (case-insensitive substring match)"),
                Required("glob", "string", "Glob pattern to match files (e.g., '*.cs', 'Services/*.cs')"),
                Optional("start_line", "integer", "1-based start line to restrict the search window within each file"),
                Optional("end_line", "integer", "1-based end line to restrict the search window within each file")));

            // ── delete_file ──────────────────────────────────────────────────
            tools.Add(MakeTool("delete_file",
                "Delete a file from disk. Use only when explicitly asked to remove a file. " +
                "Do not use delete_file speculatively — only when the task requires file removal.",
                Required("filename", "string", "File path relative to project root")));

            // ── rename_file ──────────────────────────────────────────────────
            tools.Add(MakeTool("rename_file",
                "Rename or move a file on disk. The old file is closed in the editor and the new file is opened. " +
                "Does not update references in other files — use find_in_files + patch_file to update imports.",
                Required("old_filename", "string", "Current file path relative to project root"),
                Required("new_filename", "string", "New file path relative to project root")));

            // ── diff_file ────────────────────────────────────────────────────
            tools.Add(MakeTool("diff_file",
                "Show all changes made to a file during this conversation as a unified-style diff. " +
                "Use diff_file after multiple patches to verify cumulative changes before confirming task completion. " +
                "Information-gathering only — does not modify files.",
                Required("filename", "string", "File path relative to project root")));

            // ── run_tests ────────────────────────────────────────────────────
            tools.Add(MakeTool("run_tests",
                "Run dotnet test and return structured pass/fail results. " +
                "Output is compact — only failed tests show details. " +
                "Use run_tests after making changes to verify correctness. " +
                "If tests fail, fix the code with patch_file and run_tests again.",
                Optional("project", "string", "Project file name (e.g., 'MyProject.csproj'). Omit to run all tests."),
                Optional("filter", "string", "Test filter expression (e.g., 'FullyQualifiedName~SomeTest' or 'ClassName.MethodName')")));

            // ── scratchpad ───────────────────────────────────────────────────
            tools.Add(MakeTool("scratchpad",
                "Track your state across turns. Use to record your current goal, files being edited, " +
                "status (PLANNING/PATCHING/BUILDING/DONE), last action, and next step. " +
                "Format: Goal, Files, Status, Last, Next — one line each.",
                Required("content", "string", "Scratchpad content for state tracking")));

            // ── task_done ────────────────────────────────────────────────────
            tools.Add(MakeTool("task_done",
                "Signal that the current task is complete. Emit only when all steps are finished — " +
                "code changes applied, build verified, tests passing. Do not emit mid-task.",
                Optional("summary", "string", "Brief summary of what was accomplished")));

            // ── run_build ────────────────────────────────────────────────────
            tools.Add(MakeTool("run_build",
                "Run the project build command. Call this after ANY code change (patch_file or create_file). " +
                "The actual build command is substituted by the executor based on the active project. " +
                "No parameters needed — the system knows the correct build command."));

            return tools;
        }

        // ── Helpers ──────────────────────────────────────────────────────────

        private static JObject MakeTool(string name, string description, params JProperty[] properties)
        {
            var requiredArr = new JArray();
            var propsObj = new JObject();

            foreach (var prop in properties)
            {
                propsObj.Add(prop);
                // Properties whose description does not start with "[optional]" are required
                var propObj = (JObject)prop.Value;
                if (propObj["description"]?.ToString().StartsWith("[optional]") != true)
                {
                    requiredArr.Add(prop.Name);
                }
            }

            var parameters = new JObject
            {
                ["type"] = "object",
                ["properties"] = propsObj
            };

            if (requiredArr.Count > 0)
            {
                parameters["required"] = requiredArr;
            }

            // additionalProperties = false for strict schema compliance
            parameters["additionalProperties"] = false;

            return new JObject
            {
                ["type"] = "function",
                ["function"] = new JObject
                {
                    ["name"] = name,
                    ["description"] = description,
                    ["parameters"] = parameters
                }
            };
        }

        private static JProperty Required(string name, string type, string description)
        {
            return new JProperty(name, new JObject
            {
                ["type"] = type,
                ["description"] = description
            });
        }

        private static JProperty Optional(string name, string type, string description)
        {
            return new JProperty(name, new JObject
            {
                ["type"] = type,
                ["description"] = "[optional] " + description
            });
        }
    }
}
