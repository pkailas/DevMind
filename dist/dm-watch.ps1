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
            $lastState = $state
        }

        # A newer job log means a new task started — switch to it.
        $newest = Get-ChildItem $dir -Filter 'job-*.log' -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTime -Descending | Select-Object -First 1
        if ($newest -and $newest.FullName -ne $current.FullName) { break }
    }
}
