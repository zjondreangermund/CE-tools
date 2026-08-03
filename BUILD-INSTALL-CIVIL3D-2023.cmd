@echo off
setlocal
cd /d "%~dp0"
echo Staging CE Tools outside OneDrive for a stable Civil 3D 2023 build...
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%CD%\scripts\Stage-Build-Install-Civil3D2023.ps1" -SourceRoot "%CD%"
if errorlevel 1 (
  echo.
  echo BUILD OR INSTALL FAILED. Review the error above.
  pause
  exit /b 1
)
echo.
echo CE Tools build and installation completed successfully.
pause
