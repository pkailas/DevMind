#Requires -Version 5.1
<#
.SYNOPSIS
  Daily roll-up of DevMind delegated-job usage, appended to a CSV ledger.

.DESCRIPTION
  Reads every finished job's result file (job-NNNN.result.json) from the tasks
  directory, groups jobs by the LOCAL calendar day of their end time, and
  upserts one row per day into the ledger CSV.

  Modes:
    -Date yyyy-MM-dd   Compute that one day, print it, upsert its row.
    (no -Date)         Nightly mode: upsert every completed past day (strictly
                       before today, local) that has result files and is not
                       yet in the ledger, then print today-so-far WITHOUT
                       writing it.
    -NoWrite           Print only, touch nothing.

  A job belongs to the local calendar day (machine time) of its end time
  (ended_at_utc parsed as UTC; falls back to file LastWriteTimeUtc if absent).

  CSV columns:
    date,jobs,done,not_done,iterations,minutes,jobs_with_tokens,tokens_in_total,tokens_in_new,tokens_in_cached,tokens_out,opus55_usd,opus55_usd_jobs

  Opus 5.5 equivalent cost (USD, per-million-token rates as parameters):
    - job WITH a split (tokens_in_new not null):
        tokens_in_new * RateCacheWrite + (tokens_in_total - tokens_in_new) * RateCacheRead + tokens_out * RateOutput
    - job WITHOUT a split (tokens_in_new null, total present): skipped (not priced)

  All numbers written to the CSV use invariant culture (dot decimal, no
  thousands separators). Console output uses N0 formatting.

  Compatible with Windows PowerShell 5.1 (no ??, no ternary, no -AsHashtable).

.EXAMPLE
  .\dm-daily.ps1 -NoWrite
  .\dm-daily.ps1 -Date 2026-10-01
  .\dm-daily.ps1 -Ledger C:\tmp\test-ledger.csv
#>
[CmdletBinding()]
param(
    [string]$Date,
    [string]$Ledger = 'G:\DevMind_Tracing\dm-daily-ledger.csv',
    [switch]$NoWrite,
    [double]$RateCacheWrite = 5,
    [double]$RateCacheRead = 0.20,
    [double]$RateInput = 4,
    [double]$RateOutput = 20,
    [string]$TasksDir = ''
)

$ErrorActionPreference = 'Stop'
$inv = [System.Globalization.CultureInfo]::InvariantCulture
$header = 'date,jobs,done,not_done,iterations,minutes,jobs_with_tokens,tokens_in_total,tokens_in_new,tokens_in_cached,tokens_out,opus55_usd,opus55_usd_jobs'

if (-not $TasksDir) { $TasksDir = Join-Path $env:TEMP 'devmind\tasks' }
if (-not (Test-Path $TasksDir)) { Write-Error "Tasks directory not found: $TasksDir"; exit 1 }

# ---- helpers ---------------------------------------------------------------

function To-Long {
    param($v)
    if ($null -eq $v) { return 0 }
    return [long]$v
}

$script:RateCacheWrite = $RateCacheWrite
$script:RateCacheRead = $RateCacheRead
$script:RateOutput = $RateOutput

function ConvertTo-DayStats {
    # Takes an array of parsed result-file objects, returns a single PSCustomObject
    # with the per-day aggregates.
    param($Jobs)

    $jobs = @($Jobs)
    $n = $jobs.Count
    $done = 0
    $notDone = 0
    $iterations = 0
    $elapsedSec = 0.0
    $jobsWithTokens = 0
    $tokTotal = 0
    $tokNew = 0
    $tokCached = 0
    $tokOut = 0
    $usd = 0.0
    $usdJobs = 0

    foreach ($j in $jobs) {
        if ($j.state -eq 'done') { $done++ } else { $notDone++ }
        $iterations += (To-Long $j.iterations)
        if ($null -ne $j.elapsed_seconds) { $elapsedSec += [double]$j.elapsed_seconds }

        $hasTotal = ($null -ne $j.tokens_in_total)
        $hasNew   = ($null -ne $j.tokens_in_new)

        if ($hasTotal) {
            $jobsWithTokens++
            $tokTotal += (To-Long $j.tokens_in_total)
            $tokOut   += (To-Long $j.tokens_out)
        }
        if ($hasNew) {
            $tokNew    += (To-Long $j.tokens_in_new)
            if ($hasTotal) {
                $tokCached += (To-Long $j.tokens_in_total) - (To-Long $j.tokens_in_new)
            }
        }

        # Cost: only jobs WITH a split (both total and new present)
        if ($hasTotal -and $hasNew) {
            $usd += [double](To-Long $j.tokens_in_new) * $script:RateCacheWrite / 1e6
            $usd += [double]((To-Long $j.tokens_in_total) - (To-Long $j.tokens_in_new)) * $script:RateCacheRead / 1e6
            $usd += [double](To-Long $j.tokens_out) * $script:RateOutput / 1e6
            $usdJobs++
        }
    }

    $minutes = [math]::Round($elapsedSec / 60.0, 1)
    $usdRounded = [math]::Round($usd, 2)

    [PSCustomObject]@{
        date             = $null  # filled by caller
        jobs             = $n
        done             = $done
        not_done         = $notDone
        iterations       = $iterations
        minutes          = $minutes
        jobs_with_tokens = $jobsWithTokens
        tokens_in_total  = $tokTotal
        tokens_in_new    = $tokNew
        tokens_in_cached = $tokCached
        tokens_out       = $tokOut
        opus55_usd       = $usdRounded
        opus55_usd_jobs  = $usdJobs
    }
}

function Get-EndDateTime {
    # Returns a local DateTime for a result file. Uses ended_at_utc (parsed as
    # UTC) if present and parseable; falls back to file LastWriteTimeUtc.
    param($j, $fileInfo)
    $raw = $j.ended_at_utc
    if ($raw) {
        $s = [string]$raw
        $s = $s -replace '\$', ''
        # "2026-10-01 11:49:39" style; also accept ISO 8601 with T and Z
        $s2 = $s -replace 'T', ' '
        $s2 = $s2.TrimEnd('Z')
        # The literal carries no zone info; it is UTC by definition of the field.
        # ParseExact with a fixed format is unambiguous. try/catch for the rare
        # non-conforming value.
        try {
            $dt = [DateTime]::ParseExact($s2, 'yyyy-MM-dd HH:mm:ss', $inv)
            return [DateTime]::SpecifyKind($dt, [System.DateTimeKind]::Utc).ToLocalTime()
        } catch {
            # fall through to LastWriteTimeUtc
        }
    }
    return $fileInfo.LastWriteTimeUtc.ToLocalTime()
}

function Write-DayLine {
    param($s)
    # s is the PSCustomObject from ConvertTo-DayStats with .date set
    $parts = "$($s.date)  jobs $($s.jobs) ($($s.done) done)"
    if ($s.not_done -gt 0) { $parts += " ($($s.not_done) not done)" }
    $parts += "  iterations $($s.iterations)"
    $parts += "  minutes $([string]::Format($inv, '{0:0.0}', $s.minutes))"
    if ($s.jobs_with_tokens -gt 0) {
        $parts += "  tokens in $([string]::Format($inv, '{0:N0}', $s.tokens_in_total)) ($([string]::Format($inv, '{0:N0}', $s.tokens_in_new)) new) / out $([string]::Format($inv, '{0:N0}', $s.tokens_out))"
        $parts += "  ~Opus 5.5 $([string]::Format($inv, '${0:0.00}', $s.opus55_usd)) ($($s.opus55_usd_jobs) job$(if ($s.opus55_usd_jobs -ne 1) { 's' } else { '' }) priced)"
    }
    if ($script:testSkip.ContainsKey($s.date)) {
        $parts += "  (skipped $($script:testSkip[$s.date]) test jobs)"
    }
    Write-Host $parts
}

function New-CsvLine {
    param($s)
    # Invariant-culture, no thousands separators
    $row = @(
        $s.date,
        $s.jobs,
        $s.done,
        $s.not_done,
        $s.iterations,
        [string]::Format($inv, '{0:0.0}', $s.minutes),
        $s.jobs_with_tokens,
        $s.tokens_in_total,
        $s.tokens_in_new,
        $s.tokens_in_cached,
        $s.tokens_out,
        [string]::Format($inv, '{0:0.00}', $s.opus55_usd),
        $s.opus55_usd_jobs
    ) -join ','
    return $row
}

function Invoke-Upsert {
    # Reads the ledger (if it exists), replaces/inserts the row for $targetDate,
    # writes back. Creates the folder+file with header if missing.
    param($targetDate, $line)

    $dir = Split-Path $Ledger -Parent
    if (-not (Test-Path $dir)) {
        New-Item -ItemType Directory -Path $dir -Force | Out-Null
    }

    $rows = @()
    if (Test-Path $Ledger) {
        $rows = @(Get-Content -LiteralPath $Ledger)
        if ($rows[0] -eq $header) { $rows = $rows[1..($rows.Count - 1)] }
    }

    # Drop any existing row for this date
    $kept = @()
    foreach ($r in $rows) {
        if ($r.Length -ge 10) {
            $cell = $r.Substring(0, 10)
            if ($cell -ne $targetDate) { $kept += $r }
        } else {
            $kept += $r
        }
    }
    $kept += $line

    $out = @($header) + $kept
    $out | Set-Content -LiteralPath $Ledger -Encoding UTF8
    Write-Host "  [ledger] upserted $targetDate -> $Ledger"
}

# ---- gather jobs by local day ----------------------------------------------

$dayMap = @{}
$testSkip = @{}   # day -> count of skipped test-fixture jobs
$fileCount = 0
$nullCount = 0
$badNames = @()

# Test-fixture jobs (DevMind's own test suite) run with a working_dir under the
# temp folder (e.g. C:\Users\pkailas\AppData\Local\Temp\devmind_...). They are
# not real delegated work and are excluded from every day's numbers.
$tempRoot = ($env:TEMP).TrimEnd('\').ToUpper()

function Test-IsTestJob {
    param($workingDir)
    $wd = [string]$workingDir
    if (-not $wd) { return $false }
    return $wd.TrimEnd('\').ToUpper().StartsWith($tempRoot)
}

Get-ChildItem -LiteralPath $TasksDir -Filter 'job-*.result.json' -File |
    Sort-Object Name |
    ForEach-Object {
        $fileInfo = $_
        try {
            $j = Get-Content -LiteralPath $fileInfo.FullName -Raw | ConvertFrom-Json
        } catch {
            $nullCount++
            $badNames += $fileInfo.BaseName
            return
        }
        $fileCount++
        $endLocal = Get-EndDateTime -j $j -fileInfo $fileInfo
        $day = $endLocal.ToString('yyyy-MM-dd', $inv)
        if (Test-IsTestJob -workingDir $j.working_dir) {
            if (-not $testSkip.ContainsKey($day)) { $testSkip[$day] = 0 }
            $testSkip[$day]++
            return
        }
        if (-not $dayMap.ContainsKey($day)) { $dayMap[$day] = @() }
        $dayMap[$day] += $j
    }

if ($nullCount -gt 0) {
    $list = ($badNames -join ', ')
    Write-Warning "skipped $nullCount unreadable result file(s): $list"
}

# ---- build stats for a given day string ------------------------------------

function Get-DayStats {
    param($dayKey)
    if (-not $dayMap.ContainsKey($dayKey)) { return $null }
    $s = ConvertTo-DayStats -Jobs $dayMap[$dayKey]
    $s | Add-Member -NotePropertyName date -NotePropertyValue $dayKey -Force
    return $s
}

# ---- mode: -Date -------------------------------------------------------------

if ($Date) {
    if ($Date -notmatch '^\d{4}-\d{2}-\d{2}$') {
        Write-Error "Invalid -Date '$Date'. Expected format yyyy-MM-dd."
        exit 1
    }
    $s = Get-DayStats -dayKey $Date
    if ($null -eq $s) {
        Write-Host "$Date  (no jobs found)"
        exit 0
    }
    Write-DayLine -s $s
    if (-not $NoWrite) {
        $line = New-CsvLine -s $s
        Invoke-Upsert -targetDate $Date -line $line
    }
    exit 0
}

# ---- mode: nightly (no -Date) ------------------------------------------------

$today = (Get-Date).ToString('yyyy-MM-dd', $inv)

# Upserse every completed past day (strictly before today) not yet in the ledger
if (-not $NoWrite) {
    $existing = @{}
    if (Test-Path $Ledger) {
        $rows = @(Get-Content -LiteralPath $Ledger)
        foreach ($r in $rows) {
            if ($r.Length -ge 10) {
                $existing[$r.Substring(0, 10)] = $true
            }
        }
    }

    $missingDays = @($dayMap.Keys | Where-Object { [string]$_ -lt $today -and -not $existing.ContainsKey([string]$_) } | Sort-Object)
    foreach ($d in $missingDays) {
        $s = Get-DayStats -dayKey $d
        if ($null -eq $s) { continue }  # zero jobs, skip
        $line = New-CsvLine -s $s
        Invoke-Upsert -targetDate $d -line $line
    }
    if ($missingDays.Count -eq 0) {
        Write-Host "  [ledger] all past days already in $Ledger"
    }
}

# Print today so far (never written)
$s = Get-DayStats -dayKey $today
if ($null -ne $s) {
    Write-DayLine -s $s
} else {
    Write-Host "$today  (no jobs found)"
}

Write-Host ""
$totalTestSkipped = 0
foreach ($k in $testSkip.Keys) { $totalTestSkipped += $testSkip[$k] }
Write-Host "Scanned $fileCount result file(s) from $TasksDir ($totalTestSkipped test-fixture job(s) excluded)"
if ($NoWrite) {
    Write-Host "Mode: -NoWrite (nothing written)"
} else {
    Write-Host "Ledger: $Ledger"
}
