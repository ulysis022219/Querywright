<# :
@echo off
rem Double-click to install Querywright into SSMS 22. Uninstall.cmd removes it.
rem The release build appends scripts/Install-Development.ps1 below, so this one file is the whole installer.
if not exist "%~dp0Querywright.Ssms.vsix" (
  echo Querywright.Ssms.vsix is not next to this file. You ran it from inside the zip.
  echo Right-click the zip, choose Extract All, then run this file from the extracted folder.
  pause
  exit /b 1
)
set "script=%TEMP%\Install-Querywright.ps1"
copy /y "%~f0" "%script%" >nul
powershell -NoProfile -ExecutionPolicy Bypass -File "%script%" -Package "%~dp0Querywright.Ssms.vsix" %*
set result=%errorlevel%
del "%script%" 2>nul
if "%~1"=="-Uninstall" (set action=Uninstall) else (set action=Installation)
if %result%==0 (echo Done. Start SSMS 22.) else (echo %action% failed; see the message above.)
pause
exit /b %result%
#>
