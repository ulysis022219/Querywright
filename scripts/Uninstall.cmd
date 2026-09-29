@echo off
rem Double-click to remove Querywright from SSMS 22. Settings, snippets and tab history are kept.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install-Querywright.ps1" -Uninstall
set result=%errorlevel%
if %result%==0 (echo Done. Querywright is removed; start SSMS 22.) else (echo Uninstall failed; see the message above.)
pause
exit /b %result%
