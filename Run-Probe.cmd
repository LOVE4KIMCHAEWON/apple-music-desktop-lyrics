@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\Run-Probe.ps1" %*
set "probeExit=%ERRORLEVEL%"
if "%~1"=="" pause
exit /b %probeExit%
