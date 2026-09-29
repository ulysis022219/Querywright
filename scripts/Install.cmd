@echo off
rem Double-click to install Querywright into SSMS 22. "Install.cmd -Uninstall" removes it.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install-Querywright.ps1" %*
set result=%errorlevel%
if %result%==0 (echo Done. Start SSMS 22.) else (echo Installation failed; see the message above.)
pause
exit /b %result%
