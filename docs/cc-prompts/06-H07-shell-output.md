# DevMind harness: H-07 — shell output that agents can actually consume (UTF-8 end to end, no file round-trips)

Repo: C:\Users\pkailas\source\repos\DevMind  (build: dotnet build "C:\Users\pkailas\source\repos\DevMind\DevMind.slnx" -t:Rebuild)
Read docs\harness-watchlist.md H-07 (the binary part is already fixed — BinaryOutputMask) and H-21/H-27 (shell tokenizer) first.
Run only after prompt 05 has committed.

## Symptoms (H-07, jobs 1652/1653/1655)
- Agents redirect output to %TEMP% files and read_file them back (4 round trips for one byte check).
- PowerShell `2>file` / `>file` writes UTF-16 — several iterations of decode confusion.
- `cmd /c "... > log 2>&1"` inside the wrapper wrote only "The system cannot find the path specified." with exit 0.
- CLIXML error blocks still appear for stderr from some commands.

## Current code (verify before relying on it; line numbers are approximate — search for the code)
- DevMind.Core\ShellRunner.cs: PowerShell commands are wrapped by WrapForPowerShell (~L848-890: $ErrorActionPreference='Continue',
  $ProgressPreference='SilentlyContinue', body in `& { … } 6>&1 3>&1 | ForEach … | Out-String -Stream -Width 200`), then sent as
  `-NoProfile -NonInteractive -EncodedCommand` (UTF-16 base64) to Windows PowerShell 5.1.
- RunProcessAsync (~L254-280) sets RedirectStandardOutput/Error = true but sets NO StandardOutputEncoding / StandardErrorEncoding,
  so .NET decodes with the console's OEM code page — non-ASCII output (em dashes, box drawing, accented names) arrives mangled.
- The wrapper does not set [Console]::OutputEncoding, $OutputEncoding, or a default Out-File encoding — in PowerShell 5.1 the
  `>` / `2>` redirection operators use Out-File's default, which is UTF-16LE ("Unicode").

## Fix
1. Decoding: set psi.StandardOutputEncoding and StandardErrorEncoding to UTF-8 (no BOM) for the PowerShell path, and in the wrapper
   preamble set `[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)` and `$OutputEncoding` likewise, so native
   commands' output (dotnet, git) and PowerShell's own formatting both arrive as UTF-8. Gray area: the cmd.exe path (forceCmdExe) —
   decide whether to add `chcp 65001 >nul &` or leave it; justify with a test.
2. File redirection: in the wrapper preamble set
   `$PSDefaultParameterValues['Out-File:Encoding'] = 'utf8'` (and `'*:Encoding'` only if you confirm it does not break other cmdlets)
   so `>`, `>>`, `2>` and Out-File write UTF-8 in PS 5.1. Note: PS 5.1 'utf8' writes a BOM — say whether that is acceptable or whether
   the agent-facing read path (read_file) already handles it (it should — check).
3. The `cmd /c "... > log 2>&1"` case: reproduce it inside the wrapper first. Report what actually caused "path not found" (quoting
   through -EncodedCommand? %VAR% expansion per H-27? a relative path vs WorkingDirectory?). Fix the cause if it is the harness's;
   if it is the command's own fault, say so and add nothing.
4. CLIXML on stderr: reproduce with a native command that writes to stderr (e.g. `dotnet build` of a broken project, `git` with a bad ref,
   `cmd /c "echo x 1>&2"`) and a PowerShell Write-Error. If any path still produces `#< CLIXML` text in the tool output, strip/convert it
   to plain text at the ShellRunner boundary. If none does any more (a1c375b may have fixed it), say so with the evidence.
5. Tool description: update run_shell's description to say output comes back inline (UTF-8, capped) and that redirecting to a file
   and reading it back is unnecessary.
Do NOT change how commands are tokenized or rewritten (H-21/H-27 share ShellRunner's tokenizer — leave it alone).

## Tests (ShellRunner tests run real PowerShell — follow the existing patterns; keep each test fast)
- Output containing "— é ✓ ┌─┐" from Write-Output and from a native command (e.g. `cmd /c echo` or a tiny .NET console call) arrives
  byte-exact in the tool output.
- `"é — x" > file.txt` inside run_shell writes UTF-8 (assert the bytes, BOM or not as decided).
- `2> err.txt` writes UTF-8.
- stderr from a native command and from Write-Error contains no `#< CLIXML`.
- Item 3: a regression test for whatever the cause turned out to be (or none if not the harness's).
- Existing ShellRunner / tokenizer / %VAR% / && tests unchanged and green.

## Done means
- Rebuild 0 errors / 0 warnings; dotnet test with --blame-hang --blame-hang-timeout 45s --blame-hang-dump-type none, all green; report counts.
- Mutation check: remove the StandardOutputEncoding line and confirm the non-ASCII test fails by name; restore.
- Watchlist: H-07 → fixed (or the parts that are), pending deploy.
- Update docs\cc-prompts\README.md: mark this prompt done (hash in a follow-up commit as before).
- Commit. Do NOT deploy.
- Report: files changed, what items 3 and 4 turned out to be, gray-area decisions, test counts.
