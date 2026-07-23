# CE Tools

Civil engineering productivity tools for Autodesk Civil 3D.

## Current release

**v0.1.0-alpha** introduces the first working command:

### `CE_BMVERT` — Bellmouth Densifier

The command processes one or many 2D lightweight polylines and inserts vertices at equal chainages while preserving true line and arc geometry.

Two modes are included:

- **Maximum** — enter a maximum segment length. Each selected polyline is divided into the smallest whole number of equal chainage intervals that do not exceed that value.
- **Number** — enter the exact number of equal chainage intervals required on every selected polyline.

Existing geometry vertices are retained. New vertices are inserted into the existing `LWPOLYLINE`; the object is not exploded or replaced.

## Supported host versions in this alpha

- Civil 3D 2023 (`R24.2`, .NET Framework 4.8)
- Civil 3D 2024 (`R24.3`, .NET Framework 4.8)

The current command uses the AutoCAD managed geometry API, while the application bundle is restricted to Civil 3D because CE Tools is being developed as a Civil 3D suite.

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
3. Visual Studio 2022 with the **.NET desktop development** workload, or a compatible .NET SDK/MSBuild setup.
4. PowerShell.

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
.\scripts\Build-CE-Tools.ps1 -Version 2024 -AutoCADRoot "D:\Autodesk\AutoCAD 2024"
```

Compiled files are copied automatically into:

```text
bundle\CE Tools.bundle\Contents\Windows\<year>
```

## Install for testing

```powershell
.\scripts\Install-CE-Tools.ps1 -Scope User
```

Restart Civil 3D. A **CE Tools** ribbon tab should appear with a **Roads** panel and **Bellmouth Densifier** button.

The command can always be started from the command line:

```text
CE_BMVERT
```

To uninstall:

```powershell
.\scripts\Uninstall-CE-Tools.ps1 -Scope User
```

## Command workflow

1. Type `CE_BMVERT` or click the ribbon button.
2. Select all required bellmouth or kerb-return `LWPOLYLINE` objects.
3. Choose **Maximum** or **Number**.
4. Enter the spacing or segment count.
5. Review the command-line summary.

Distances are interpreted in the current drawing units.

## Alpha limitations

- Supports AutoCAD `Polyline` / DXF `LWPOLYLINE` objects only.
- Does not yet process Feature Lines, legacy 2D polylines, 3D polylines, splines, alignments or survey figures.
- Existing vertices remain in place. Inserted stations are equally spaced by chainage, but original geometry vertices can create shorter vertex-to-vertex portions between those stations.
- The host-independent mathematics is covered by automated tests. The Autodesk-dependent assembly must still be compiled and validated inside Civil 3D before production use.
- Test on a copy of a drawing during the alpha stage.

See [`docs/CE_BMVERT_TEST_PLAN.md`](docs/CE_BMVERT_TEST_PLAN.md) before using the command on a live project.
