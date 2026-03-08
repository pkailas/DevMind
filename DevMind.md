# DevMind Project Context

## Project Overview
DevMind is a Visual Studio extension (VSIX) that provides local LLM assistance directly inside Visual Studio 2022+. It is a product of iOnline Consulting LLC. The UI is a CC-style command executor (single dark RichTextBox stream), not a bubble chat interface.

## Platform & Constraints
- **Project type**: VSIX (.NET Framework net48)
- **C# language version**: 8.0 — no C# 9+ syntax (no `or` pattern combinators, no records, no init-only setters, no top-level statements)
- **UI framework**: WPF inside a ToolWindowPane
- **Threading**: All VS API calls require `await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync()` first
- **Async void**: Permitted only for fire-and-forget VS event handlers; must have try/catch wrapping the entire body

## Architecture
- `DevMindToolWindowControl.xaml` — WPF layout: Input TextBox (top), toolbar row (Ask/Run/Stop/Clear), OutputBox RichTextBox (dark #1E1E1E, Consolas)
- `DevMindToolWindowControl.xaml.cs` — All logic; current version is tracked in the file header comment
- `LlmClient.cs` — HTTP streaming client for local LLM (LM Studio OpenAI-compatible endpoint); do not modify schema
- `DevMindOptionsPage.cs` — VS Tools > Options page; categories: General, File Generation

## Key Fields & Methods
- `AppendOutput(text, OutputColor)` — appends colored Run to OutputBox; never replaces, always appends
- `OutputColor` enum: Normal, Dim, Input (blue), Error (red), Success (green)
- `SendToLlm()` — async void, handles Ask button and Enter key
- `RunShellCommand()` — handles Run button and Ctrl+Enter; intercepts `/reload` command
- `GetEditorContextAsync()` — reads active VS editor selection and/or full file (≤300 lines)
- `BuildMessageWithContext()` — prepends fenced code block to LLM message if context exists
- `ExtractFileName(prompt)` — scans prompt for words ending in known code extensions
- `SaveGeneratedFileAsync(fileName, code)` — strips `<think>` blocks, resolves active project dir via DTE, writes file, calls `ProjectItems.AddFromFile()`
- `StartGeneratingAnimation(fileName)` / `StopGeneratingAnimation()` — animated dots in OutputBox during file gen
- `LoadDevMindContextAsync()` — reads DevMind.md from solution root; cached in `_devMindContext`

## LLM Backend
- Local model via LM Studio at `http://localhost:1234` (OpenAI-compatible)
- Current model: Qwen3.5 27B on RTX 4000 SFF Ada (20GB VRAM)
- Models may produce `<think>...</think>` blocks — these are always stripped before display and before file writes

## File Generation Behavior
When a prompt contains a filename with a known code extension (.cs, .ts, .js, .py, .xml, .json, .sql, .html, .css, .xaml, .cpp, .h, .vb, .fs):
1. LLM is instructed to respond with raw source only (no fences, no preamble)
2. Tokens accumulate silently; animated dots show in OutputBox
3. VS status bar shows live token count: `DevMind: Generating X.cs... (N tokens)`
4. File saves to active project directory; added to project via `ProjectItems.AddFromFile()`
5. Active project's `DefaultNamespace` is injected into the instruction
6. File opens in VS editor if `OpenFileAfterGeneration` option is true (default)

## Options (Tools > Options > DevMind)
- **General**: SystemPrompt (string), ModelUrl (string)
- **File Generation**: OpenFileAfterGeneration (bool, default true)

## Shell Commands (Run Button)
- PowerShell is the shell — use `;` to chain commands, NOT `&&` (`&&` is invalid in PowerShell)
- Example: `git commit -am "msg"; git push`
- `dotnet build` works directly: `dotnet build C:\path\to\Solution.slnx`

## PATCH Command
- Syntax: `PATCH <filename> / FIND: / <text> / REPLACE: / <text>`
- Bypasses LLM entirely — instant local file edit, no tokens consumed
- Uses whitespace-normalized matching — CRLF and indentation differences ignored
- Supports path hints to disambiguate same-named files: `PATCH VLink.PDFSanitizerService/Program.cs`

## READ Command
- Syntax: `READ <filename>` or `READ path/hint/filename.cs` for disambiguation
- Loads file into `_readContext`, prepended to next Ask — no tokens consumed
- Multiple READs accumulate before a single Ask
- Multi-line: paste multiple READ lines at once, all load simultaneously
- Context persists across multiple Asks — use `/context` (Run) to see loaded files, `/context clear` to wipe

## UNDO Command
- Syntax: `UNDO`
- Restores the last PATCHed file from a timestamped backup in `%TEMP%\DevMind\`
- Stack depth: 10 — oldest backup discarded when limit reached
- Stack cleared on Restart

## Coding Conventions
- All file headers include: `// File: FileName.cs  vX.Y` — always bump version on changes
- MVVM not used in this project — code-behind only
- No external NuGet dependencies beyond VSSDK and Community Toolkit
- No third-party markdown renderers — plain RichTextBox with Run/Paragraph appends only
- Namespace: `DevMind`

## Shell Shortcuts
- `/reload` — clears cached DevMind.md, reloads on next Ask
- `/context` — shows currently loaded READ files
- `/context clear` — wipes READ context without restarting

## DevMind.md Reload
Type `/reload` in the input box and press Run (Ctrl+Enter) to force a fresh read of this file on the next Ask.
