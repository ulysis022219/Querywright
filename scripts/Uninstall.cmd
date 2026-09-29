@echo off
rem Double-click to remove Querywright from SSMS 22. Settings, snippets and tab history are kept.
if not exist "%~dp0Install-Querywright.ps1" (
  echo Install-Querywright.ps1 is not next to this file. You ran it from inside the zip.
  echo Right-click the zip, choose Extract All, then run this file from the extracted folder.
  pause
  exit /b 1
)
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install-Querywright.ps1" -Uninstall
set result=%errorlevel%
if %result%==0 (echo Done. Querywright is removed; start SSMS 22.) else (echo Uninstall failed; see the message above.)
pause
exit /b %result%
