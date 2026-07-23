# CE Tools

Civil engineering productivity tools for Autodesk Civil 3D.

## Current alpha commands

### `CE_BMVERT` — Bellmouth Densifier

Processes one or many 2D lightweight polylines and inserts vertices at equal chainages while preserving true line and arc geometry.

- **Maximum** — enter a maximum segment length. Each selected polyline is divided into the smallest whole number of equal chainage intervals that do not exceed that value.
- **Number** — enter the exact number of equal chainage intervals required on every selected polyline.

Existing geometry vertices are retained. New vertices are inserted into the existing `LWPOLYLINE`; the object is not exploded or replaced.

### `CE_FLTOOLS` — Feature Line Tools

The first feature-line alpha provides:

- **Report** — report 2D/3D length, minimum/maximum elevation, minimum/maximum grade, PI count, elevation-point count and total point count.
- **RaiseLower** — raise or lower all points on multiple selected feature lines by one entered value.
- **SetElevation** — set every point on multiple selected feature lines to one absolute elevation.

Direct commands are also available:

```text
CE_FLREPORT
CE_FLRAISE
CE_FLSETELEV
```

Relative-to-surface points retain their relationship when using **RaiseLower**. **SetElevation** converts relative points to the entered absolute elevation. Referenced feature lines and feature lines on locked layers are skipped.

### `CE_TLENGTH` — Total Length

Select supported AutoCAD curve objects. CE Tools reports the total selected length, counted/skipped objects and a length subtotal for every selected layer.

### `CE_TAREA` — Total Area

Select closed boundaries, hatches and regions. CE Tools reports the total selected area, counted/skipped objects and an area subtotal for every selected layer.

Open, non-planar or invalid boundaries are skipped without stopping the complete selection. Results are shown in square drawing units.

### `CE_COORDINATE` — Coordinate Tools

One command provides four survey and setting-out workflows:

- **Pick** — pick one point and place an XYZ MLeader.
- **Cogo** — batch-label selected Civil 3D COGO points with point identification, description and XYZ data.
- **Cross** — place a coordinate cross with an XYZ MLeader.
- **Table** — create a coordinate setting-out table from selected AutoCAD DBPoints and/or Civil 3D COGO points.

A revised coordinate workflow based on the user's reference LSP is planned for a later update.

### `CE_SEWSEQ` — Sewer Network Sequence

Select only the start manhole/structure and the end manhole/structure. CE Tools:

1. traces the connected shortest path automatically;
2. assigns the pipe network the next available name, such as `Branch-1`;
3. renames structures `MH1`, `MH2`, ... in start-to-end order;
4. renames pipes `P1`, `P2`, ... in the same direction;
5. adds the branch name to the selected network parts' descriptions.

No intermediate pipe or manhole selection is required.

### `CE_COLOR250` / `COLOR250` — Color 250

Changes preselected or selected drawing objects to AutoCAD colour index 250. Objects on locked layers are skipped and the command reports changed/skipped totals.

## Supported host versions in this alpha

- Civil 3D 2023 (`R24.2`, .NET Framework 4.8)
- Civil 3D 2024 (`R24.3`, .NET Framework 4.8)

The project references the AutoCAD managed API, AEC managed API and `AeccDbMgd.dll` for Civil 3D objects.

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
- **Feature Lines** — Feature Line Tools
- **Quantities** — Total Length and Total Area
- **Survey** — Coordinate Tools
- **Utilities** — Sewer Sequence
- **Drawing** — Color 250

Commands can also be started directly:

```text
CE_BMVERT
CE_FLTOOLS
CE_FLREPORT
CE_FLRAISE
CE_FLSETELEV
CE_TLENGTH
CE_TAREA
CE_COORDINATE
CE_SEWSEQ
CE_COLOR250
COLOR250
```

To uninstall:

```powershell
.\scripts\Uninstall-CE-Tools.ps1 -Scope User
```

## Alpha limitations

- `CE_BMVERT` supports AutoCAD `Polyline` / DXF `LWPOLYLINE` objects only.
- Feature Lines, legacy 2D polylines, 3D polylines, alignments and survey figures are not yet supported by `CE_BMVERT`.
- Existing geometry vertices remain in place. Inserted stations are equally spaced by chainage, but original geometry vertices can create shorter vertex-to-vertex portions between those stations.
- `CE_FLTOOLS` currently edits ordinary grading feature lines only. Create, surface elevation, insert/delete point and weeding tools are planned next.
- `CE_TAREA` requires valid closed planar boundaries, evaluated hatches or regions.
- `CE_COORDINATE` uses drawing defaults in this first release; intelligent overlap cleanup and company label-style mapping are later stages.
- `CE_SEWSEQ` currently supports Civil 3D gravity pipe networks, not pressure networks. It uses the shortest connected path when loops provide multiple possible routes.
- Autodesk-dependent assemblies must be compiled and validated inside Civil 3D before production use.
- Test new alpha builds on a copy of a drawing.

See the command-specific test plans in [`docs`](docs) before using a new alpha build on a live project.
