@echo off
setlocal
set "ROOT=%~dp0"

echo ============================================================
echo CE Tools Civil 3D 2023 - August 19/20 staged build/install
echo ============================================================
echo.
echo The complete August 18 pipeline will run first.
echo August 19/20 repairs are applied only to the temporary staged copy.
echo Existing source files are preservation-checked before/after.
echo.

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%ROOT%scripts\Stage-Build-Install-Civil3D2023-August20.ps1" -SourceRoot "%ROOT%"
set "EXITCODE=%ERRORLEVEL%"

if not "%EXITCODE%"=="0" (
    echo.
    echo August 19/20 build/install FAILED with exit code %EXITCODE%.
    pause
    exit /b %EXITCODE%
)

echo.
echo August 19/20 build/install completed successfully.
pause
exit /b 0