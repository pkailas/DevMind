@echo off
rem ── DevMind launcher (short alias: dm) ─────────────────────────────────────
rem Runs the self-contained DevMind.TUI.exe that sits next to this script.
rem   dm                -> opens DevMind on the CURRENT directory
rem   dm --dir C:\proj  -> passes your own args through verbatim
rem This is a short alias for devmind.cmd; both launch the same DevMind.TUI.exe.
setlocal
set "DM=%~dp0DevMind.TUI.exe"
if not exist "%DM%" (
  echo [dm] DevMind.TUI.exe not found next to dm.cmd ^(%~dp0^)
  exit /b 1
)
if "%~1"=="" (
  "%DM%" --dir "%CD%"
) else (
  "%DM%" %*
)
endlocal
