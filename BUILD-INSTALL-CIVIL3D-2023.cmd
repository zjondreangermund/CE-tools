@echo off
setlocal
cd /d "%~dp0"

echo ============================================================
echo CE Tools - Civil 3D 2023 Build / Install
echo Source folder: %CD%
echo ============================================================
echo.

if not exist "%CD%\src\CE.Tools.Civil3D\August11NetworkBatchCommands.cs" (
  echo ERROR: CE Tools source files were not found in this folder.
  echo Download/extract the latest CE-tools main repository and run this file from its root folder.
  pause
  exit /b 1
)

findstr /C:"Select multiple now" "%CD%\src\CE.Tools.Civil3D\August11NetworkBatchCommands.cs" >nul
if errorlevel 1 (
  echo ERROR: THIS IS AN OLD CE TOOLS SOURCE COPY.
  echo The sewer/network true multi-selection source is missing.
  echo Download/extract the latest GitHub main before building.
  pause
  exit /b 1
)

findstr /C:"TrySelectOne" "%CD%\src\CE.Tools.Civil3D\August12SurfaceSelectionPopup.cs" >nul
if errorlevel 1 (
  echo ERROR: THIS IS AN OLD CE TOOLS SOURCE COPY.
  echo The latest surface popup selector source is missing.
  echo Download/extract the latest GitHub main before building.
  pause
  exit /b 1
)

findstr /C:"\"LXT\"" "%CD%\scripts\Repair-August12SurveyGridCoordinatesAndProductionEscape-Civil3D2023.ps1" >nul
if errorlevel 1 (
  echo ERROR: THIS IS AN OLD CE TOOLS SOURCE COPY.
  echo The four-side Survey Site Grid repair is missing.
  echo Download/extract the latest GitHub main before building.
  pause
  exit /b 1
)

findstr /C:"Repair-August12SurveyGridCoordinatesAndProductionEscape-Civil3D2023.ps1" "%CD%\scripts\Repair-August12SurveyGridAndDisplayNames-Civil3D2023.ps1" >nul
if errorlevel 1 (
  echo ERROR: THIS IS AN OLD CE TOOLS SOURCE COPY.
  echo The final Survey Site Grid coordinate repair is not chained into the build.
  echo Download/extract the latest GitHub main before building.
  pause
  exit /b 1
)

if exist "%CD%\.git" (
  for /f "delims=" %%i in ('git -C "%CD%" rev-parse HEAD 2^>nul') do set "CE_SOURCE_COMMIT=%%i"
  if defined CE_SOURCE_COMMIT echo Source commit: %CE_SOURCE_COMMIT%
) else (
  echo Source type: downloaded/extracted repository copy
  echo Latest-source markers: PASSED
)

echo.
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
