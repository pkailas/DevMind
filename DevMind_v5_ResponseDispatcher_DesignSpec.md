# DevMind v5.0 Response Dispatcher — Design Spec

**Author:** Claude (for Paul Kailas / iOnline Consulting LLC)
**Date:** March 10, 2026
**Scope:** Replace LLM response handling in `DevMindToolWindowControl.xaml.cs`

---

## Problem Statement

The current architecture pre-commits to a response handling strategy before the LLM responds.
`ExtractFileName()` scans the **user's prompt** and decides the entire pipeline upfront:

- **File generation mode:** Every token goes into `fileGenBuffer`. PATCH blocks, SHELL directives,
  and plain text all get captured as file content.
- **Normal mode:** Tokens stream to the OutputBox. After completion, `onComplete` parses for
  PATCH, SHELL, and READ directives.

These two modes are mutually exclusive. A single LLM response cannot create a file AND patch
another file AND run a build — but that's exactly what multi-step agentic tasks require.

The `onComplete` handler is 200+ lines of interleaved branching that manages: file saving,
PATCH parsing, SHELL execution, agentic re-triggering, auto-READ resubmission, fuzzy patch
confirmation, build-success detection, and error recovery. Every point fix makes it worse.

---

## Architecture

### Core Principle

**Classify the response AFTER it arrives, not before.**

Stream all tokens to a single buffer (with real-time UI display). After streaming completes,
parse the full response into typed blocks, then execute each block in sequence.

### LLM Directives

The model has four directives. All are explicit, line-delimited, and composable in any order:

```
FILE: <filename>
<raw source code — no fences, no explanation>
END_FILE

PATCH <filename>
FIND:
<exact text>
REPLACE:
<replacement text>

SHELL: <command>

READ <filename>
```

A single response can contain any combination:

```
Here's the new helper class:

FILE: DateHelper.cs
namespace DevMindTestBed;
public static class DateHelper
{
    public static string FormatIso(DateTime dt) => dt.ToString("yyyy-MM-dd");
}
END_FILE

Now updating Program.cs to call it:

PATCH Program.cs
FIND:
Console.WriteLine("Hello, World!");
REPLACE:
Console.WriteLine(DateHelper.FormatIso(DateTime.Today));

SHELL: dotnet build
```

### Response Block Types

```
ResponseBlock
├── TextBlock        — plain text (already displayed during streaming)
├── FileBlock        — FILE:/END_FILE content (filename + source)
├── PatchBlock       — PATCH directive (raw text passed to ApplyPatchAsync)
├── ShellBlock       — SHELL: directive (command string)
└── ReadRequest      — model asking to READ a file before proceeding
```

---

## Implementation

### 1. ResponseParser.cs (new file)

Pure static class. No VS dependencies, no UI — just string in, blocks out.

```csharp
// File: ResponseParser.cs  v5.0
// Copyright (c) iOnline Consulting LLC. All rights reserved.

namespace DevMind
{
    public enum BlockType { Text, File, Patch, Shell, ReadRequest }

    public class ResponseBlock
    {
        public BlockType Type { get; set; }
        public string Content { get; set; }    // raw text / file source / patch text
        public string FileName { get; set; }   // for File, Patch, ReadRequest
        public string Command { get; set; }    // for Shell
    }

    public static class ResponseParser
    {
        public static List<ResponseBlock> Parse(string response) { ... }
    }
}
```

**Parsing rules (scan top-to-bottom):**

1. Line starts with `FILE: ` → begin FileBlock, capture filename. All subsequent lines go
   into FileBlock.Content until a line starting with `END_FILE` is found.
2. Line matches `^PATCH \S+\.\S+` → begin PatchBlock. Capture everything through the last
   FIND/REPLACE pair (terminated by the next `FILE:`, `PATCH `, `SHELL:`, or end-of-string).
   Stop capture at `SHELL:` lines — they are NOT part of the PATCH.
3. Line starts with `SHELL:` → ShellBlock with the command portion.
4. Line matches `^READ \S+\.\S+` → ReadRequest (model asking for file context).
5. Everything else accumulates into the current TextBlock.

Adjacent text segments merge into a single TextBlock. Empty TextBlocks are discarded.

### 2. Streaming (onToken)

During streaming, tokens display in real-time. File content is captured silently.

New fields (replace `isFileGeneration`, `fileGenBuffer`, `_generatingTokenCount`):

```csharp
private bool _inFileCapture;
private string _fileCaptureFileName;
private StringBuilder _fileCaptureBuffer;
```

Token handling:

```csharp
onToken: token =>
{
    var visible = FilterChunk(token);  // think-block filter (unchanged)
    if (string.IsNullOrEmpty(visible)) return;

    responseBuffer.Append(visible);    // ALWAYS buffer the full response

    // Detect FILE: boundary
    // Note: token boundaries are unpredictable — "FILE:" may span two tokens.
    // Check the tail of responseBuffer for complete line matches.
    if (!_inFileCapture)
    {
        // Check if responseBuffer now ends with a "FILE: <filename>\n" line
        string bufStr = responseBuffer.ToString();
        var fileMatch = Regex.Match(bufStr, @"FILE:\s*(\S+\.[\w]+)\s*$");
        if (fileMatch.Success)
        {
            _inFileCapture = true;
            _fileCaptureFileName = fileMatch.Groups[1].Value;
            _fileCaptureBuffer = new StringBuilder();
            _generatingTokenCount = 0;
            StartGeneratingAnimation(_fileCaptureFileName);
            return;
        }
        // Normal display
        streamRun.Text += visible;
        OutputBox.ScrollToEnd();
    }
    else
    {
        // Check for END_FILE
        if (visible.TrimStart().StartsWith("END_FILE"))
        {
            _inFileCapture = false;
            StopGeneratingAnimation();
            // Resume normal display for subsequent tokens
            return;
        }
        _fileCaptureBuffer.Append(visible);
        _generatingTokenCount++;
        StatusText.Text = $"Generating {_fileCaptureFileName}... ({_generatingTokenCount} tokens)";
    }
}
```

**Important:** `responseBuffer` captures EVERYTHING (including file content). The
`_fileCaptureBuffer` also captures file content separately for saving. The full
`responseBuffer` is what gets parsed by `ResponseParser.Parse()` in `onComplete`.

### 3. onComplete (simplified)

The entire onComplete handler becomes a flat execute loop:

```csharp
onComplete: () =>
{
    AppendNewLine();
    string fullResponse = responseBuffer.ToString();
    var blocks = ResponseParser.Parse(fullResponse);

    var patchedPaths = new List<string>();
    bool ranShell = false;
    bool hadReadRequest = false;

    foreach (var block in blocks)
    {
        switch (block.Type)
        {
            case BlockType.File:
                await SaveGeneratedFileAsync(block.FileName, block.Content);
                break;

            case BlockType.Patch:
                AppendOutput($"[AUTO-PATCH] Executing PATCH {block.FileName}...\n", OutputColor.Dim);
                var path = await ApplyPatchAsync(block.Content, clearInput: false);
                if (path != null && !patchedPaths.Contains(path))
                    patchedPaths.Add(path);
                break;

            case BlockType.Shell:
                AppendOutput($"[SHELL] > {block.Command}\n", OutputColor.Dim);
                var (output, exitCode) = await RunShellCommandCaptureAsync(block.Command);
                _lastShellExitCode = exitCode;
                AppendOutput(output + "\n", OutputColor.Normal);
                ranShell = true;
                break;

            case BlockType.ReadRequest:
                hadReadRequest = true;
                await ApplyReadCommandAsync("READ " + block.FileName);
                break;

            case BlockType.Text:
                break; // already displayed during streaming
        }
    }

    // ── Agentic loop decision ─────────────────────────────────────

    bool hasActions = patchedPaths.Count > 0 || ranShell || blocks.Any(b => b.Type == BlockType.File);

    // Auto-READ resubmit: response was just a READ request, no other actions
    if (hadReadRequest && !hasActions && _pendingResubmitPrompt != null)
    {
        string saved = _pendingResubmitPrompt;
        _pendingResubmitPrompt = null;
        AppendOutput($"[AUTO-READ] File(s) loaded — resubmitting original prompt...\n", OutputColor.Dim);
        InputTextBox.Text = saved;
        SendToLlm();
        return;
    }

    // Build succeeded — done
    if (ranShell && _lastShellExitCode == 0)
    {
        AppendOutput("[AGENTIC] Build succeeded — task complete.\n", OutputColor.Success);
        _agenticDepth = 0;
        _pendingResubmitPrompt = null;
        // fall through to completion
    }
    // Build failed or PATCH-only — re-trigger if depth allows
    else if (hasActions)
    {
        int maxDepth = DevMindOptions.Instance.AgenticLoopMaxDepth;
        if (maxDepth > 0 && _agenticDepth < maxDepth)
        {
            _agenticDepth++;

            var context = new StringBuilder();
            context.AppendLine($"[AGENTIC LOOP — iteration {_agenticDepth} of {maxDepth}]");

            // Inject current file state for patched files
            foreach (var pp in patchedPaths)
            {
                try
                {
                    string content = File.ReadAllText(pp);
                    context.AppendLine($"\n[Current state — {Path.GetFileName(pp)}]\n{content}");
                }
                catch { }
            }

            _pendingShellContext = context.ToString().TrimEnd();
            InputTextBox.Text = ranShell
                ? "The build failed. Analyze the errors above and apply targeted PATCH fixes. Do NOT re-add code that already exists. Do NOT replace code with unrelated code."
                : "PATCH applied. If the original task requires more changes, continue. If done, respond with DONE.";
            _shellLoopPending = true;
            SendToLlm();
            return;
        }
        else
        {
            // Depth cap reached
            _agenticDepth = 0;
            if (ranShell && _lastShellExitCode != 0)
            {
                AppendOutput($"[AGENTIC] Depth cap reached — build still failing.\n", OutputColor.Error);
                AppendOutput("Type UNDO to revert changes, or continue manually.\n", OutputColor.Dim);
            }
        }
    }

    // ── Completion ────────────────────────────────────────────────

    _agenticDepth = 0;
    _pendingResubmitPrompt = null;
    StatusText.Text = "Ready";
    ContextIndicator.Text = "";
    SetInputEnabled(true);
    InputTextBox.Focus();
}
```

### 4. SendToLlm() Cleanup

Remove from `SendToLlm()`:

- `ExtractFileName()` call and all `isFileGeneration` branching
- `fileGenBuffer` variable
- `targetFileName` variable
- The namespace-injection block (move to system prompt or FILE: instruction)
- The conditional between file-gen streaming and normal streaming in `onToken`

The `onToken` callback becomes a single path with the file-capture toggle (Section 2).

### 5. System Prompt

Update the runtime-injected directive (the `llmDirective` string prepended to the system prompt):

```
To create new files, wrap content in FILE: / END_FILE markers:
FILE: <filename>
<raw source code — no fences, no explanation>
END_FILE

To edit existing files, use PATCH blocks:
PATCH <filename>
FIND:
<exact text from the file>
REPLACE:
<replacement text>

To run shell commands: SHELL: <command>
To read files before editing: READ <filename>

Rules:
- You may combine FILE, PATCH, SHELL, and READ in a single response.
- After ANY code change, always emit SHELL: dotnet build to verify.
- Never output raw code blocks. All file content must use FILE: or PATCH.
- If you need to see a file before editing, say READ <filename>.
```

---

## What Gets Deleted

| Item | Reason |
|------|--------|
| `ExtractFileName()` method | Replaced by `FILE:` directive |
| `isFileGeneration` flag | No longer exists — one streaming path |
| `fileGenBuffer` | Replaced by `_fileCaptureBuffer` |
| `targetFileName` in SendToLlm | No pre-detection needed |
| Namespace injection in SendToLlm | Model handles via FILE: context |
| SHELL: line stripping from file content | Impossible by design — SHELL: is outside FILE:/END_FILE |
| PATCH-in-file splitting logic | Impossible by design — PATCH is outside FILE:/END_FILE |
| The 200-line branched onComplete | Replaced by flat execute loop |
| `_pendingShellContext` concatenation spaghetti | Replaced by structured context builder |

---

## Files Affected

| File | Change |
|------|--------|
| `ResponseParser.cs` | **New** — block parser |
| `DevMindToolWindowControl.xaml.cs` | Rewrite `SendToLlm()` onToken/onComplete |
| `DevMindToolWindowControl.Patch.cs` | No changes — `ApplyPatchAsync` unchanged |
| `DevMindToolWindowControl.Shell.cs` | No changes |
| `DevMindOptionsPage.cs` | Update default system prompt |
| `DevMind.md` | Document `FILE:` / `END_FILE` syntax |

---

## Edge Cases

| Scenario | Handling |
|----------|----------|
| Model emits `END_FILE` inside a string literal in file content | Use `<<<END_FILE>>>` as sentinel if this becomes a real problem. In practice, `END_FILE` alone on a line is extremely unlikely inside source code. |
| `FILE:` token split across streaming chunks | `responseBuffer` accumulates everything. File-capture detection checks the buffer tail for a complete `FILE:` line, not individual tokens. |
| Model ignores `FILE:` directive and dumps bare code | The bare-code-block retry detection (existing fix) catches this and resubmits with a correction prompt. |
| Model emits multiple `FILE:` blocks | Each is parsed as a separate FileBlock and saved independently. |
| Model emits PATCH before FILE | Works — blocks execute in order. PATCH runs first, then FILE saves. |
| Agentic re-trigger with filename in prompt | No longer a problem — `ExtractFileName` is gone. File creation only happens via explicit `FILE:` directive from the model. |

---

## Success Criteria

This single prompt must work end-to-end in one response:

> Create a new file DateHelper.cs with a static class DateHelper containing two methods:
> string FormatIso(DateTime dt) and bool IsWeekend(DateTime dt). Then modify Program.cs
> to call both methods. Run a build to confirm it compiles.

Expected flow:
1. Model emits `FILE: DateHelper.cs` ... `END_FILE` → file saved
2. Model emits `PATCH Program.cs` → executed
3. Model emits `SHELL: dotnet build` → executed
4. Build succeeds (exit code 0) → "Build succeeded — task complete"

One response. No agentic re-trigger needed. No file corruption. No UI freeze.
