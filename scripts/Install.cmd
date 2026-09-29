@echo off
rem Double-click to install Querywright into SSMS 22. Uninstall.cmd removes it.
if not exist "%~dp0Install-Querywright.ps1" (
  echo Install-Querywright.ps1 is not next to this file. You ran it from inside the zip.
  echo Right-click the zip, choose Extract All, then run this file from the extracted folder.
  pause
  exit /b 1
)
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install-Querywright.ps1" %*
set result=%errorlevel%
if %result%==0 (echo Done. Start SSMS 22.) else (echo Installation failed; see the message above.)
pause
exit /b %result%
