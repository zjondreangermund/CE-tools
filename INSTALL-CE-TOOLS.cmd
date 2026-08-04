@echo off
setlocal
cd /d "%~dp0"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\Install-CE-Tools-Release.ps1"
if errorlevel 1 (
  echo.
  echo CE TOOLS INSTALLATION FAILED. Review the error above.
  pause
  exit /b 1
)
echo.
echo CE Tools installation completed successfully.
pause
