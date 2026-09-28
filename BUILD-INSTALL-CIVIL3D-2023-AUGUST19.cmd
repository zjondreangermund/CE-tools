@echo off
setlocal
set "ROOT=%~dp0"

echo ============================================================
echo CE Tools Civil 3D 2023 - current source build/install
echo ============================================================
echo.
echo Building the current repository source and creating a release ZIP.
echo.

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%ROOT%scripts\Build-Install-Current-Civil3D2023.ps1"
set "EXITCODE=%ERRORLEVEL%"

if not "%EXITCODE%"=="0" (
    echo.
    echo Current source build/install FAILED with exit code %EXITCODE%.
    pause
    exit /b %EXITCODE%
)

echo.
echo Current source build/install completed successfully.
pause
exit /b 0
