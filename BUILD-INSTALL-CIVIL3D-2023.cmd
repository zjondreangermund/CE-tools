@echo off
setlocal
cd /d "%~dp0"
echo Building and installing CE Tools for Civil 3D 2023...
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\Build-Install-Civil3D2023.ps1" -Clean
if errorlevel 1 (
  echo.
  echo BUILD OR INSTALL FAILED. Review the error above.
  pause
  exit /b 1
)
echo.
echo CE Tools build and installation completed successfully.
pause
