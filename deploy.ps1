<#
.SYNOPSIS
    Deploy DevMind: stop what is running, back up the current build, publish, report.

.DESCRIPTION
    run-deploy.ps1 publishes TWO executables that are normally in use:
      dist\DevMind.TUI.exe          - the TUI, run by 'devmind' / 'dm' on PATH
      dist\mcp\DevMind.McpServer.exe - the MCP server, spawned by Claude Code
    dotnet publish fails on a locked file, and a half-finished publish leaves
    dist\ as neither version. So this stops them first, confirms the files are
    actually writable, and only then publishes.

    Also handles the two things around it that were being done by hand:

      Version comes from git (1.0.<git-commit-count>, Directory.Build.props), so a
      dirty tree deploys under the PREVIOUS commit's number and the running server
      then misreports what it is. Refused unless -Force.

      dist\mcp is snapshotted to dist\mcp.bak-<version it is replacing> first, so
      the rollback is named for the version it actually contains. An existing
      backup for that version is never overwritten - deploying twice off one commit
      must not replace a good rollback with the build you just made.

    RUN THIS FROM A PLAIN POWERSHELL WINDOW, not from inside Claude Code: it kills
    the MCP server, which is the process Claude Code is talking to. Restart Claude
    Code afterwards to pick up the new one.

.PARAMETER SkipBackup
    Publish without snapshotting dist\mcp first.

.PARAMETER Force
    Proceed despite a dirty tree or unpushed commits. The version stamp will be wrong.

.PARAMETER GlobalDir
    The DevMind global state directory holding the live system-prompt.md. Defaults to
    DEVMIND_GLOBAL_DIR, else %APPDATA%\devmind - the same order DevMindPaths.GlobalDir
    uses. Point it at a temp folder to exercise the prompt sync without touching the
    real one.

.PARAMETER OverwriteLivePrompt
    Replace a live system-prompt.md that differs from the repo's copy, instead of
    refusing. The old live copy is kept as system-prompt.md.bak-<timestamp>.

.PARAMETER TimeoutSeconds
    How long to wait for a stopped process to release its file. Default 20.

.EXAMPLE
    .\deploy.ps1
.EXAMPLE
    .\deploy.ps1 -Force        # deploying a work-in-progress build on purpose
#>
[CmdletBinding()]
param(
    [switch] $SkipBackup,
    [switch] $Force,
    [string] $GlobalDir,
    [switch] $OverwriteLivePrompt,
    [int]    $TimeoutSeconds = 20
)

$ErrorActionPreference = 'Stop'
$repo = $PSScriptRoot
$dist = Join-Path $repo 'dist'
$mcp  = Join-Path $dist 'mcp'
$exe  = Join-Path $mcp  'DevMind.McpServer.exe'
$tui  = Join-Path $dist 'DevMind.TUI.exe'

# The runtime resolves its state directory as DEVMIND_GLOBAL_DIR else %APPDATA%\devmind
# (DevMindPaths.GlobalDir, re-read on every access). The prompt only matters at the path
# the runtime actually reads, so the deploy reconciles against the same one. An explicit
# -GlobalDir wins; whitespace-only counts as unset, as it does in DevMindPaths.
if     (-not [string]::IsNullOrWhiteSpace($GlobalDir))             { $globalDirPath = $GlobalDir }
elseif (-not [string]::IsNullOrWhiteSpace($env:DEVMIND_GLOBAL_DIR)) { $globalDirPath = $env:DEVMIND_GLOBAL_DIR }
else                                                                { $globalDirPath = Join-Path $env:APPDATA 'devmind' }

function Fail($msg) { Write-Host "`nDEPLOY ABORTED: $msg" -ForegroundColor Red; exit 1 }
function Step($msg) { Write-Host "`n$msg" -ForegroundColor Cyan }

# Returns $true when the file can be opened for writing, i.e. nothing holds it.
function Test-Unlocked([string] $path) {
    if (-not (Test-Path $path)) { return $true }   # nothing there to lock
    try {
        $fs = [System.IO.File]::Open($path, 'Open', 'Write', 'None')
        $fs.Close(); $fs.Dispose()
        return $true
    } catch { return $false }
}

# Byte-for-byte equality. Deliberately not a timestamp or line-count compare: the
# prompt is whitespace-sensitive to the model, so a difference of one space is a
# real difference and must be reported as one.
function Test-SameBytes([string] $a, [string] $b) {
    $x = [System.IO.File]::ReadAllBytes($a)
    $y = [System.IO.File]::ReadAllBytes($b)
    if ($x.Length -ne $y.Length) { return $false }
    for ($i = 0; $i -lt $x.Length; $i++) { if ($x[$i] -ne $y[$i]) { return $false } }
    return $true
}

Push-Location $repo
try {
    # ---- 1. tree must be clean and pushed ------------------------------------
    Step "Checking working tree..."

    # Only changes that can reach the BUILD matter here. A modified tracked file
    # always can. An untracked file can only if the compiler would pick it up, so
    # untracked build inputs block and everything else (notes, logs, scratch) is
    # reported and allowed through - otherwise a stray file in the repo root
    # blocks a deploy it cannot possibly affect.
    $buildInput = '\.(cs|csproj|slnx|sln|props|targets|json|resx)$'
    $blocking = @(); $benign = @()
    foreach ($line in (git status --porcelain)) {
        if (-not $line) { continue }
        $code = $line.Substring(0,2)
        $path = $line.Substring(3).Trim('"')
        if ($code -eq '??') {
            if ($path -match $buildInput) { $blocking += $line } else { $benign += $line }
        } else { $blocking += $line }
    }

    if ($benign) {
        Write-Host "  untracked, not a build input - ignoring:" -ForegroundColor DarkGray
        $benign | ForEach-Object { Write-Host "    $_" -ForegroundColor DarkGray }
    }
    if ($blocking -and -not $Force) {
        Write-Host "  changes that would reach the build:" -ForegroundColor Yellow
        $blocking | ForEach-Object { Write-Host "    $_" -ForegroundColor Yellow }
        Fail "working tree has uncommitted build changes. The build is stamped 1.0.<commit-count>, so this would deploy under the previous commit's version. Commit first, or re-run with -Force."
    }

    $head  = (git rev-parse --short HEAD).Trim()
    $count = (git rev-list --count HEAD).Trim()
    foreach ($r in @('origin','nas')) {
        $remoteHead = git rev-parse --short "$r/master" 2>$null
        if ($LASTEXITCODE -ne 0) {
            Write-Host "  note: remote '$r' not reachable - skipping its check" -ForegroundColor DarkGray
            continue
        }
        if ($remoteHead.Trim() -ne $head -and -not $Force) {
            Fail "HEAD ($head) does not match $r/master ($($remoteHead.Trim())). Push first, or re-run with -Force."
        }
    }
    Write-Host "  clean, HEAD $head, will publish as 1.0.$count" -ForegroundColor DarkGray

    # ---- 2. reconcile the system prompt ---------------------------------------
    # system-prompt.md in the global state directory REPLACES the built-in prompt on
    # every prompt assembly, in all three skins (SystemPromptFile.Load, called from
    # the TUI, the CLI and the headless agent), so it is the most influential piece
    # of configuration DevMind has. The repo copy is the source of truth - it is what
    # gets reviewed, diffed and committed; the live copy is what runs, and editing it
    # directly stays possible for quick experiments because the runtime re-reads it
    # every turn.
    #
    # Publishing over live edits the repo has never seen would silently destroy them,
    # which is the loss this step exists to prevent - so drift is a refusal, not a
    # warning. It runs immediately after the dirty-tree check, so the repo copy being
    # compared is a committed one, and before anything is stopped or snapshotted, so a
    # refusal costs nothing: no killed server, no half-taken backup, no touched dist\.
    Step "Reconciling system-prompt.md..."

    $repoPrompt = Join-Path $repo 'prompts\system-prompt.md'
    $livePrompt = Join-Path $globalDirPath 'system-prompt.md'

    if (-not (Test-Path $repoPrompt)) {
        Fail "prompts\system-prompt.md is missing from the repo. It is the source of truth for the live prompt - restore it from git rather than deploying without it. Nothing was published."
    }

    if (-not (Test-Path $livePrompt)) {
        # Absence is a normal state, not an error: all three callers fall back to the
        # built-in prompt when the file is missing. The fix is simply to install it.
        New-Item -ItemType Directory -Force -Path $globalDirPath | Out-Null
        Copy-Item -LiteralPath $repoPrompt -Destination $livePrompt -Force
        Write-Host "  Installed system-prompt.md -> $livePrompt" -ForegroundColor Yellow
    }
    elseif (Test-SameBytes $repoPrompt $livePrompt) {
        Write-Host "  system-prompt.md in sync" -ForegroundColor DarkGray
    }
    elseif (-not $OverwriteLivePrompt) {
        Fail @"
system-prompt.md differs between the repo (prompts\system-prompt.md) and the live copy
($livePrompt). The live copy has edits the repo does not. Either
copy them into prompts\system-prompt.md and commit, or re-run with -OverwriteLivePrompt
to discard them (a .bak is kept). Nothing was published.
"@
    }
    else {
        # Named for the moment it was taken, not for a version: the live copy has no
        # version to name it after - that is the whole problem being fixed here.
        $bak = "$livePrompt.bak-$(Get-Date -Format 'yyyyMMdd-HHmmss')"
        Copy-Item -LiteralPath $livePrompt -Destination $bak -Force
        Copy-Item -LiteralPath $repoPrompt -Destination $livePrompt -Force
        Write-Host "  -OverwriteLivePrompt: live copy saved to $bak" -ForegroundColor Yellow
        Write-Host "  system-prompt.md replaced from the repo" -ForegroundColor Yellow
    }

    # ---- 3. refuse to kill a live job ----------------------------------------
    # AgentJobManager writes %TEMP%\devmind\tasks\_active.json for exactly as long as a
    # delegated job is executing, and its own comment (AgentJobManager.cs:714) records
    # why it exists: a deploy once killed a live job by guessing from transcript silence
    # and CPU load, both of which lie - think blocks are transcript-silent and generation
    # is GPU-bound. The marker is the positive signal, so check it before stopping anything.
    Step "Checking for a running delegated job..."
    $tasksDir = if ($env:DEVMIND_TASKS_DIR) { $env:DEVMIND_TASKS_DIR } else { Join-Path $env:TEMP 'devmind\tasks' }
    $marker   = Join-Path $tasksDir '_active.json'

    if (-not (Test-Path $marker)) {
        Write-Host "  none running" -ForegroundColor DarkGray
    } else {
        $active = $null
        try { $active = Get-Content $marker -Raw | ConvertFrom-Json } catch { }

        if (-not $active -or -not $active.pid) {
            # Unreadable marker: treat as stale rather than blocking forever on a corrupt file.
            Write-Host "  marker present but unreadable - treating as stale: $marker" -ForegroundColor Yellow
        }
        else {
            # A marker outlives a crashed server, so the pid decides, not the file's existence.
            $owner = Get-Process -Id $active.pid -ErrorAction SilentlyContinue
            if (-not $owner) {
                Write-Host ("  stale marker from dead PID {0} (job {1}) - ignoring" -f $active.pid, $active.job_id) -ForegroundColor DarkGray
            }
            elseif (-not $Force) {
                Write-Host ("  job      : {0}" -f $active.job_id)       -ForegroundColor Yellow
                Write-Host ("  pid      : {0} ({1})" -f $active.pid, $owner.ProcessName) -ForegroundColor Yellow
                Write-Host ("  started  : {0} UTC" -f $active.started_at_utc) -ForegroundColor Yellow
                Write-Host ("  work dir : {0}" -f $active.working_dir)  -ForegroundColor Yellow
                Fail "a delegated job is running. Stopping the server now would kill it mid-task. Wait for it to finish, or re-run with -Force to kill it deliberately."
            }
            else {
                Write-Host ("  -Force: killing live job {0} (PID {1})" -f $active.job_id, $active.pid) -ForegroundColor Red
            }
        }
    }

    # ---- 4. stop what is running ---------------------------------------------
    Step "Stopping running DevMind processes..."

    # Match on image path under dist\ rather than name alone, so a DevMind build
    # running from somewhere else (a test copy, another checkout) is left alone.
    $targets = Get-Process -Name 'DevMind.McpServer','DevMind.TUI' -ErrorAction SilentlyContinue |
               Where-Object {
                   try { $_.Path -and $_.Path.StartsWith($dist, [StringComparison]::OrdinalIgnoreCase) }
                   catch { $false }   # Path throws on processes we cannot open
               }

    if (-not $targets) {
        Write-Host "  none running" -ForegroundColor DarkGray
    } else {
        foreach ($p in $targets) {
            Write-Host ("  stopping {0} (PID {1})" -f $p.ProcessName, $p.Id) -ForegroundColor Yellow
            try { $p.CloseMainWindow() | Out-Null } catch { }   # no-op for a console/stdio process
        }
        Start-Sleep -Milliseconds 500
        foreach ($p in $targets) {
            try {
                $p.Refresh()
                if (-not $p.HasExited) {
                    Write-Host ("    PID {0} still up - killing" -f $p.Id) -ForegroundColor Yellow
                    Stop-Process -Id $p.Id -Force -ErrorAction Stop
                }
            } catch [System.Management.Automation.ProcessCommandException] {
                # already gone between the check and the kill - fine
            } catch {
                Fail ("could not stop PID {0}: {1}" -f $p.Id, $_.Exception.Message)
            }
        }
    }

    # A process can be gone while the filesystem has not yet released its handle,
    # so wait on the FILE, not on the process table.
    Step "Waiting for file locks to clear..."
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    foreach ($f in @($exe, $tui)) {
        while (-not (Test-Unlocked $f)) {
            if ((Get-Date) -gt $deadline) {
                $holders = Get-Process -Name 'DevMind.McpServer','DevMind.TUI' -ErrorAction SilentlyContinue
                if ($holders) { Write-Host ("  still running: {0}" -f (($holders | ForEach-Object { "$($_.ProcessName)/$($_.Id)" }) -join ', ')) -ForegroundColor Yellow }
                Fail "$f is still locked after $TimeoutSeconds s. Close Claude Code and any open DevMind window, then re-run."
            }
            Start-Sleep -Milliseconds 250
        }
        Write-Host ("  {0} writable" -f (Split-Path $f -Leaf)) -ForegroundColor DarkGray
    }

    # ---- 5. snapshot the version being replaced ------------------------------
    if (-not $SkipBackup -and (Test-Path $exe)) {
        $old = (Get-Item $exe).VersionInfo.FileVersion
        $bak = Join-Path $dist "mcp.bak-$($old -replace '\.0$','')"
        Step "Backing up deployed MCP server $old"
        if (Test-Path $bak) {
            Write-Host "  $bak already exists - keeping it, not overwriting" -ForegroundColor Yellow
        } else {
            Copy-Item $mcp $bak -Recurse -Force
            Write-Host "  -> $bak" -ForegroundColor DarkGray
        }
    }

    # ---- 6. publish -----------------------------------------------------------
    Step "Publishing..."
    & (Join-Path $repo 'run-deploy.ps1')
    if ($LASTEXITCODE -ne 0) { Fail "run-deploy.ps1 failed (exit $LASTEXITCODE)" }

    # ---- 7. report ------------------------------------------------------------
    $newMcp = (Get-Item $exe).VersionInfo.FileVersion
    $newTui = if (Test-Path $tui) { (Get-Item $tui).VersionInfo.FileVersion } else { '(not published)' }

    Write-Host ""
    Write-Host "Deployed $head" -ForegroundColor Green
    Write-Host ("  DevMind.McpServer  {0}" -f $newMcp) -ForegroundColor Green
    Write-Host ("  DevMind.TUI        {0}" -f $newTui) -ForegroundColor Green

    if ($newMcp -notlike "1.0.$count*") {
        Write-Host ("  WARNING: expected 1.0.{0} from the commit count but the exe reports {1}." -f $count, $newMcp) -ForegroundColor Red
    }

    Write-Host "`nRollbacks on disk:" -ForegroundColor DarkGray
    Get-ChildItem $dist -Directory -Filter 'mcp.bak-*' | Sort-Object Name | ForEach-Object { "  $($_.Name)" }

    Write-Host "`nRestart Claude Code to pick up the new MCP server." -ForegroundColor Yellow
}
finally { Pop-Location }
