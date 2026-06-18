<#
.SYNOPSIS
    Deploys the DevMind TUI to a target directory and (optionally) registers it on PATH.

.DESCRIPTION
    Copies the published DevMind build -- devmind.tui.exe, devmind.cmd, and ALL of its
    dependencies (DLLs, *.deps.json, *.runtimeconfig.json, etc.) -- from -Source to
    -Target, then optionally adds -Target to PATH so `devmind` runs from any console.

    The whole source folder is copied, not just the two named files, because the .NET
    executable will not run without its dependency set alongside it.

.PARAMETER Source
    The publish/build output folder that contains devmind.tui.exe and devmind.cmd.
    e.g. .\src\DevMind.Tui\bin\Release\net9.0\publish

.PARAMETER Target
    Install directory. Local path (C:\Tools\DevMind) or UNC (\\win-tcdev\C$\Tools\DevMind).

.PARAMETER AddToPath
    Register -Target on PATH if it isn't already there. (Local targets only.)

.PARAMETER Scope
    PATH scope used with -AddToPath: User (default) or Machine (Machine requires elevation).

.EXAMPLE
    # Local install for the current user
    .\deploy-devmind.ps1 -Source .\publish -Target "$env:LOCALAPPDATA\DevMind" -AddToPath

.EXAMPLE
    # Push to the VM over the admin share (run PATH update separately on that box)
    .\deploy-devmind.ps1 -Source .\publish -Target \\win-tcdev\C$\Tools\DevMind

.EXAMPLE
    # Dry run -- show what would happen without changing anything
    .\deploy-devmind.ps1 -Source .\publish -Target C:\Tools\DevMind -AddToPath -WhatIf
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)] [string] $Source,
    [Parameter(Mandatory)] [string] $Target,
    [switch] $AddToPath,
    [ValidateSet('User','Machine')] [string] $Scope = 'User'
)

$ErrorActionPreference = 'Stop'

# --- Validate source -------------------------------------------------------
$Source = (Resolve-Path -LiteralPath $Source).Path
foreach ($f in @('devmind.tui.exe','devmind.cmd')) {
    if (-not (Test-Path (Join-Path $Source $f))) {
        throw "Required file '$f' not found in source: $Source"
    }
}

# --- Runtime sanity note (framework-dependent builds) ----------------------
$rc = Get-ChildItem -LiteralPath $Source -Filter '*.runtimeconfig.json' -ErrorAction SilentlyContinue |
      Select-Object -First 1
if ($rc) {
    try {
        $tfm = (Get-Content -LiteralPath $rc.FullName -Raw | ConvertFrom-Json).runtimeOptions.tfm
        if ($tfm) {
            Write-Host "Build targets $tfm. If this is a framework-dependent publish, the matching .NET runtime must be installed on the target machine." -ForegroundColor Yellow
        }
    } catch { }  # non-fatal -- just an advisory
}

# --- Elevation check for machine-wide PATH ---------------------------------
if ($AddToPath -and $Scope -eq 'Machine') {
    $admin = ([Security.Principal.WindowsPrincipal] `
              [Security.Principal.WindowsIdentity]::GetCurrent()
             ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    if (-not $admin) { throw "Machine PATH scope requires an elevated (Administrator) PowerShell." }
}

# --- Create target ---------------------------------------------------------
if ($PSCmdlet.ShouldProcess($Target, 'Create install directory')) {
    New-Item -ItemType Directory -Path $Target -Force | Out-Null
}

# --- Copy payload ----------------------------------------------------------
Write-Host "Deploying DevMind:`n  from  $Source`n  to    $Target"
if ($PSCmdlet.ShouldProcess($Target, 'Copy DevMind files')) {
    Copy-Item -Path (Join-Path $Source '*') -Destination $Target -Recurse -Force
    $count = (Get-ChildItem -LiteralPath $Target -Recurse -File).Count
    Write-Host "Copied $count file(s)." -ForegroundColor Green
}

# --- PATH registration (idempotent) ----------------------------------------
if ($AddToPath) {
    if ($Target -like '\\*') {
        Write-Warning "Target is a UNC path; skipping PATH update. Add a local path to PATH on the target machine instead."
    }
    else {
        $envTarget = if ($Scope -eq 'Machine') {
            [EnvironmentVariableTarget]::Machine
        } else {
            [EnvironmentVariableTarget]::User
        }
        $current = [Environment]::GetEnvironmentVariable('Path', $envTarget)
        $parts   = @($current -split ';' | Where-Object { $_ -ne '' })
        if ($parts -notcontains $Target) {
            if ($PSCmdlet.ShouldProcess("$Scope PATH", "Add $Target")) {
                $newPath = ($parts + $Target) -join ';'
                [Environment]::SetEnvironmentVariable('Path', $newPath, $envTarget)
                Write-Host "Added to $Scope PATH. Open a NEW console for it to take effect." -ForegroundColor Green
            }
        }
        else {
            Write-Host "$Target is already on the $Scope PATH."
        }
    }
}

# --- Summary ---------------------------------------------------------------
Write-Host "`nDone." -ForegroundColor Green
if (-not $AddToPath) {
    Write-Host "Run it directly with:  `"$Target\devmind.cmd`""
} else {
    Write-Host "Run 'devmind' from a new console."
}
