@echo off
rem ── DevMind launcher ───────────────────────────────────────────────────────
rem Runs the self-contained DevMind.TUI.exe that sits next to this script.
rem   devmind                -> opens DevMind on the CURRENT directory
rem   devmind --dir C:\proj  -> passes your own args through verbatim
rem Put this folder on PATH (or copy devmind.cmd + DevMind.TUI.exe to a PATH dir)
rem and run "devmind" from anywhere.
setlocal
set "DM=%~dp0DevMind.TUI.exe"
if not exist "%DM%" (
  echo [devmind] DevMind.TUI.exe not found next to devmind.cmd ^(%~dp0^)
  exit /b 1
)
if "%~1"=="" (
  "%DM%" --dir "%CD%"
) else (
  "%DM%" %*
)
endlocal
