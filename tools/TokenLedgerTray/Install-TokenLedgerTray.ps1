<#
.SYNOPSIS
    Builds and installs the DevMind Token Ledger tray app so it starts with the
    current user's Windows session.

.DESCRIPTION
    1. Stops any TokenLedgerTray process that is already running.
    2. dotnet publish (framework-dependent, win-x64) into %LOCALAPPDATA%\DevMind\TokenLedgerTray.
    3. Creates or replaces a "DevMind Token Ledger.lnk" shortcut in the user's
       Startup folder pointing at the published exe.
    4. Starts the tray app.

    Nothing here is written to the ledger folder; the tray reads it.

    Windows PowerShell 5.1 target: ASCII-only source, no PS7-only syntax,
    hashtable splatting only.

.EXAMPLE
    powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools\TokenLedgerTray\Install-TokenLedgerTray.ps1
#>
[CmdletBinding()]
param(
    [string]$CsprojPath = '',
    [string]$InstallDir = (Join-Path $env:LOCALAPPDATA 'DevMind\TokenLedgerTray'),
    [switch]$NoStart
)

$ErrorActionPreference = 'Stop'

# Default the csproj to the one next to this script, so the script works from
# any working directory. $PSScriptRoot is the script's own folder.
if ([string]::IsNullOrWhiteSpace($CsprojPath)) {
    $CsprojPath = Join-Path $PSScriptRoot 'TokenLedgerTray.csproj'
}

function Write-Step {
    param([Parameter(Mandatory = $true)][string]$Message)
    Write-Host $Message
}

# --------------------------------------------------------------------------
# 1. Stop a running tray so the publish can overwrite the exe.
# --------------------------------------------------------------------------
$running = @(Get-Process -Name 'TokenLedgerTray' -ErrorAction SilentlyContinue)
if (@($running).Count -gt 0) {
    Write-Step ("Stopping {0} running TokenLedgerTray process(es)." -f @($running).Count)
    foreach ($proc in $running) {
        # Kill() rather than Stop-Process: Stop-Process can fail on a GUI process
        # that is mid-message-loop; Kill is unconditional.
        try {
            $proc.Kill()
            $proc.WaitForExit(5000) | Out-Null
        } catch {
            Write-Step ("  (could not stop pid {0}: {1})" -f $proc.Id, $_.Exception.Message)
        }
    }
    # Give the named mutex a moment to be released.
    Start-Sleep -Milliseconds 500
} else {
    Write-Step 'No TokenLedgerTray process running.'
}

if (-not (Test-Path -LiteralPath $CsprojPath)) {
    Write-Error "csproj not found: $CsprojPath"
    exit 1
}

# --------------------------------------------------------------------------
# 2. Publish. Framework-dependent (-r win-x64, --self-contained false).
# --------------------------------------------------------------------------
Write-Step "Publishing $CsprojPath -> $InstallDir"
New-Item -Path $InstallDir -ItemType Directory -Force | Out-Null

$publishArgs = @{
    FilePath               = 'dotnet'
    ArgumentList           = @(
        'publish',
        $CsprojPath,
        '-c', 'Release',
        '-r', 'win-x64',
        '--self-contained', 'false',
        '-o', $InstallDir
    )
    NoNewWindow            = $true
    Wait                   = $true
    PassThru               = $true
}
$pub = Start-Process @publishArgs
if ($pub.ExitCode -ne 0) {
    Write-Error "dotnet publish failed with exit code $($pub.ExitCode)"
    exit 1
}

$exePath = Join-Path $InstallDir 'TokenLedgerTray.exe'
if (-not (Test-Path -LiteralPath $exePath)) {
    Write-Error "publish did not produce $exePath"
    exit 1
}
Write-Step "Published: $exePath"

# --------------------------------------------------------------------------
# 3. Startup shortcut via WScript.Shell COM. Create or replace.
# --------------------------------------------------------------------------
$startupDir = [Environment]::GetFolderPath('Startup')
$lnkPath = Join-Path $startupDir 'DevMind Token Ledger.lnk'

Write-Step "Writing startup shortcut: $lnkPath"
$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($lnkPath)
$shortcut.TargetPath = $exePath
$shortcut.WorkingDirectory = $InstallDir
$shortcut.Description = 'DevMind Token Ledger tray'
$shortcut.Save()

# Release the COM object deterministically; leaving it alive can keep the .lnk
# write buffered on some systems.
[System.Runtime.InteropServices.Marshal]::ReleaseComObject($shortcut) | Out-Null
[System.Runtime.InteropServices.Marshal]::ReleaseComObject($shell) | Out-Null
[GC]::Collect()
[GC]::WaitForPendingFinalizers()

Write-Step "Startup entry created (or replaced)."

# --------------------------------------------------------------------------
# 4. Start the tray.
# --------------------------------------------------------------------------
if ($NoStart) {
    Write-Step '-NoStart: not launching. It will start at next logon.'
} else {
    Write-Step "Starting $exePath"
    $startArgs = @{
        FilePath     = $exePath
        WorkingDirectory = $InstallDir
        PassThru     = $true
    }
    $started = Start-Process @startArgs
    if ($null -ne $started) {
        Write-Step ("Tray started (pid {0})." -f $started.Id)
    } else {
        Write-Step 'Tray launched (no process object returned).'
    }
}

Write-Step 'Done.'
