<#
.SYNOPSIS
    Logs a manual reading of the Claude plan usage meters to claude-plan-usage.csv.

.DESCRIPTION
    Settings > Usage on claude.ai shows a percentage for the current 5-hour session
    and for the current week. There is no API for these, so the numbers have to be
    read off the page by hand and typed in here. Each call appends one row.

    Header is created automatically when the file is new.

.EXAMPLE
    powershell.exe -NoProfile -ExecutionPolicy Bypass -File Add-PlanUsage.ps1 -SessionPct 42 -WeeklyPct 61

.EXAMPLE
    Add-PlanUsage.ps1 -SessionPct 42 -WeeklyPct 61 -Note 'after the big refactor run'

.NOTES
    Windows PowerShell 5.1 target: ASCII-only source, no PS7-only syntax.
#>
param(
    [Parameter(Mandatory = $true)]
    [ValidateRange(0, 100)]
    [int]$SessionPct,

    [Parameter(Mandatory = $true)]
    [ValidateRange(0, 100)]
    [int]$WeeklyPct,

    [string]$Note = '',
    [string]$LedgerDir = 'G:\DevMind_Tracing\ledger'
)

$ErrorActionPreference = 'Stop'

try {
    New-Item -Path $LedgerDir -ItemType Directory -Force | Out-Null

    $path = Join-Path $LedgerDir 'claude-plan-usage.csv'
    $isNew = -not (Test-Path -LiteralPath $path)

    $entry = [pscustomobject]@{
        TimestampLocal = (Get-Date).ToString('yyyy-MM-dd HH:mm')
        SessionPct     = $SessionPct
        WeeklyPct      = $WeeklyPct
        Note           = $Note
    }

    if ($isNew) {
        # Append-Csv writes the header only when the target is empty or absent,
        # so a brand new file gets a header and an existing one does not.
        $entry | Export-Csv -LiteralPath $path -NoTypeInformation -Encoding ASCII
    } else {
        $entry | Export-Csv -LiteralPath $path -NoTypeInformation -Encoding ASCII -Append
    }

    Write-Host ("Logged: {0} session={1}% weekly={2}%{3} -> {4}" -f `
        $entry.TimestampLocal, $SessionPct, $WeeklyPct, `
        $(if ($Note) { " note='$Note'" } else { '' }), $path)
    exit 0
} catch {
    Write-Error "Could not log plan usage: $($_.Exception.Message)"
    exit 1
}
