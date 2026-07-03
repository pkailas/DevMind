<#
.SYNOPSIS
    Deploys DevMind by publishing a self-contained, single-file build straight into
    the repo's dist\ folder -- the install that the `devmind` / `dm` launchers on PATH run.

.DESCRIPTION
    This is the CURRENT deploy path. It replaces the previous approach (a
    framework-dependent build copied into %LOCALAPPDATA%\DevMind via dist\deploy-devmind.ps1
    with -AddToPath), which left a stale, shadowed second install on PATH. Everything
    now lives in dist\, which is already on PATH via devmind.cmd / dm.cmd.

    Version is git-driven (Directory.Build.props): COMMIT FIRST, then run this so the
    published exe is stamped with the new commit count (1.0.<git-commit-count>).

    The dist\ launcher scripts (devmind.cmd, dm.cmd, deploy-devmind.ps1) are tracked and
    are preserved -- `dotnet publish` only refreshes the build output beside them.

.EXAMPLE
    .\run-deploy.ps1                 # Release / win-x64 into .\dist
.EXAMPLE
    .\run-deploy.ps1 -Runtime linux-x64
#>
[CmdletBinding()]
param(
    [string] $Configuration = 'Release',
    [string] $Runtime       = 'win-x64'
)

$ErrorActionPreference = 'Stop'

$repo = $PSScriptRoot
$proj = Join-Path $repo 'DevMind.TUI\DevMind.TUI.csproj'
$dist = Join-Path $repo 'dist'

Write-Host "Publishing self-contained DevMind ($Configuration / $Runtime) into $dist ..." -ForegroundColor Cyan

dotnet publish $proj `
    -c $Configuration `
    -r $Runtime `
    --self-contained `
    -p:PublishSingleFile=true `
    -p:PublishTrimmed=false `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -o $dist

if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit $LASTEXITCODE)" }

# ── MCP server ────────────────────────────────────────────────────────────────
# Published into dist\mcp (its own folder — the TUI's single-file publish would
# otherwise clobber shared DLL names). This is the exe MCP clients (Claude Code)
# are registered against:
#   claude mcp add --scope user devmind -- <repo>\dist\mcp\DevMind.McpServer.exe

$mcpProj = Join-Path $repo 'DevMind.McpServer\DevMind.McpServer.csproj'
$mcpDist = Join-Path $dist 'mcp'

Write-Host "`nPublishing DevMind.McpServer ($Configuration / $Runtime) into $mcpDist ..." -ForegroundColor Cyan

dotnet publish $mcpProj `
    -c $Configuration `
    -r $Runtime `
    --self-contained `
    -p:PublishSingleFile=true `
    -p:PublishTrimmed=false `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -o $mcpDist

if ($LASTEXITCODE -ne 0) { throw "dotnet publish (McpServer) failed (exit $LASTEXITCODE)" }

Write-Host "`nDeployed to $dist -- 'devmind' / 'dm' on PATH now run this build." -ForegroundColor Green
Write-Host "MCP server: $mcpDist\DevMind.McpServer.exe" -ForegroundColor Green
