<#
.SYNOPSIS
    Registers (or replaces) the "DevMind Token Ledger" scheduled task: runs
    Update-TokenLedger.ps1 daily at 23:50 local time.

.DESCRIPTION
    The task runs under Windows PowerShell 5.1 as the current user with LogonType
    Interactive: it runs while you are logged on (BEAST normally is) and stores no
    password. S4U ("run whether or not logged on") was tried first and fails on
    BEAST for the domain account KAILAS\pkailas: LogonUserS4U returns
    0x80070032 ERROR_NOT_SUPPORTED (Task Scheduler event 104, 2026-10-04).

    StartWhenAvailable is on, so a run missed because the machine was off or
    asleep is caught up as soon as it is back.

.RUNS
    Run this elevated (as administrator) - the driver does that, not the ledger.
    Requires the ScheduledTasks module (built in to Windows 10/11).

.EXAMPLE
    # In an elevated Windows PowerShell prompt:
    powershell.exe -NoProfile -ExecutionPolicy Bypass -File Register-TokenLedgerTask.ps1

.NOTES
    Windows PowerShell 5.1 target: ASCII-only source, no PS7-only syntax,
    hashtable splatting only.
#>
param(
    [string]$TaskName = 'DevMind Token Ledger',
    [string]$LedgerDir = 'G:\DevMind_Tracing\ledger',
    [string]$TimeOfDay = '23:50'
)

$ErrorActionPreference = 'Stop'

$scriptPath = Join-Path $PSScriptRoot 'Update-TokenLedger.ps1'
if (-not (Test-Path -LiteralPath $scriptPath)) {
    Write-Error "Update-TokenLedger.ps1 not found next to this script: $scriptPath"
    exit 1
}

# Current user, in DOMAIN\user form, for the S4U principal.
$user = ('{0}\{1}' -f $env:USERDOMAIN, $env:USERNAME)

$action = New-ScheduledTaskAction `
    -Execute "$env:WINDIR\System32\WindowsPowerShell\v1.0\powershell.exe" `
    -Argument ('-NoProfile -ExecutionPolicy Bypass -File "{0}"' -f $scriptPath) `
    -WorkingDirectory $PSScriptRoot

$trigger = New-ScheduledTaskTrigger -Daily -At $TimeOfDay

# Interactive: runs in your logged-on session, no stored password. A run missed
# while logged off is caught up at next logon via StartWhenAvailable.
# (S4U is not supported for this domain account on BEAST - see .DESCRIPTION.)
$principal = New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Limited

$settings = New-ScheduledTaskSettingsSet `
    -StartWhenAvailable `
    -ExecutionTimeLimit ([TimeSpan]::FromMinutes(30)) `
    -AllowStartIfOnBatteries `
    -DontStopIfGoingOnBatteries `
    -MultipleInstances IgnoreNew

$registerArgs = @{
    TaskName    = $TaskName
    Action      = $action
    Trigger     = $trigger
    Principal   = $principal
    Settings    = $settings
    Description = 'Nightly DevMind token ledger harvest and rollup (Update-TokenLedger.ps1).'
    Force       = $true
}
$task = Register-ScheduledTask @registerArgs

Write-Host ("Registered task '{0}'" -f $TaskName)
Write-Host ("  user      : {0} (LogonType Interactive)" -f $user)
Write-Host ("  action    : {0} {1}" -f $action.Execute, $action.Arguments)
Write-Host ("  trigger   : daily at {0}" -f $TimeOfDay)
Write-Host ("  next run  : {0}" -f (Get-ScheduledTaskInfo -TaskName $TaskName).NextRunTime)
exit 0
