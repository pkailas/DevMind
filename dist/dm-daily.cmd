@echo off
rem -- dm-daily launcher ------------------------------------------------------
rem Daily roll-up of DevMind delegated-job usage into a CSV ledger. Runs the
rem dm-daily.ps1 that sits next to this script. Put this folder on PATH
rem (it already is, for devmind/dm) and run "dm-daily" from anywhere.
rem
rem Examples:
rem   dm-daily                     nightly mode (upsert past days, print today)
rem   dm-daily -Date 2026-10-01    one specific day
rem   dm-daily -NoWrite            print only, touch nothing
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0dm-daily.ps1" %*
