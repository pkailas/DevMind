# ── dm-watch: live terminal view of DevMind delegated tasks ──────────────────
# Tails the newest task transcript in %TEMP%\devmind\tasks and automatically
# switches when a new job starts. Works because HeadlessSession streams the
# transcript to disk live (FileShare.Read) as of 1.0.303 — on older builds the
# file only appears when the job finishes.
#
#   dm-watch            follow the newest job (and every job after it)
#   Ctrl+C              stop watching (does not affect the running job)

$dir = Join-Path $env:TEMP 'devmind\tasks'
$marker = Join-Path $dir '_active.json'

Write-Host "dm-watch — following DevMind task transcripts in $dir" -ForegroundColor Cyan
Write-Host "Ctrl+C to stop (the task keeps running).`n" -ForegroundColor DarkGray

# _active.json exists exactly while a job is executing (written/removed by the
# MCP server, 1.0.308+). Its pid tells us whether a leftover marker is stale.
function Get-ActiveState {
    if (-not (Test-Path $marker)) { return 'idle' }
    try {
        $m = Get-Content $marker -Raw | ConvertFrom-Json
        if (Get-Process -Id $m.pid -ErrorAction SilentlyContinue) {
            return "BUSY: $($m.job_id) (since $($m.started_at_utc)Z)"
        }
        return 'idle (stale marker — server crashed?)'
    } catch { return 'unknown' }
}

# Print a completion line for a finished job: local time, final state, iterations, elapsed,
# and token usage when the result carries it (tokens_* fields, 1.0.548+; omitted on older
# results or servers that report no usage; "new" omitted when the server gave no cache split).
# The result file is written as the job ends; give it a few seconds to appear.
function Write-JobFinished([string]$jobId) {
    $stamp = Get-Date -Format 'yyyy-MM-dd HH:mm:ss'
    $resultPath = Join-Path $dir "$jobId.result.json"
    $r = $null
    for ($i = 0; $i -lt 10 -and -not $r; $i++) {
        if (Test-Path $resultPath) {
            try { $r = Get-Content $resultPath -Raw | ConvertFrom-Json } catch { }
        }
        if (-not $r) { Start-Sleep -Milliseconds 500 }
    }
    if ($r) {
        $color = if ($r.state -eq 'done') { 'Green' } else { 'Red' }
        $mins  = [math]::Round([double]$r.elapsed_seconds / 60, 1)
        $tok = ''
        if ($null -ne $r.tokens_in_total -or $null -ne $r.tokens_out) {
            $in  = if ($null -ne $r.tokens_in_total) { ([long]$r.tokens_in_total).ToString('N0') } else { '?' }
            $new = if ($null -ne $r.tokens_in_new) { " ($(([long]$r.tokens_in_new).ToString('N0')) new$(if ($r.tokens_in_new_partial) { ', partial' }))" } else { '' }
            $out = if ($null -ne $r.tokens_out) { ([long]$r.tokens_out).ToString('N0') } else { '?' }
            $tok = ", tokens in $in$new / out $out"
        }
        Write-Host "── $stamp  $jobId COMPLETE — state: $($r.state), $($r.iterations) iterations, $mins min$tok ──" -ForegroundColor $color
        if ($r.incomplete_reasons) {
            Write-Host "   reasons: $(($r.incomplete_reasons | Select-Object -First 2) -join ' | ')" -ForegroundColor Red
        }
    } else {
        Write-Host "── $stamp  $jobId COMPLETE (no result file found) ──" -ForegroundColor Yellow
    }
}

while ($true) {
    $current = Get-ChildItem $dir -Filter 'job-*.log' -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $current) {
        Start-Sleep -Seconds 1
        continue
    }

    Write-Host "`n── $($current.Name) [$(Get-ActiveState)] ──────────────────────" -ForegroundColor Cyan
    $lastState = Get-ActiveState
    $pos = 0
    while ($true) {
        try {
            $fs = [System.IO.File]::Open($current.FullName, 'Open', 'Read', 'ReadWrite')
            try {
                if ($fs.Length -lt $pos) { $pos = 0 }   # file truncated/rewritten — restart
                if ($fs.Length -gt $pos) {
                    $fs.Seek($pos, 'Begin') | Out-Null
                    $reader = New-Object System.IO.StreamReader($fs)
                    $chunk = $reader.ReadToEnd()
                    $pos = $fs.Position
                    if ($chunk) { Write-Host -NoNewline $chunk }
                }
            }
            finally { $fs.Dispose() }
        }
        catch { }   # transient share violations — retry next poll

        Start-Sleep -Milliseconds 400

        # Announce busy/idle transitions — "idle" is the safe-to-deploy signal.
        $state = Get-ActiveState
        if ($state -ne $lastState) {
            Write-Host "`n── DM state: $state ──" -ForegroundColor $(if ($state -eq 'idle') { 'Green' } else { 'Yellow' })
            # A job just finished: say so with a local timestamp and its final state.
            if ($lastState -like 'BUSY: *' -and $state -notlike 'BUSY: *') {
                Write-JobFinished (($lastState -replace '^BUSY: (\S+).*$', '$1'))
            }
            $lastState = $state
        }

        # A newer job log means a new task started — switch to it.
        $newest = Get-ChildItem $dir -Filter 'job-*.log' -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTime -Descending | Select-Object -First 1
        if ($newest -and $newest.FullName -ne $current.FullName) { break }
    }
}
