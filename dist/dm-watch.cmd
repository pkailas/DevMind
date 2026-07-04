@echo off
rem ── dm-watch launcher ───────────────────────────────────────────────────────
rem Live terminal view of DevMind delegated-task transcripts. Runs the
rem dm-watch.ps1 that sits next to this script. Put this folder on PATH
rem (it already is, for devmind/dm) and run "dm-watch" from anywhere.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0dm-watch.ps1"
