<#
.SYNOPSIS
    Install DevMind from scratch on a fresh Windows machine.

.DESCRIPTION
    Bootstrap installer that:
      - Checks for git and .NET 10 SDK prerequisites (installs .NET 10 via winget if missing)
      - Clones or updates the DevMind repository
      - Publishes self-contained builds by calling the repo's run-deploy.ps1
      - Ensures the 'dm' short alias launcher exists
      - Adds dist\ to the user PATH
      - Registers the MCP server with Claude Code (if claude CLI is available)
      - Installs netcoredbg debug adapter (optional, skip with -SkipDebugger)

    Idempotent and safe to re-run.

.EXAMPLE
    .\install.ps1
    # Uses defaults: clones to %USERPROFILE%\source\repos\DevMind, installs debugger.

.EXAMPLE
    .\install.ps1 -InstallDir C:\Projects\DevMind -SkipDebugger
    # Installs to a custom directory, skips netcoredbg.
#>

[CmdletBinding()]
param(
    [string]$InstallDir  = "$env:USERPROFILE\source\repos\DevMind",
    [string]$RepoUrl     = 'https://github.com/pkailas/DevMind.git',
    [string]$Branch      = 'master',
    [switch]$SkipDebugger
)

$ErrorActionPreference = 'Stop'

# ── Helpers ───────────────────────────────────────────────────────────────────

function Write-Step($message) {
    Write-Host "`n>> $message" -ForegroundColor Cyan
}

function Write-Ok($message) {
    Write-Host "   $message" -ForegroundColor Green
}

function Write-Warn($message) {
    Write-Host "   WARNING: $message" -ForegroundColor Yellow
}

# ── 1. Prerequisite: git ─────────────────────────────────────────────────────

Write-Step 'Checking for git...'

if (-not (Get-Command git -ErrorAction SilentlyContinue)) {
    throw "git is not found on PATH. Please install Git for Windows from https://git-scm.com/download/win and retry."
}

Write-Ok "git found: $(git --version)"

# ── 2. Prerequisite: .NET 10 SDK ─────────────────────────────────────────────

Write-Step 'Checking for .NET 10 SDK...'

$dotnetManual = @"
The .NET 10 SDK is required but was not found, and it could not be installed automatically.

Install it manually, then re-run install.ps1:
  https://dotnet.microsoft.com/download/dotnet/10.0   (choose the x64 SDK installer)

Note (Windows Server): winget / App Installer is often absent on Server. Either install
the .NET 10 SDK directly from the link above, or sideload winget from
https://github.com/microsoft/winget-cli/releases and re-run install.ps1.
"@

$dotnetInstalled = $false
$dotnetFound     = $false

if (Get-Command dotnet -ErrorAction SilentlyContinue) {
    $dotnetFound = $true
    $sdks = dotnet --list-sdks 2>$null
    if ($sdks -match '^10\.') {
        $dotnetInstalled = $true
        $matchedSdk = ($sdks | Where-Object { $_ -match '^10\.' })[0]
        Write-Ok ".NET 10 SDK found: $matchedSdk"
    }
}

if (-not $dotnetInstalled) {
    $winget = Get-Command winget -ErrorAction SilentlyContinue
    if (-not $winget) {
        throw $dotnetManual
    }

    if (-not $dotnetFound) {
        Write-Step 'dotnet not found on PATH - installing via winget...'
    }
    else {
        Write-Step '.NET 10 SDK not installed - installing via winget...'
    }

    winget install --id Microsoft.DotNet.SDK.10 -e --source winget `
        --accept-source-agreements --accept-package-agreements

    if ($LASTEXITCODE -ne 0) {
        throw $dotnetManual
    }

    # Re-check after install
    $sdks = dotnet --list-sdks 2>$null
    if ($sdks -match '^10\.') {
        $matchedSdk = ($sdks | Where-Object { $_ -match '^10\.' })[0]
        Write-Ok ".NET 10 SDK installed: $matchedSdk"
    }
    else {
        throw $dotnetManual
    }
}

# ── 3. Clone or update the repo ──────────────────────────────────────────────

Write-Step "Preparing repository at $InstallDir..."

if (-not (Test-Path $InstallDir)) {
    Write-Host "   Cloning repository..." -ForegroundColor Cyan
    git clone --branch $Branch $RepoUrl $InstallDir
    if ($LASTEXITCODE -ne 0) { throw "git clone failed (exit $LASTEXITCODE)" }
    Write-Ok 'Repository cloned.'
}
elseif (Test-Path (Join-Path $InstallDir '.git')) {
    # Already a git repo - fetch, checkout branch, pull
    git -C $InstallDir fetch origin
    if ($LASTEXITCODE -ne 0) { throw "git fetch failed (exit $LASTEXITCODE)" }

    git -C $InstallDir checkout $Branch
    if ($LASTEXITCODE -ne 0) { throw "git checkout $Branch failed (exit $LASTEXITCODE)" }

    git -C $InstallDir pull --ff-only origin $Branch
    if ($LASTEXITCODE -ne 0) {
        Write-Warn "Fast-forward pull failed (may have local changes). Continuing with current state."
    }

    Write-Ok 'Repository updated.'
}
else {
    throw "$InstallDir exists but is not a git repository. Please remove it or choose a different InstallDir."
}

# ── 4. Publish via run-deploy.ps1 ────────────────────────────────────────────

Write-Step 'Publishing DevMind (self-contained, single-file)...'

$deployScript = Join-Path $InstallDir 'run-deploy.ps1'
if (-not (Test-Path $deployScript)) {
    throw "Deploy script not found at $deployScript - repository may be incomplete."
}

& $deployScript
if ($LASTEXITCODE -ne 0) { throw "run-deploy.ps1 failed (exit $LASTEXITCODE)" }

Write-Ok 'Publish complete.'

# ── 5. Ensure dist\dm.cmd alias exists ───────────────────────────────────────

$distDir   = Join-Path $InstallDir 'dist'
$devmindCmd = Join-Path $distDir 'devmind.cmd'
$dmCmd      = Join-Path $distDir 'dm.cmd'

if (-not (Test-Path $dmCmd) -and (Test-Path $devmindCmd)) {
    Write-Step 'Creating dm.cmd short alias...'
    Copy-Item $devmindCmd $dmCmd
    Write-Ok 'dm.cmd created.'
}
elseif (Test-Path $dmCmd) {
    Write-Ok 'dm.cmd already exists.'
}

# ── 6. Add dist\ to user PATH (idempotent) ───────────────────────────────────

Write-Step 'Adding dist\ to user PATH...'

$userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
$pathEntries = $userPath.Split(';', [System.StringSplitOptions]::RemoveEmptyEntries)

$distDirNormalized = (Resolve-Path $distDir -ErrorAction SilentlyContinue).Path
if (-not $distDirNormalized) {
    $distDirNormalized = $distDir
}

if ($pathEntries -notcontains $distDirNormalized) {
    $newPath = ($pathEntries + , $distDirNormalized) -join ';'
    [Environment]::SetEnvironmentVariable('Path', $newPath, 'User')
    Write-Ok "Added '$distDirNormalized' to user PATH."
    Write-Warn 'Open a NEW console for the PATH change to take effect.'
}
else {
    Write-Ok "'$distDirNormalized' is already on user PATH."
}

# ── 7. Register MCP server with Claude Code ──────────────────────────────────

Write-Step 'Checking for Claude Code CLI...'

$claudeCmd = Get-Command claude -ErrorAction SilentlyContinue
$mcpServerExe = Join-Path $distDir 'mcp\DevMind.McpServer.exe'
$mcpRegistered = $false

if ($claudeCmd) {
    Write-Host "   Registering MCP server with Claude Code..." -ForegroundColor Cyan
    claude mcp add --scope user devmind -- $mcpServerExe
    if ($LASTEXITCODE -eq 0) {
        $mcpRegistered = $true
        Write-Ok 'MCP server registered with Claude Code.'
    }
    else {
        Write-Warn "claude mcp add failed (exit $LASTEXITCODE). Register manually later."
    }
}
else {
    Write-Warn ("claude CLI not found on PATH. To register the MCP server later, run:`n" +
        "   claude mcp add --scope user devmind -- `"$mcpServerExe`"")
}

# ── 8. Install netcoredbg (optional) ─────────────────────────────────────────

$netcoredbgTarget = "$env:LOCALAPPDATA\netcoredbg\netcoredbg\netcoredbg.exe"
$debuggerInstalled = $false

if ($SkipDebugger) {
    Write-Step 'Skipping netcoredbg install (-SkipDebugger set).'
}
elseif (Test-Path $netcoredbgTarget) {
    Write-Step 'Checking netcoredbg...'
    Write-Ok "netcoredbg already installed at $netcoredbgTarget"
    $debuggerInstalled = $true
}
else {
    Write-Step 'Installing netcoredbg debug adapter...'
    try {
        $apiUrl = 'https://api.github.com/repos/Samsung/netcoredbg/releases/latest'
        $release = Invoke-RestMethod -Uri $apiUrl -Headers @{ 'User-Agent' = 'DevMind-Installer' }

        $asset = $release.assets | Where-Object { $_.name -like '*win64.zip' } | Select-Object -First 1
        if (-not $asset) {
            throw 'No win64 zip asset found in the latest netcoredbg release.'
        }

        $zipUrl = $asset.browser_download_url
        $tempZip = [System.IO.Path]::GetTempFileName() + '.zip'

        Write-Host "   Downloading $($asset.name)..." -ForegroundColor Cyan
        Invoke-WebRequest -Uri $zipUrl -OutFile $tempZip -UseBasicParsing

        $extractDir = "$env:LOCALAPPDATA\netcoredbg"
        if (-not (Test-Path $extractDir)) {
            New-Item -ItemType Directory -Path $extractDir -Force | Out-Null
        }

        Write-Host '   Extracting...' -ForegroundColor Cyan
        Expand-Archive -Path $tempZip -DestinationPath $extractDir -Force

        Remove-Item $tempZip -Force

        if (Test-Path $netcoredbgTarget) {
            Write-Ok "netcoredbg installed at $netcoredbgTarget"
            $debuggerInstalled = $true
        }
        else {
            throw "netcoredbg.exe not found at expected path after extraction."
        }
    }
    catch {
        Write-Warn ("netcoredbg install failed: $($_.Exception.Message)`n" +
            "   To install manually:`n" +
            "   1. Download netcoredbg-win64.zip from https://github.com/Samsung/netcoredbg/releases`n" +
            "   2. Extract so the exe is at %LOCALAPPDATA%\netcoredbg\netcoredbg\netcoredbg.exe`n" +
            "   (The debugger is optional - DevMind works without it.)")
    }
}

# ── 9. Final summary ─────────────────────────────────────────────────────────

$version = 'unknown'
try {
    $commitCount = git -C $InstallDir rev-list --count HEAD 2>$null
    if ($commitCount -and [int]::TryParse($commitCount, [ref]$null)) {
        $version = "1.0.$commitCount"
    }
}
catch { }

Write-Host ""
Write-Host "========================================" -ForegroundColor Green
Write-Host "  DevMind installation complete!" -ForegroundColor Green
Write-Host "========================================" -ForegroundColor Green
Write-Host ""
Write-Host "  Version      : $version" -ForegroundColor Green
Write-Host "  Install dir  : $InstallDir" -ForegroundColor Green
Write-Host "  Dist path    : $distDirNormalized" -ForegroundColor Green
Write-Host "  MCP server   : $(if ($mcpRegistered) { 'Registered' } else { 'Not registered (claude CLI not found)' })" -ForegroundColor Green
Write-Host "  netcoredbg   : $(if ($debuggerInstalled) { 'Installed' } elseif ($SkipDebugger) { 'Skipped' } else { 'Not installed (manual setup needed)' })" -ForegroundColor Green
Write-Host ""
Write-Host "  Open a NEW console and run:" -ForegroundColor Green
Write-Host "    devmind            (or 'dm' for short)" -ForegroundColor Green
Write-Host ""
