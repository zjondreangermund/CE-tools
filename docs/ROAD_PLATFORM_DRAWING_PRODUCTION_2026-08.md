# CE Tools — Road, Platform and Drawing Production Expansion

Date: 2026-08-10
Target host: Autodesk Civil 3D 2023 / .NET Framework 4.8

This ledger records the first integrated implementation of the requested road-layout, platform-production and multiple-boundary drawing workflows. It is intentionally separate from the existing Civil 3D road alignment/profile/corridor production layer.

## 1. Road layout production

Entry points:

- `CE_PRODUCTIONEXPANSION`
- `CE_ROADLAYOUTTOOLS`

Implemented commands:

| Requirement | Command | Validation focus |
|---|---|---|
| Road-reserve centre polylines from cadastral layout | `CE_ROADRESERVECENTERLINES` | Closed cadastral polylines, mixed reserve widths, parallel/opposing boundary matching, no source deletion |
| Road edges at specified offset | `CE_ROADEDGES` | All/Selected, both sides, linked parent handle |
| Sidewalk/shoulder edges | `CE_ROADSHOULDERS` | All/Selected road edges, specified offset |
| General road offset | `CE_ROADOFFSET` | Centreline/edge/shoulder source, All/Selected, positive/negative/both |
| Multiple T and cross junctions | `CE_ROADJUNCTIONBULK` | Automatic intersection classification, All/Selected, bulk return arcs |
| Trim lines through junction middles | `CE_ROADJUNCTIONTRIM` | All/Selected generated road geometry, source cadastral geometry preserved |
| Road names | `CE_ROADNAMES` | Sequential names and linked repositioning |
| Lane/road-width dimensions | `CE_ROADDIMENSIONS` | Centre-to-edge and edge-to-edge dimensions |
| Junction-only vertex setting-out | `CE_ROADJUNCTIONSETTINGOUT` | T/cross returns only, grouped sequence, COGO style sync |
| Linked maintenance | `CE_ROADLAYOUTREFRESH` | Road labels/dynamic maintained output |

Recommended Civil 3D acceptance drawing:

1. Four cadastral blocks surrounding a cross intersection.
2. At least one road reserve with a different width from the others.
3. A T-junction elsewhere in the same drawing.
4. Run reserve centrelines, road edges, shoulders and general offsets.
5. Run bulk junction creation with **All**.
6. Repeat with **Selected** and confirm unselected roads are untouched.
7. Trim junction middles.
8. Create names, dimensions and junction setting-out.
9. Move/edit one source road and run `CE_ROADLAYOUTREFRESH`.
10. Continue to `CE_ROADPRODUCTION` for alignment/profile/corridor work.

The first implementation creates preliminary road layout geometry. Final junction geometry should be visually checked against the project road standard before corridor production.

## 2. Platform production

Entry point: `CE_PLATFORMTOOLS`

| Requirement | Command | Validation focus |
|---|---|---|
| Multiple polylines to feature lines + surface popup | `CE_FLCREATE` | Existing CE multi-source feature-line creation is reused |
| Constant/fixed/flatten platform levels | `CE_PLATFORMSLOPE` | Highest-to-lowest plane, fixed fall, flatten to highest |
| Multiple stepped offsets | `CE_PLATFORMSTEPOFFSETS` | Existing `CE_FLREL` relationship schema, outward closed-platform offsets |
| Drape stepped offsets to selected surface | `CE_PLATFORMDRAPE` | Popup surface, source platform driven by draped child |
| Dynamic surface-driven platform levels | automatic + `CE_PLATFORMREFRESH` | Change survey/target surface and allow idle refresh; verify source and linked steps change |
| Platform site / separate surface / infill | `CE_PLATFORMSURFACE` | All/Selected closed feature lines, site assignment, breaklines, grading infill where Civil 3D host API permits |
| Vertex or grid setting-out | `CE_PLATFORMSETTINGOUT` | Existing `CE_VERTEXSETTINGOUT` and `CE_GRIDSETTINGOUT` engines |
| Platform names and final elevation | `CE_PLATFORMNAMES` | PLATFORM-n, centre placement, final elevation/range |
| Linked platform table | `CE_PLATFORMTABLE` | Area, perimeter, min/max/final levels; annotative refresh |
| Linked cut/fill table | `CE_PLATFORMCUTFILL` | NG/design surface selection, cut/fill grid integration, refresh after surface edits |
| Platform drawings/layouts/sections | `CE_PLATFORMDRAWINGS` + `CE_XSTOOLS` | CE-PLATFORM layouts and generated section source lines |
| Platform BOQ/report | `CE_BOQPLATFORM`, `CE_REPORTPLATFORM` | Existing CE production outputs reused |

Dynamic validation:

1. Create at least three closed platform feature lines at different elevations.
2. Create multiple stepped offsets.
3. Drape selected outer steps to a surveyed surface.
4. Create platform names and register.
5. Select separate NG and design surfaces for cut/fill.
6. Raise/lower the controlling surveyed surface.
7. Wait until Civil 3D is idle or run `CE_PLATFORMREFRESH`.
8. Confirm draped steps, source platform levels, linked steps, platform labels and linked tables update.
9. Run `CE_PLATFORMDRAWINGS`, then `CE_XSCREATE`/`CE_XSTOOLS` on generated section lines.

`CE_PLATFORMSURFACE` uses compatibility-safe runtime discovery for Site/TIN Surface/Grading APIs because Autodesk exposes different overloads between Civil 3D releases. If the installed 2023 host does not expose an expected operation, the command reports a host-API warning and retains the source feature lines instead of deleting them.

## 3. Multiple-boundary drawing tools

Entry point: `CE_BOUNDARYEDITTOOLS`

- `CE_TRIMOUTSIDEMULTI`
- `CE_TRIMINSIDEMULTI`
- `CE_TRIMDELETEOUTSIDEMULTI`
- `CE_TRIMDELETEINSIDEMULTI`
- `CE_EXTENDOUTSIDEMULTI`
- `CE_EXTENDINSIDEMULTI`

Validation drawing:

1. Create at least three closed lightweight-polyline boundaries.
2. Draw lines and open polylines fully inside, fully outside and crossing one or more boundaries.
3. Validate each command with **Selected** and **All** scope.
4. Confirm boundary objects are never erased or edited.
5. Plain Trim commands must trim crossing portions but leave wholly non-crossing objects unchanged.
6. Trim+Delete commands must also remove wholly unwanted objects.
7. Extend commands currently extend supported `Line` and open `Polyline` endpoints to the nearest boundary intersection in the requested inside/outside direction; unsupported curve types are skipped and reported.

## 4. Civil 3D 2023 staging integration

`Stage-Build-Install-Civil3D2023.ps1` now runs `Inject-ProductionExpansion-Civil3D2023.ps1` after the existing Civil 3D 2023 compatibility repairs and before sanitizing/MSBuild.

The integration step:

- starts the platform dynamic refresh manager when CE Tools loads;
- adds **Preliminary Road Layout Production** to the Road Production flyout;
- adds **Platform Production** to Site Design;
- adds **Multiple Boundary Trim / Extend** to Drawing Tools;
- verifies all required new command declarations exist before compilation.

## 5. Build gate

Close Civil 3D 2023 and run the existing one-click build/install process. The build must complete with no C# compile errors before runtime acceptance testing.

Because the production source uses Autodesk/Civil 3D managed APIs, repository-side source review is not a substitute for the installed Civil 3D 2023 compiler/runtime validation. Record any compile/runtime exception with the full source filename, line number and error text before changing logic.
