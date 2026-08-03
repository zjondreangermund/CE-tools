@echo off
setlocal
cd /d "%~dp0"
echo Preparing CE Tools sources for Civil 3D 2023...
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%CD%\scripts\Repair-Civil3D2023-Compatibility.ps1" -RepoRoot "%CD%"
if errorlevel 1 (
  echo.
  echo SOURCE REPAIR FAILED. Review the error above.
  pause
  exit /b 1
)
echo.
echo Building and installing CE Tools for Civil 3D 2023...
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%CD%\scripts\Build-Install-Civil3D2023.ps1" -Clean
if errorlevel 1 (
  echo.
  echo BUILD OR INSTALL FAILED. Review the error above.
  pause
  exit /b 1
)
echo.
echo CE Tools build and installation completed successfully.
pause
