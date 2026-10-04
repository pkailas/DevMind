<#
.SYNOPSIS
    Harvests DevMind headless job sidecars into a durable per-job ledger and
    recomputes the daily rollups. Safe to run as often as you like.

.DESCRIPTION
    Every headless job writes %LOCALAPPDATA%\Temp\devmind\tasks\<job-id>.result.json,
    and %TEMP% gets cleaned, so this script keeps its own durable copy of every job
    it has ever harvested in <LedgerDir>\jobs.csv, then derives everything else from
    that one file.

      a) Harvest  - each *.result.json upserts one row per job_id into jobs.csv.
                    Rows whose sidecar has since vanished are KEPT. Unparseable
                    sidecars are skipped with a warning. jobs.csv is written
                    atomically (.tmp then Move-Item -Force).
      b) daily.csv         - recomputed entirely from jobs.csv.
      c) daily-by-repo.csv - recomputed entirely from jobs.csv.
      d) ledger.log        - one appended line per run.
      e) dashboard.html    - one self-contained HTML page built from
                             dashboard.template.html with the rollups embedded as
                             JSON. Written atomically, and a failure here never
                             fails the run (see section e).

    Dates bucket on the MACHINE LOCAL date of ended_at_utc (UTC), because the ledger
    answers "what did I burn today".

.NOTES
    Windows PowerShell 5.1 target: ASCII-only source, no PS7-only syntax, hashtable
    splatting only, pipeline results wrapped in @() before .Count or indexing.
    Deliberately no Set-StrictMode: pre-Oct-2026 jobs have no tokens_in_total
    property at all, so reading a missing property is normal here, not a bug.
    See README.md for the price parameters and column definitions.
#>
param(
    [string]$TasksDir = (Join-Path $env:LOCALAPPDATA 'Temp\devmind\tasks'),
    [string]$LedgerDir = 'G:\DevMind_Tracing\ledger',
    [decimal]$InputPerM = 4,        # USD per 1M uncached input tokens
    [decimal]$OutputPerM = 20,      # USD per 1M output tokens
    [decimal]$CacheWritePerM = 5,   # USD per 1M cache-write input tokens
    [decimal]$CacheHitPerM = 0.20   # USD per 1M cache-hit input tokens
)

$ErrorActionPreference = 'Stop'

function Get-Field {
    # Value of a JSON property, or $null when the property is absent or null.
    # Not Mandatory: [Parameter(Mandatory)] on an [object] parameter rejects
    # $null in 5.1, and unwrapping the optional { "result": { ... } } wrapper
    # passes $null for the (much more common) flat shape.
    param(
        [AllowNull()][object]$Object,
        [Parameter(Mandatory = $true)][string]$Name
    )
    if ($null -eq $Object) { return $null }
    if ($null -eq $Object.PSObject.Properties[$Name]) { return $null }
    return $Object.$Name
}

function Convert-JsonNumber {
    # JSON number to decimal; $null for absent/null/blank so "unknown" never
    # becomes a reported 0.
    param(
        [AllowNull()][object]$Value
    )
    if ($null -eq $Value) { return $null }
    return [decimal]$Value
}

function Convert-JsonBool {
    param(
        [AllowNull()][object]$Value
    )
    if ($null -eq $Value) { return '' }
    if ($Value) { return 'true' }
    return 'false'
}

# --------------------------------------------------------------------------
# Ledger dir must exist and be writable. That is the only failure worth a
# non-zero exit for.
# --------------------------------------------------------------------------
$jobsPath = Join-Path $LedgerDir 'jobs.csv'
$dailyPath = Join-Path $LedgerDir 'daily.csv'
$repoPath = Join-Path $LedgerDir 'daily-by-repo.csv'
$logPath = Join-Path $LedgerDir 'ledger.log'
$planPath = Join-Path $LedgerDir 'claude-plan-usage.csv'
$dashboardPath = Join-Path $LedgerDir 'dashboard.html'
$templatePath = Join-Path $PSScriptRoot 'dashboard.template.html'

New-Item -Path $LedgerDir -ItemType Directory -Force | Out-Null
if (-not (Test-Path -LiteralPath $LedgerDir)) {
    Write-Error "Ledger directory does not exist and could not be created: $LedgerDir"
    exit 1
}
$probe = Join-Path $LedgerDir ('.write-probe-{0}.tmp' -f [guid]::NewGuid().ToString('N'))
try {
    Set-Content -LiteralPath $probe -Value 'probe' -Encoding ASCII
    Remove-Item -LiteralPath $probe -Force
} catch {
    Write-Error "Ledger directory is not writable ($LedgerDir): $($_.Exception.Message)"
    exit 1
}

# --------------------------------------------------------------------------
# a) Harvest
# --------------------------------------------------------------------------
$known = @{}
if (Test-Path -LiteralPath $jobsPath) {
    foreach ($row in @(Import-Csv -LiteralPath $jobsPath)) {
        if ($row.JobId) { $known[$row.JobId] = $row }
    }
}
$beforeCount = @($known.Keys).Count

$sidecars = @()
if (Test-Path -LiteralPath $TasksDir) {
    $sidecars = @(Get-ChildItem -LiteralPath $TasksDir -Filter '*.result.json' -File | Sort-Object Name)
} else {
    Write-Warning "Tasks directory not found: $TasksDir (existing ledger rows are kept)"
}

$parseWarnings = 0
$newJobs = 0
$updatedJobs = 0

foreach ($file in $sidecars) {
    try {
        $parsed = ConvertFrom-Json ([System.IO.File]::ReadAllText($file.FullName))
    } catch {
        $parseWarnings++
        Write-Warning ("Skipping unparseable sidecar {0}: {1}" -f $file.Name, $_.Exception.Message)
        continue
    }

    # Sidecars are either the result object itself or wrapped as { "result": {...} }.
    $r = Get-Field -Object $parsed -Name 'result'
    if ($null -eq $r) { $r = $parsed }

    $jobId = [string](Get-Field -Object $r -Name 'job_id')
    if (-not $jobId) {
        $parseWarnings++
        Write-Warning "Skipping sidecar with no job_id: $($file.Name)"
        continue
    }

    $endedUtc = [string](Get-Field -Object $r -Name 'ended_at_utc')
    $endedLocalDate = ''
    if ($endedUtc) {
        $dt = [datetime]::MinValue
        $ok = [datetime]::TryParseExact($endedUtc, 'yyyy-MM-dd HH:mm:ss',
            [System.Globalization.CultureInfo]::InvariantCulture,
            [System.Globalization.DateTimeStyles]::AssumeUniversal, [ref]$dt)
        if ($ok) { $endedLocalDate = $dt.ToLocalTime().ToString('yyyy-MM-dd') }
    }

    $workingDir = [string](Get-Field -Object $r -Name 'working_dir')
    $repo = ''
    if ($workingDir) { $repo = Split-Path -Path $workingDir.TrimEnd('\', '/') -Leaf }

    $total = Convert-JsonNumber (Get-Field -Object $r -Name 'tokens_in_total')
    $new = Convert-JsonNumber (Get-Field -Object $r -Name 'tokens_in_new')
    $out = Convert-JsonNumber (Get-Field -Object $r -Name 'tokens_out')

    # Blank, not 0, when the job predates token reporting.
    $row = [pscustomobject]@{
        JobId          = $jobId
        ParentJobId    = [string](Get-Field -Object $r -Name 'parent_job_id')
        EndedUtc       = $endedUtc
        EndedLocalDate = $endedLocalDate
        Repo           = $repo
        WorkingDir     = $workingDir
        State          = [string](Get-Field -Object $r -Name 'state')
        HitDepthCap    = Convert-JsonBool (Get-Field -Object $r -Name 'hit_depth_cap')
        Iterations     = [string](Get-Field -Object $r -Name 'iterations')
        ElapsedSeconds = [string](Get-Field -Object $r -Name 'elapsed_seconds')
        TokensInTotal  = [string]$total
        TokensInNew    = [string]$new
        TokensOut      = [string]$out
    }

    if ($known.ContainsKey($jobId)) { $updatedJobs++ } else { $newJobs++ }
    $known[$jobId] = $row
}

# jobs.csv atomically: .tmp then Move-Item -Force.
#
# Sorted deliberately: $known is a hashtable, and its .Values order is unspecified,
# so a raw dump reshuffles rows on every run and makes the file undiffable. EndedUtc
# then JobId is a total order (JobId is the key and is unique), so two runs over the
# same jobs produce byte-identical output. Rows with no parseable EndedUtc sort first
# and break the tie on JobId. This is what makes jobs.csv SHA256-stable between runs.
$jobsTmp = "$jobsPath.tmp"
@($known.Values | Sort-Object -Property @{ Expression = { [string]($_.EndedUtc) } },
                              @{ Expression = { [string]($_.JobId) } }) |
    Export-Csv -LiteralPath $jobsTmp -NoTypeInformation -Encoding ASCII
Move-Item -LiteralPath $jobsTmp -Destination $jobsPath -Force

# --------------------------------------------------------------------------
# Re-read the durable ledger so every rollup is derived from jobs.csv itself.
# --------------------------------------------------------------------------
$rows = @(Import-Csv -LiteralPath $jobsPath)

$usage = @($rows | Where-Object { $_.TokensInTotal -or $_.TokensInNew -or $_.TokensOut })
$noUsage = @($rows | Where-Object { -not ($_.TokensInTotal -or $_.TokensInNew -or $_.TokensOut) })

foreach ($row in $rows) {
    # Cached = Total - New, per job, only where both are known.
    if ($row.TokensInTotal -and $row.TokensInNew) {
        $row | Add-Member -NotePropertyName TokensInCached -NotePropertyValue ([decimal]$row.TokensInTotal - [decimal]$row.TokensInNew) -Force
    } else {
        $row | Add-Member -NotePropertyName TokensInCached -NotePropertyValue $null -Force
    }
    $row | Add-Member -NotePropertyName HasUsage -NotePropertyValue (
        [bool]($row.TokensInTotal -or $row.TokensInNew -or $row.TokensOut)
    ) -Force
}

# --------------------------------------------------------------------------
# b) daily.csv
# --------------------------------------------------------------------------
$perM = [decimal]1000000
$daily = @()
foreach ($group in ($rows | Group-Object -Property EndedLocalDate | Sort-Object Name)) {
    $g = @($group.Group)
    $withUsage = @($g | Where-Object { $_.HasUsage })
    $sumIn = ($withUsage | Measure-Object -Property TokensInTotal -Sum).Sum
    $sumNew = ($withUsage | Measure-Object -Property TokensInNew -Sum).Sum
    $sumCached = ($withUsage | Measure-Object -Property TokensInCached -Sum).Sum
    $sumOut = ($withUsage | Measure-Object -Property TokensOut -Sum).Sum
    $sumElapsed = ($g | Measure-Object -Property ElapsedSeconds -Sum).Sum
    $sumIter = ($g | Measure-Object -Property Iterations -Sum).Sum
    if ($null -eq $sumElapsed) { $sumElapsed = 0 }
    if ($null -eq $sumIter) { $sumIter = 0 }

    # No usage data that day: token and $ columns stay BLANK, not 0. Unknown is
    # not zero, same rule as jobs.csv. Money is 2 decimals where it exists.
    if (@($withUsage).Count -eq 0) {
        $colIn = ''; $colNew = ''; $colCached = ''; $colOut = ''; $colUsdCached = ''; $colUsdNoCache = ''
    } else {
        if ($null -eq $sumIn) { $sumIn = 0 }
        if ($null -eq $sumNew) { $sumNew = 0 }
        if ($null -eq $sumCached) { $sumCached = 0 }
        if ($null -eq $sumOut) { $sumOut = 0 }
        $usdCached = ($sumNew * $CacheWritePerM) + ($sumCached * $CacheHitPerM) + ($sumOut * $OutputPerM)
        $usdNoCache = ($sumIn * $InputPerM) + ($sumOut * $OutputPerM)
        $colIn = ($sumIn -as [decimal]).ToString([System.Globalization.CultureInfo]::InvariantCulture)
        $colNew = ($sumNew -as [decimal]).ToString([System.Globalization.CultureInfo]::InvariantCulture)
        $colCached = ($sumCached -as [decimal]).ToString([System.Globalization.CultureInfo]::InvariantCulture)
        $colOut = ($sumOut -as [decimal]).ToString([System.Globalization.CultureInfo]::InvariantCulture)
        # [math]::Round returns an INT when handed one, so a plain 0 would print
        # as "0" not "0.00"; -as [decimal] forces the decimal overload.
        $colUsdCached = [math]::Round((($usdCached / $perM) -as [decimal]), 2).ToString('0.00', [System.Globalization.CultureInfo]::InvariantCulture)
        $colUsdNoCache = [math]::Round((($usdNoCache / $perM) -as [decimal]), 2).ToString('0.00', [System.Globalization.CultureInfo]::InvariantCulture)
    }

    $daily += [pscustomobject]@{
        Date             = $group.Name
        Jobs             = $g.Count
        Done             = @($g | Where-Object { $_.State -eq 'done' }).Count
        StoppedIncomplete= @($g | Where-Object { $_.State -eq 'stopped_incomplete' }).Count
        Cancelled        = @($g | Where-Object { $_.State -eq 'cancelled' }).Count
        Failed           = @($g | Where-Object { $_.State -eq 'failed' }).Count
        OtherState       = @($g | Where-Object { $_.State -notin @('done', 'stopped_incomplete', 'cancelled', 'failed') }).Count
        JobsWithUsage    = $withUsage.Count
        JobsWithoutUsage = @($g | Where-Object { -not $_.HasUsage }).Count
        TokensInTotal    = $colIn
        TokensInNew      = $colNew
        TokensInCached   = $colCached
        TokensOut        = $colOut
        Iterations       = [long]$sumIter
        AgentMinutes     = [math]::Round(($sumElapsed -as [decimal]) / 60, 1)
        OpusEquivUsdCached   = $colUsdCached
        OpusEquivUsdNoCache  = $colUsdNoCache
    }
}
$dailyTmp = "$dailyPath.tmp"
$daily | Export-Csv -LiteralPath $dailyTmp -NoTypeInformation -Encoding ASCII
Move-Item -LiteralPath $dailyTmp -Destination $dailyPath -Force

# --------------------------------------------------------------------------
# c) daily-by-repo.csv
# --------------------------------------------------------------------------
$byRepo = @()
foreach ($group in ($rows | Group-Object -Property EndedLocalDate, Repo | Sort-Object Name)) {
    $g = @($group.Group)
    $withUsage = @($g | Where-Object { $_.HasUsage })
    $sumIn = ($withUsage | Measure-Object -Property TokensInTotal -Sum).Sum
    $sumOut = ($withUsage | Measure-Object -Property TokensOut -Sum).Sum
    $sumElapsed = ($g | Measure-Object -Property ElapsedSeconds -Sum).Sum
    if ($null -eq $sumElapsed) { $sumElapsed = 0 }
    # Blank when none of that day's jobs for that repo have usage.
    if (@($withUsage).Count -eq 0) {
        $colIn = ''
        $colOut = ''
    } else {
        if ($null -eq $sumIn) { $sumIn = 0 }
        if ($null -eq $sumOut) { $sumOut = 0 }
        $colIn = ($sumIn -as [decimal]).ToString([System.Globalization.CultureInfo]::InvariantCulture)
        $colOut = ($sumOut -as [decimal]).ToString([System.Globalization.CultureInfo]::InvariantCulture)
    }
    $byRepo += [pscustomobject]@{
        Date          = $g[0].EndedLocalDate
        Repo          = $g[0].Repo
        Jobs          = $g.Count
        TokensInTotal = $colIn
        TokensOut     = $colOut
        AgentMinutes  = [math]::Round(($sumElapsed -as [decimal]) / 60, 1)
    }
}
$repoTmp = "$repoPath.tmp"
$byRepo | Export-Csv -LiteralPath $repoTmp -NoTypeInformation -Encoding ASCII
Move-Item -LiteralPath $repoTmp -Destination $repoPath -Force

# --------------------------------------------------------------------------
# d) ledger.log
# --------------------------------------------------------------------------
$summary = '{0} | sidecars_read={1} | new_jobs={2} | jobs_total={3} | parse_warnings={4}' -f `
    (Get-Date).ToString('yyyy-MM-dd HH:mm:ss'), $sidecars.Count, $newJobs, $rows.Count, $parseWarnings
Add-Content -LiteralPath $logPath -Value $summary -Encoding ASCII

# --------------------------------------------------------------------------
# e) dashboard.html
#
# Read dashboard.template.html, embed everything above as one JSON object in place
# of the placeholder line, write dashboard.html atomically. A single line in a
# UTF-8-without-BOM file is enough to keep the page self-contained: no external
# URLs, CDNs, fonts or images, so it renders offline from file:// in Edge.
#
# Wrapped in try/catch on purpose: the CSVs above are the ledger, the page is a
# convenience view over them. A template problem must not cost the harvest.
# --------------------------------------------------------------------------
$dashboardNote = ''
try {
    if (-not (Test-Path -LiteralPath $templatePath)) {
        throw "template not found: $templatePath"
    }

    function Convert-DashboardRow {
        # Import-Csv rows are all strings; blank means unknown, and unknown has to
        # reach the page as JSON null, never as 0. Anything numeric becomes a real
        # JSON number so the page can sort, sum and plot it without re-parsing.
        param(
            [Parameter(Mandatory = $true)][object]$Row
        )
        # Start from an empty object and add one property per CSV column, so the
        # column set follows whatever the CSV actually has.
        $out = New-Object psobject
        foreach ($p in $Row.PSObject.Properties) {
            $v = [string]$p.Value
            # The two-arg TryParse(string, ref double) overload is .NET Core only.
            # Under 5.1's .NET Framework it binds to (string, NumberStyles, IFormatProvider,
            # ref double) - pass that shape explicitly so it compiles on either runtime.
            $n = [double]0
            $numberStyles = [System.Globalization.NumberStyles]::Float
            $parsed = [double]::TryParse($v, $numberStyles,
                [System.Globalization.CultureInfo]::InvariantCulture, [ref]$n)
            if ($v -ne '' -and $parsed) {
                $out | Add-Member -NotePropertyName $p.Name -NotePropertyValue $n -Force
            } else {
                $out | Add-Member -NotePropertyName $p.Name -NotePropertyValue $(if ($v -eq '') { $null } else { $v }) -Force
            }
        }
        return $out
    }

    $dashboardData = [pscustomobject]@{
        generatedLocal = (Get-Date).ToString('yyyy-MM-dd HH:mm')
        prices = [pscustomobject]@{
            inputPerM      = [double]$InputPerM
            outputPerM     = [double]$OutputPerM
            cacheWritePerM = [double]$CacheWritePerM
            cacheHitPerM   = [double]$CacheHitPerM
        }
        daily = @(foreach ($r in $daily) { Convert-DashboardRow $r })
        byRepo = @(foreach ($r in $byRepo) { Convert-DashboardRow $r })
        planUsage = @()
    }
    if (Test-Path -LiteralPath $planPath) {
        $dashboardData.planUsage = @(foreach ($r in @(Import-Csv -LiteralPath $planPath)) { Convert-DashboardRow $r })
    }

    $json = ConvertTo-Json -InputObject $dashboardData -Depth 5 -Compress
    # A '</script>' inside a JSON string would close the data block early. ConvertTo-Json
    # in 5.1 already escapes '<' as \u003c, so this is belt-and-braces: a backslash
    # before '/' is a legal JSON escape that leaves the parsed value untouched.
    $json = $json.Replace('</', '<\/')

    $templateText = [System.IO.File]::ReadAllText($templatePath)
    if ($templateText.IndexOf('/*__LEDGER_DATA__*/') -lt 0) {
        throw "placeholder '/*__LEDGER_DATA__*/' missing from $templatePath"
    }
    $page = $templateText.Replace('/*__LEDGER_DATA__*/', $json)

    # UTF-8 without BOM: a BOM lands before <!DOCTYPE html> and Edge drops the page
    # into quirks mode, which is exactly what breaks the responsive layout.
    $utf8NoBom = New-Object System.Text.UTF8Encoding($false)
    $dashboardTmp = "$dashboardPath.tmp"
    [System.IO.File]::WriteAllText($dashboardTmp, $page, $utf8NoBom)
    Move-Item -LiteralPath $dashboardTmp -Destination $dashboardPath -Force

    $dashboardNote = ' dashboard_ok'
} catch {
    $dashboardNote = " dashboard_FAILED"
    $line = 'dashboard: FAILED {0}' -f $_.Exception.Message
    try {
        Add-Content -LiteralPath $logPath -Value $line -Encoding ASCII
    } catch {
        # Nowhere left to log it; the dashboard is still not worth failing the run.
        Write-Warning $line
    }
    Write-Warning $line
}

Write-Host $summary
Write-Host ("updated_jobs={0} jobs_before={1} rows_with_usage={2} rows_without_usage={3}{4}" -f `
    $updatedJobs, $beforeCount, $usage.Count, $noUsage.Count, $dashboardNote)
exit 0
