# CE Tools

Civil engineering productivity tools for Autodesk Civil 3D.

## Current alpha commands

### `CE_BMVERT` — Bellmouth Densifier

Processes one or many 2D lightweight polylines and inserts vertices at equal chainages while preserving true line and arc geometry.

- **Maximum** — enter a maximum segment length. Each selected polyline is divided into the smallest whole number of equal chainage intervals that do not exceed that value.
- **Number** — enter the exact number of equal chainage intervals required on every selected polyline.

Existing geometry vertices are retained. New vertices are inserted into the existing `LWPOLYLINE`; the object is not exploded or replaced.

### `CE_TLENGTH` — Total Length

Select any supported AutoCAD curve objects. CE Tools reports:

- total selected length;
- number of objects counted and skipped;
- a length subtotal for every selected layer.

Results are shown in current drawing units.

### `CE_TAREA` — Total Area

Select closed boundaries, hatches and regions. CE Tools reports:

- total selected area;
- number of objects counted and skipped;
- an area subtotal for every selected layer.

Open, non-planar or invalid boundaries are skipped without stopping the complete selection. Results are shown in square drawing units.

## Supported host versions in this alpha

- Civil 3D 2023 (`R24.2`, .NET Framework 4.8)
- Civil 3D 2024 (`R24.3`, .NET Framework 4.8)

The current commands use the AutoCAD managed geometry API, while the application bundle is restricted to Civil 3D because CE Tools is being developed as a Civil 3D suite.

## Repository structure

```text
src/CE.Tools.Core         Host-independent geometry and planning logic
src/CE.Tools.Civil3D      Civil 3D command and ribbon integration
tests/CE.Tools.Core.Tests Dependency-free console tests
bundle/CE Tools.bundle    Autodesk application bundle
scripts                   Build and installation helpers
docs                      Civil 3D test plans
```

## Build prerequisites

1. Windows 10 or 11.
2. Civil 3D 2023 and/or Civil 3D 2024 installed.
3. Microsoft .NET 8 SDK for the build tools and core tests.
4. PowerShell.

The prerequisite helper can install the .NET SDK with Windows Package Manager:

```powershell
Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass -Force
.\scripts\Install-Prerequisites.ps1
```

## Build

Open PowerShell in the repository root.

```powershell
# Run the host-independent geometry tests
dotnet run --project .\tests\CE.Tools.Core.Tests\CE.Tools.Core.Tests.csproj -c Release

# Build for Civil 3D 2024
.\scripts\Build-CE-Tools.ps1 -Version 2024 -Configuration Release

# Build for Civil 3D 2023
.\scripts\Build-CE-Tools.ps1 -Version 2023 -Configuration Release

# Build both installed versions
.\scripts\Build-CE-Tools.ps1 -Version All -Configuration Release
```

The default Autodesk installation path is:

```text
C:\Program Files\Autodesk\AutoCAD <year>
```

Override it where necessary:

```powershell
.\scripts\Build-CE-Tools.ps1 -Version 2023 -Configuration Release -AutoCADRoot "C:\Program Files\Autodesk\AutoCAD 2023"
```

Compiled files are copied automatically into:

```text
bundle\CE Tools.bundle\Contents\Windows\<year>
```

## Install or update

Close Civil 3D before installing an updated build.

```powershell
.\scripts\Install-CE-Tools.ps1 -Scope User
```

Restart Civil 3D. The **CE Tools** ribbon contains:

- **Roads** — Bellmouth Densifier
- **Quantities** — Total Length and Total Area

Commands can also be started directly:

```text
CE_BMVERT
CE_TLENGTH
CE_TAREA
```

To uninstall:

```powershell
.\scripts\Uninstall-CE-Tools.ps1 -Scope User
```

## Alpha limitations

- `CE_BMVERT` supports AutoCAD `Polyline` / DXF `LWPOLYLINE` objects only.
- Feature Lines, legacy 2D polylines, 3D polylines, alignments and survey figures are not yet supported by `CE_BMVERT`.
- Existing geometry vertices remain in place. Inserted stations are equally spaced by chainage, but original geometry vertices can create shorter vertex-to-vertex portions between those stations.
- `CE_TAREA` requires valid closed planar boundaries, evaluated hatches or regions.
- Autodesk-dependent assemblies must be compiled and validated inside Civil 3D before production use.
- Test new alpha builds on a copy of a drawing.

See [`docs/CE_BMVERT_TEST_PLAN.md`](docs/CE_BMVERT_TEST_PLAN.md) before using the bellmouth command on a live project.
