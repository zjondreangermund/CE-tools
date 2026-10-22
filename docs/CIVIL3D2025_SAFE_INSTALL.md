# Civil 3D 2025 CE Tools installation

The 2025 version uses the **same CE Tools engineering source** as the other adapters. It is compiled against **Civil 3D 2025 managed assemblies and .NET 8**, rather than attempting to load a 2023/2024 .NET Framework DLL.

## Prerequisites
- Autodesk Civil 3D **2025** installed on Windows (not AutoCAD alone)
- .NET **8 SDK or later** for compiling the project
- Close Civil 3D before installing
- Keep the repository checked out at the desired commit on `main`

## Build and preview (does not install)

Open PowerShell from the repository root:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Build-Install-Civil3D2025-Safe.ps1
```

If compiling succeeds, inspect `artifacts\CE Tools 2025.bundle`. The separate .NET 8 DLL is in `Contents\Windows\2025`.

## Install for current user

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Build-Install-Civil3D2025-Safe.ps1 -Install -Scope User
```

To install for all Windows users, launch elevated PowerShell and use `-Scope AllUsers`.

## Safety
- Installation is separate: `%APPDATA%\Autodesk\ApplicationPlugins\CE Tools 2025.bundle`.
- Existing `CE Tools.bundle`, `CE Tools 2024.bundle`, and other version folders are not overwritten.
- Installation is blocked if `CE Tools.bundle` already contains a 2025 adapter to avoid loading duplicate commands.
- Existing 2025 bundle backups are stored outside ApplicationPlugins.
- The installer does not rewrite the Civil 3D command source or engineering algorithms.
- The build uses the local Civil 3D 2025 managed assemblies and requires a Windows workstation with the host installed.
- After install, launch Civil 3D 2025 and test the CE Tools ribbon and important commands on a **copy** of a project DWG.

## Custom Autodesk installation folder
If Civil 3D 2025 resides outside `C:\Program Files\Autodesk\AutoCAD 2025`, pass `-AutoCADRoot`, `-Civil3DRoot`, and `-AecRoot` with the actual assembly locations.

Compilation and in-host smoke tests cannot be claimed complete until run on a computer with Civil 3D 2025 installed.
