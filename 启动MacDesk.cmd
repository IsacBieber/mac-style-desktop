@echo off
rem Run the Medium-integrity copy that build.ps1 installs; never the D:\ build output (Low integrity).
set "EXE=%USERPROFILE%\Documents\MacDesk\MacDesk.exe"
if not exist "%EXE%" (
  echo MacDesk.exe not found. Run build.ps1 first.
  pause
  exit /b 1
)
start "" "%EXE%"
