# CE Tools — Civil 3D 2024 installation

## Safety boundary

The production Civil 3D 2023 `CE Tools.bundle`, its DLL, its installer and its command implementations must not be modified or replaced when setting up Civil 3D 2024.

The project already targets `net48` for both 2023 and 2024 and resolves AutoCAD / Civil 3D managed references from the chosen installation. Autodesk-version-specific binaries are still required.

## Build a 2024 preview

From a PowerShell window in the CE-tools repository on a Windows computer with Civil 3D 2024 installed:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Build-Install-Civil3D2024-Safe.ps1
```

The output is `artifacts\CE Tools 2024.bundle` (2024-only manifest and DLLs). The default invocation **does not install anything**.

If Autodesk is installed in a nonstandard path, supply `-AutoCADRoot`. The script requires the actual 2024 `AcMgd.dll`, `AeccDbMgd.dll`, and `AecBaseMgd.dll`. Compilation errors must be fixed before continuing.

## Install only after compiling and testing a backup drawing

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Build-Install-Civil3D2024-Safe.ps1 -Install
```

The installer uses `%APPDATA%\Autodesk\ApplicationPlugins\CE Tools 2024.bundle` for user scope or `%PROGRAMDATA%\Autodesk\ApplicationPlugins\CE Tools 2024.bundle` for all-users scope.

The installer intentionally blocks installation if it finds an already installed 2024 DLL in the old shared CE Tools bundle or if a destination 2024 bundle already exists. Do not remove the 2023 bundle to work around the safety guard.

## Validation checklist

1. Verify the 2024 release build succeeds with zero errors against Autodesk 2024 DLLs.
2. Verify startup/ribbon, all command registrations and no duplicate CE Tools ribbon.
3. Test roads, corridors, feature lines, grading, surfaces, plan and profile views, setting out, sewer and stormwater networks, water/bulk-water, CAD supplementary, quantity reports and exports.
4. Stress-test `CE_ROADAUTOSHEETS` including running the command twice; prior work has seen fatal errors on sheet reruns.
5. Compare output data, defaults and calculations on the same test geometry against 2023.
6. Confirm 2023 installation and command behaviour remains unchanged.

Status: Repository packaging and guarded installation workflow prepared. **Not compiled or tested against a 2024 host in this session.** Do not label production-ready until tests pass.
