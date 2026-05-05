// File: DevMindTools.cs  v1.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.
//
// Diagnostic policy: never write to Console.Out / Console.WriteLine in this file.
// All diagnostics go to Console.Error (stderr). stdout is reserved for the MCP
// stdio transport's JSON-RPC framing and must not be touched by application code.
//
// Phase status:
//   Phase A (this file): list_memory_topics implemented end-to-end.
//                        All other 15 tools stubbed with NotImplementedException.
//   Phase B: read_file, list_files, grep_file, find_in_files, diff_file,
//            recall_memory (read-only tools)
//   Phase C: patch_file, create_file, append_file, delete_file, rename_file,
//            save_memory (mutation tools)
//   Phase D: run_shell, run_build, run_tests (shell / streaming tools)

using System;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using DevMind;
using DevMind.McpServer;
using ModelContextProtocol.Server;

[McpServerToolType]
internal sealed class DevMindTools
{
    private readonly McpServices _svc;

    public DevMindTools(McpServices svc) => _svc = svc;

    // ── Phase A: implemented ─────────────────────────────────────────────────

    [McpServerTool(Name = "list_memory_topics")]
    [Description(
        "List all available memory topics with their descriptions from the memory index. " +
        "Use this to see what knowledge has been saved in previous sessions before " +
        "recalling a specific topic with recall_memory.")]
    public string ListMemoryTopics()
    {
        string index = _svc.Memory.LoadIndex();
        if (!string.IsNullOrWhiteSpace(index))
            return index;

        var topics = _svc.Memory.ListTopics();
        return topics.Count == 0
            ? "No memory topics found. Use save_memory to create one."
            : string.Join("\n", topics.Select(t => $"- [{t}]"));
    }

    // ── Phase B stubs: read-only tools ───────────────────────────────────────

    [McpServerTool(Name = "read_file")]
    [Description(
        "Read a file from the project. Files under 100 lines return full content; " +
        "larger files return an outline (class/method/property declarations with line numbers). " +
        "Use start_line/end_line for targeted reads after reviewing the outline. " +
        "Set force_full to true only when you need the entire file regardless of size. " +
        "For git operations set filename to 'git log' or 'git diff [args]'.")]
    public string ReadFile(
        [Description("Absolute file path, or 'git log'/'git diff [args]' for git operations.")] string filename,
        [Description("1-based start line for a targeted range read.")] int? start_line = null,
        [Description("1-based end line for a targeted range read.")] int? end_line = null,
        [Description("When true, bypasses the outline threshold and returns full file content.")] bool? force_full = null)
        => throw new NotImplementedException("read_file is implemented in Phase B.");

    [McpServerTool(Name = "list_files")]
    [Description(
        "List files matching a glob pattern under the project root. Returns absolute paths, " +
        "alphabetically sorted, capped at 200 results. Skips build artifacts (bin, obj) and " +
        "version control metadata (.vs, .git, node_modules, packages) automatically.")]
    public string ListFiles(
        [Description("Glob pattern to match files (e.g., '*.cs', 'Services/*.cs'). Required.")] string glob,
        [Description("If true (default), searches all subdirectories.")] bool recursive = true,
        CancellationToken cancellationToken = default)
        => throw new NotImplementedException("list_files is implemented in Phase B.");

    [McpServerTool(Name = "grep_file")]
    [Description(
        "Search a single file for lines matching a pattern (case-insensitive substring match). " +
        "Returns matching lines with 1-based line numbers, capped at 50 matches. " +
        "Use grep_file to locate code, then read_file with a targeted range, then patch_file.")]
    public string GrepFile(
        [Description("Search pattern (case-insensitive substring match, not regex).")] string pattern,
        [Description("Absolute file path.")] string filename,
        [Description("1-based start line to restrict the search window.")] int? start_line = null,
        [Description("1-based end line to restrict the search window.")] int? end_line = null)
        => throw new NotImplementedException("grep_file is implemented in Phase B.");

    [McpServerTool(Name = "find_in_files")]
    [Description(
        "Search across multiple files by glob pattern for lines matching a text pattern. " +
        "Returns filename:line:content for each hit, capped at 100 results. " +
        "Use find_in_files when you need to know where something is used across the project. " +
        "Use grep_file when you already know which file to search.")]
    public string FindInFiles(
        [Description("Search pattern (case-insensitive substring match).")] string pattern,
        [Description("Glob pattern to match files (e.g., '*.cs', 'Services/*.cs').")] string glob,
        [Description("1-based start line to restrict the search window within each file.")] int? start_line = null,
        [Description("1-based end line to restrict the search window within each file.")] int? end_line = null)
        => throw new NotImplementedException("find_in_files is implemented in Phase B.");

    [McpServerTool(Name = "diff_file")]
    [Description(
        "Show all changes made to a file during this session as a unified-style diff. " +
        "Use diff_file after multiple patches to verify cumulative changes before completing a task. " +
        "Information-gathering only — does not modify files.")]
    public string DiffFile(
        [Description("Absolute file path.")] string filename)
        => throw new NotImplementedException("diff_file is implemented in Phase B.");

    [McpServerTool(Name = "recall_memory")]
    [Description(
        "Recall previously saved knowledge about a topic. Returns the content of a memory " +
        "topic file. Call list_memory_topics first if you are not sure what topics are available.")]
    public string RecallMemory(
        [Description("The topic slug to recall (e.g., 'auth-system', 'build-quirks').")] string topic)
        => throw new NotImplementedException("recall_memory is implemented in Phase B.");

    // ── Phase C stubs: mutation tools ────────────────────────────────────────

    [McpServerTool(Name = "patch_file")]
    [Description(
        "Edit an existing file by replacing exact text. The find text must be copied " +
        "verbatim from read_file output — never reconstructed from memory. " +
        "Whitespace-normalized matching is applied (CRLF and indentation differences ignored). " +
        "Always read_file first if you have not seen the file.")]
    public string PatchFile(
        [Description("Absolute file path.")] string filename,
        [Description("Exact text to find in the file (verbatim from read_file output).")] string find,
        [Description("Replacement text.")] string replace)
        => throw new NotImplementedException("patch_file is implemented in Phase C.");

    [McpServerTool(Name = "create_file")]
    [Description(
        "Create a new file with the given content. Use for brand-new files only — " +
        "use patch_file to edit existing files. Do not wrap content in code fences.")]
    public string CreateFile(
        [Description("Absolute file path.")] string filename,
        [Description("The complete content for the new file.")] string content)
        => throw new NotImplementedException("create_file is implemented in Phase C.");

    [McpServerTool(Name = "append_file")]
    [Description(
        "Append content to the end of an existing file. If the file does not exist, it will be created.")]
    public string AppendFile(
        [Description("Absolute file path.")] string filename,
        [Description("Content to append to the end of the file.")] string content)
        => throw new NotImplementedException("append_file is implemented in Phase C.");

    [McpServerTool(Name = "delete_file")]
    [Description(
        "Delete a file from disk. Use only when explicitly asked to remove a file.")]
    public string DeleteFile(
        [Description("Absolute file path.")] string filename)
        => throw new NotImplementedException("delete_file is implemented in Phase C.");

    [McpServerTool(Name = "rename_file")]
    [Description(
        "Rename or move a file on disk. Does not update references in other files — " +
        "use find_in_files + patch_file to update imports after renaming.")]
    public string RenameFile(
        [Description("Current absolute file path.")] string old_filename,
        [Description("New absolute file path.")] string new_filename)
        => throw new NotImplementedException("rename_file is implemented in Phase C.");

    [McpServerTool(Name = "save_memory")]
    [Description(
        "Save project knowledge that should persist across sessions. Topics are overwritten " +
        "if they already exist — include all relevant content, not just additions.")]
    public string SaveMemory(
        [Description("Slug for the topic (e.g., 'auth-system', 'build-quirks').")] string topic,
        [Description("The knowledge to save — conventions, insights, patterns.")] string content,
        [Description("Short one-line description for the memory index.")] string? description = null)
        => throw new NotImplementedException("save_memory is implemented in Phase C.");

    // ── Phase D stubs: shell / streaming tools ───────────────────────────────

    [McpServerTool(Name = "run_shell")]
    [Description(
        "Execute a shell command and return its output. Commands run via PowerShell with a " +
        "120-second timeout. Use this for git commands and operations no other tool covers. " +
        "Do not use run_shell to list or search files — use list_files or find_in_files instead.")]
    public string RunShell(
        [Description("The shell command to execute.")] string command,
        CancellationToken cancellationToken = default)
        => throw new NotImplementedException("run_shell is implemented in Phase D.");

    [McpServerTool(Name = "run_build")]
    [Description(
        "Run the project build command. Call this after ANY code change (patch_file or create_file). " +
        "The build command is auto-detected from the working directory " +
        "(VSIX projects use MSBuild; other projects use dotnet build). No parameters needed.")]
    public string RunBuild(CancellationToken cancellationToken = default)
        => throw new NotImplementedException("run_build is implemented in Phase D.");

    [McpServerTool(Name = "run_tests")]
    [Description(
        "Run dotnet test and return structured pass/fail results. " +
        "Only failed tests show details. Use run_tests after making changes to verify correctness.")]
    public string RunTests(
        [Description("Project file name (e.g., 'MyProject.csproj'). Omit to run all tests.")] string? project = null,
        [Description("Test filter expression (e.g., 'FullyQualifiedName~SomeTest').")] string? filter = null,
        CancellationToken cancellationToken = default)
        => throw new NotImplementedException("run_tests is implemented in Phase D.");
}
