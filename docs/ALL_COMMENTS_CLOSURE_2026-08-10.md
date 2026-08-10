# CE Tools — Master Comment Closure Ledger

Date: 2026-08-10  
Target: Autodesk Civil 3D 2023 / .NET Framework 4.8  
Repository: `zjondreangermund/CE-tools`

## Status meaning

- **CODE COMPLETE** — source/staging implementation is present on `main` and is included in the final closure validator.
- **ALREADY IMPLEMENTED / REVALIDATED** — the capability existed in the current source before this closure pass and was checked against the comment.
- **HOST ACCEPTANCE REQUIRED** — the code-side work is complete but final Autodesk compile/runtime/geometry behavior must be tested in the user's Civil 3D 2023 installation.
- **EXTERNAL FILE REQUIRED** — the requested exact output depends on a source file that is not available in the repository/conversation file system.

All code-complete items below remain subject to the final Civil 3D 2023 build and drawing acceptance test. Repository-side source review cannot prove Autodesk runtime behavior.

---

## A. Workflow Centre, shortcuts and usage statistics

| Comment | Status | Implementation / command |
|---|---|---|
| Ctrl+F must open the CE Tools workflow window, not OSNAP | CODE COMPLETE | Existing shortcut retained plus `AugustGlobalShortcutManager` Windows-message interception in the staged 2023 build |
| Shortcut / access to Overall Most Used | CODE COMPLETE | Ctrl+Shift+M through the final global shortcut manager; existing Overall Most Used view retained |
| Overall Most Used across all saved drawings/projects | ALREADY IMPLEMENTED / REVALIDATED | Existing floating-tools/usage tracking infrastructure |
| Project/drawing statistics, totals, last drawings and clear | ALREADY IMPLEMENTED / REVALIDATED | Existing floating workflow/statistics implementation |

## B. Project setup, coordinates and PDF

| Comment | Status | Implementation / command |
|---|---|---|
| Selecting a town should link/assign coordinate-system context | ALREADY IMPLEMENTED / REVALIDATED | Existing `CE_SURVEYLOCATION` / project-coordinate workflow |
| Correct WGS84 latitude/longitude to Namibia survey XY | CODE COMPLETE | `CE_NAMIBIALO`; Schwarzeck/LO-aware runtime with GeoData fallback for non-Namibia drawings |
| Accept decimal or DMS latitude/longitude | CODE COMPLETE | `CE_NAMIBIALO` decimal/DMS parser and DMS formatter |
| Pick a point in any drawing and update XY/NE/Lat/Long review values | CODE COMPLETE | `CE_COORDPICKMAP` |
| Google Maps / Google Earth should use the converted WGS84 point | CODE COMPLETE | Namibia conversion output includes map-opening option |
| PDF to DWG must show a file-selection popup | ALREADY IMPLEMENTED / REVALIDATED | Existing `CE_PDFTODWG` picker/native PDF import handoff |

## C. Automatic linked refresh and tables

| Comment | Status | Implementation / command |
|---|---|---|
| No manual table sync after placing a linked table | CODE COMPLETE | `AugustAutomaticRefreshManager` queues the existing universal/linked refresh after every completed `CE_*` command |
| Tables/annotations update when linked design data changes | CODE COMPLETE | Existing universal dynamic refresh retained; final command-ended queue and platform refresh added |
| Table grid lines are missing | CODE COMPLETE | `CE_TABLEPRESENTATIONFIX` restores all grid lines using 2023-compatible fallback |
| Table text/spacing should be readable and centred | CODE COMPLETE | `CE_TABLEPRESENTATIONFIX` row/column minimums + centred text |
| Setting-out table should show DESIGN LEVEL and not the unwanted Z column | ALREADY IMPLEMENTED / REVALIDATED | Current vertex setting-out table uses `POINT NAME, X, Y, NG LEVEL, DESIGN LEVEL, DIFFERENCE, RADIUS, SEGMENT LENGTH` |
| Table should allow base/NG and comparison/design surfaces where applicable | ALREADY IMPLEMENTED / REVALIDATED | Existing linked feature-line/survey comparison tables plus platform cut/fill workflow |
| Click a table data cell/row and highlight/zoom its linked source | CODE COMPLETE | `CE_TABLECELLZOOM` uses actual Table hit-test row/column and linked source handles |
| Select a linked table and zoom one/all sources | CODE COMPLETE | `CE_TABLESOURCEZOOM` |

## D. COGO, MText, MLeader and annotation overlap

| Comment | Status | Implementation / command |
|---|---|---|
| COGO labels move too far after overlap resolution | CODE COMPLETE | Staged `Repair-CogoOverlap-Civil3D2023.ps1`: bounded close search, no farthest-candidate fallback |
| Do not move COGO labels that are not overlapping | CODE COMPLETE | Staged COGO resolver first checks current label box and returns current location when clear |
| Keep COGO point coordinates/reference positions fixed when labels move | CODE COMPLETE | COGO resolver moves label location only; final smart resolver excludes point-coordinate movement |
| Generic Resolve Overlap popup for COGO / MText / MLeader / etc. | CODE COMPLETE | `CE_OVERLAPSMART` |
| All/Selected overlap options | CODE COMPLETE | `CE_OVERLAPSMART`, restore, masks, draw-order and related final tools support All/Selected |
| Restore all/selected moved annotations | CODE COMPLETE | `CE_ANNOTATIONRESTORE`, backed by `CE_OVERLAP_ORIGINAL` Xrecords |
| MText/MLeader background-mask option | CODE COMPLETE | `CE_ANNOTATIONMASK` plus mask option in smart overlap / leader correction |
| MLeader text must sit above the leader, not below it | CODE COMPLETE | `CE_MLEADERTEXTABOVE`; only `TextLocation` moves, leader/reference vertices remain fixed |
| Bring design labels/COGO/elevations/slopes to front or send to back | CODE COMPLETE | `CE_ANNOTATIONDRAWORDER` |
| One combined final annotation review window | CODE COMPLETE | `CE_ANNOTATIONREVIEW` |

## E. Vertex setting-out and feature-line dynamics

| Comment | Status | Implementation / command |
|---|---|---|
| COGO style should be correct immediately after vertex setting-out, not only after manual refresh | CODE COMPLETE | Existing project COGO style sync plus automatic `CE_*` command-ended linked refresh |
| Resolve overlap should also work for polyline/feature-line vertex output | CODE COMPLETE | Core COGO resolver repaired; `CE_OVERLAPSMART` covers linked MText/MLeader output |
| Keep linked points/tables with a moved/edited feature line | ALREADY IMPLEMENTED / REVALIDATED | Existing vertex-setting-out source handles + refresh engine; final automatic queue retained |
| Refresh feature-line annotations/tables — All | CODE COMPLETE | `CE_FLANNOTREFRESH` |
| Refresh feature-line annotations/tables — Selected only | CODE COMPLETE | `CE_FLANNOTREFRESHSELECTED`; discovers tables linked to selected source feature lines and queues selected stepped-offset sets |
| Grid setting-out option using selected polyline/boundary | ALREADY IMPLEMENTED / REVALIDATED | Existing `CE_GRIDSETTINGOUT`; also exposed in `CE_ANNOTATIONREVIEW` and platform setting-out |
| Source/NG and comparison/target surface choices for feature-line tables | ALREADY IMPLEMENTED / REVALIDATED | Existing feature-line report and platform/surface comparison workflows |
| Surface picker must show available surfaces | ALREADY IMPLEMENTED / REVALIDATED | Existing Civil surface-choice popup infrastructure reused across final workflows |

## F. Surface correction

| Comment | Status | Implementation / command |
|---|---|---|
| Add triangles/fill only where internal surface holes exist | CODE COMPLETE | Staged surface repair gets `Internal holes only` mode; spike/low candidate repair is disabled in that mode |
| Keep spike/low repair separate from hole-only repair | CODE COMPLETE | Final staged repair mode has all / internal-holes-only / spikes-lows-only choices |

## G. Road preliminary layout / cadastral production

| Comment | Status | Implementation / command |
|---|---|---|
| Create centre polylines in all road reserves from cadastral layout, including different reserve widths | CODE COMPLETE | `CE_ROADRESERVECENTERLINES` |
| Create road edges at specified offsets, All/Selected | CODE COMPLETE | `CE_ROADEDGES` |
| Create sidewalk/shoulder edges at specified offsets, All/Selected | CODE COMPLETE | `CE_ROADSHOULDERS` |
| General offset from centreline/road edge/shoulder, All/Selected | CODE COMPLETE | `CE_ROADOFFSET` |
| Create multiple T and cross junctions | CODE COMPLETE | `CE_ROADJUNCTIONBULK` |
| Complete all four quadrants of a cross junction before continuing to next junction | CODE COMPLETE / HOST ACCEPTANCE REQUIRED | Bulk-junction loop creates one complete four-return group per intersection before advancing |
| Junction return output option: Arc or Polyline | CODE COMPLETE | Bulk-junction popup includes geometry choice; `CE_JUNCTIONRETURNTYPE` converts existing generated returns while preserving CE link records |
| Trim road objects through middle of junctions, All/Selected | CODE COMPLETE | `CE_ROADJUNCTIONTRIM` |
| Create offsets for multiple polylines and trim at junctions/crossings | CODE COMPLETE | Road layout offset + bulk junction trim workflows |
| Road names similar to branch names | CODE COMPLETE | `CE_ROADNAMES` |
| Dimensions for lane widths / road widths | CODE COMPLETE | `CE_ROADDIMENSIONS` |
| Junction-only vertex setting-out and numbering sequence | CODE COMPLETE | `CE_ROADJUNCTIONSETTINGOUT` |
| Refresh linked road layout | CODE COMPLETE | `CE_ROADLAYOUTREFRESH` |

## H. Road Civil 3D production, styles, band sets and assemblies

| Comment | Status | Implementation / command |
|---|---|---|
| Road alignment received a pipe/Devotech style; choose road styles instead | CODE COMPLETE | Staged road-style resolver ranks Road/centre/station styles and strongly rejects Pipe/Sewer/Water/Storm styles |
| Correct road alignment label sets/styles | ALREADY IMPLEMENTED / REVALIDATED | Existing Project Style Centre selections remain exact-first; road-only fallback is now safer |
| Automatically import missing profile/band-set styles | CODE COMPLETE | `ProfileStyleAutoImportRuntime` + staged profile hooks; imports bundled supplied CE style DWGs only when expected band library is missing |
| First/default road profile band set should be `Road-Single-Band Set 1-Full Grid` | CODE COMPLETE | `AugustRoadProfileDefaults` + staged road profile resolver |
| Apply band sets to multiple profile views | ALREADY IMPLEMENTED / REVALIDATED | Existing profile-view/band batch infrastructure; final safe batch remains available |
| Apply label sets to multiple alignments/profile views | ALREADY IMPLEMENTED / REVALIDATED | Existing project style / production / batch style infrastructure |
| Road assembly inserted but not visible | CODE COMPLETE | `CE_ASSEMBLYMARKERS`; new assembly creation gets a visible linked marker at its insertion point |

## I. Utility route planner — Roads / Sewer / Stormwater / Water / Bulk Water

| Comment | Status | Implementation / command |
|---|---|---|
| One route planner for Roads, SW, Sewer, Water and Bulk Water | CODE COMPLETE | `CE_ROUTEPLANNER` |
| Option 1 utilities must follow road-reserve route geometry | CODE COMPLETE | `CE_UTILITYFROMROADRESERVE`, sourced from `CE-ROAD-CENTERLINE` |
| Preliminary output should remain CAD geometry before actual network/detail design | CODE COMPLETE | Route planner produces preliminary polylines; downstream network-production hub remains separate |
| Route planner should continue into networks, pipes/structures, profiles, tables, BOQs | CODE COMPLETE | Route planner exposes network and discipline production handoffs |
| Option 2 Sewer — Midblock | CODE COMPLETE | `CE_MIDBLOCKSEWERLAYOUT` |
| Midblock option must visibly show offsets | CODE COMPLETE | Midblock command creates centre route plus two visible side-offset guides on separate layers |
| Road/parcels layout should resemble connected road-reserve plan, not erf inset loops | CODE COMPLETE / HOST ACCEPTANCE REQUIRED | Road-reserve centreline pairing and Road Layout Production replace closed-erf inset behavior for road planning |

## J. Sewer / Stormwater / Water sequencing, alignments, labels and networks

| Comment | Status | Implementation / command |
|---|---|---|
| Sewer sequence should continue automatically to alignments | CODE COMPLETE | `CE_SEWSEQPRODUCTION` |
| Stormwater sequence should continue automatically to alignments | CODE COMPLETE | `CE_SWSEQPRODUCTION` |
| Water sequence should continue automatically to alignments | CODE COMPLETE | `CE_WATERSEQPRODUCTION` |
| Optional profile handoff after sequence/alignment | CODE COMPLETE | Sewer/SW/Water sequence-production popups support optional profile queueing |
| Branch names/labels on separate layer | CODE COMPLETE | `CE_BRANCHLABELLAYER`, default `CE-BRANCH-LABELS`; universal refresh applies it automatically |
| Branch labels/names should appear after production | CODE COMPLETE / HOST ACCEPTANCE REQUIRED | Sequence-to-alignment workflows + existing discipline label engines + branch-layer refresh |
| Select/connect multiple pipes and structures | CODE COMPLETE | `CE_NETWORKMULTI`, existing `CE_NETWORKCONNECTALL`, `CE_NETWORKCONNECT`, network schedules |
| Create network from multiple lines/polylines/feature lines | ALREADY IMPLEMENTED / REVALIDATED | Existing `CE_NETWORKFROMPOLYLINES` / network creation hub |

## K. Sewer / SW / Water profile internal errors

| Comment | Status | Implementation / command |
|---|---|---|
| One bad Sewer alignment must not cancel all profile views | CODE COMPLETE | Staged Sewer profile creation now runs one branch per transaction and reports skipped branch names |
| One bad Stormwater alignment must not cancel all profile views | CODE COMPLETE | Staged SW profile creation uses singleton alignment transactions and continues |
| One bad Water alignment must not cancel all profile views | CODE COMPLETE | Staged Water profile loop uses one transaction per route/alignment and continues |
| Batch band-set/internal-error workflow must remain usable after one failure | CODE COMPLETE | `CE_PROFILEBATCHSAFE` separates import / style / band / refresh / discipline stages |

## L. Sewer excavation / pipe data

| Comment | Status | Implementation / command |
|---|---|---|
| Pipe diameter must show correct nominal sizes such as 110, 160, 200, 250, 300 mm | ALREADY IMPLEMENTED / REVALIDATED | Current Sewer excavation code normalizes host diameter to standard nominal diameter list |

## M. Survey / interoperability / CAD conversion

| Comment | Status | Implementation / command |
|---|---|---|
| Import LandXML under Survey | CODE COMPLETE | `CE_LANDXMLIMPORT` / `CE_LANDXMLTOOLS` |
| Export LandXML under Survey | CODE COMPLETE | `CE_LANDXMLEXPORT` / `CE_LANDXMLTOOLS` |
| Convert/export Civil design to CAD but keep current CAD/design drawing unchanged | CODE COMPLETE | `CE_EXPORTCADCOPY` uses the native Civil 3D export-to-AutoCAD-copy workflow |

## N. Drawing Tools — multiple boundaries

| Comment | Status | Implementation / command |
|---|---|---|
| Trim all objects outside multiple boundaries | CODE COMPLETE | `CE_TRIMOUTSIDEMULTI` |
| Trim all objects inside multiple boundaries | CODE COMPLETE | `CE_TRIMINSIDEMULTI` |
| Trim + delete all objects outside multiple boundaries | CODE COMPLETE | `CE_TRIMDELETEOUTSIDEMULTI` |
| Trim + delete all objects inside multiple boundaries | CODE COMPLETE | `CE_TRIMDELETEINSIDEMULTI` |
| Extend outside objects to selected boundaries | CODE COMPLETE | `CE_EXTENDOUTSIDEMULTI` |
| Extend inside objects to selected boundaries | CODE COMPLETE | `CE_EXTENDINSIDEMULTI` |

## O. Platform production

| Comment | Status | Implementation / command |
|---|---|---|
| Create feature lines from multiple polylines with surface popup | CODE COMPLETE | `CE_FLCREATE`, reused in `CE_PLATFORMTOOLS` |
| Constant high-low slope, fixed slope or flatten to highest | CODE COMPLETE | `CE_PLATFORMSLOPE` |
| Multiple linked stepped offsets | CODE COMPLETE | `CE_PLATFORMSTEPOFFSETS` |
| Drape selected steps to selected surface | CODE COMPLETE | `CE_PLATFORMDRAPE` |
| Surface changes should drive linked draped/source/step feature lines | CODE COMPLETE / HOST ACCEPTANCE REQUIRED | Platform dynamic refresh manager + `CE_PLATFORMREFRESH` |
| Assign platforms to site/separate surface and infill | CODE COMPLETE / HOST ACCEPTANCE REQUIRED | `CE_PLATFORMSURFACE`; reflection-safe host API calls |
| Vertex/grid setting-out for platforms | CODE COMPLETE | `CE_PLATFORMSETTINGOUT` |
| PLATFORM-1 / PLATFORM-2 labels with final elevation | CODE COMPLETE | `CE_PLATFORMNAMES` |
| Linked dynamic platform table | CODE COMPLETE | `CE_PLATFORMTABLE` |
| Linked cut/fill quantities using NG/design surfaces | CODE COMPLETE | `CE_PLATFORMCUTFILL` |
| Platform layouts / sections / production drawings | CODE COMPLETE | `CE_PLATFORMDRAWINGS` + existing dynamic section tools |
| Platform BOQ/report | ALREADY IMPLEMENTED / REVALIDATED | Existing `CE_BOQPLATFORM` / `CE_REPORTPLATFORM` integrated into workflow |

## P. Saved project settings versus active drawing settings

| Comment | Status | Implementation / command |
|---|---|---|
| When opening another drawing choose Keep existing drawing settings or Use saved project settings | CODE COMPLETE | `CE_SETTINGSMODE`; staged `DisciplineWorkflowDialogs` respects the chosen priority |

## Q. Cost-estimate workbook structure

| Comment | Status | Implementation / command |
|---|---|---|
| Use an approved XLSX/XLSM workbook as the cost-estimate structure | ALREADY IMPLEMENTED / REVALIDATED | Existing `CE_COSTTEMPLATESELECT` and `CE_COSTTEMPLATEINFO`; selected `.xlsx/.xlsm` is preferred and macro-enabled output is preserved |
| Replace the existing/default cost estimate with the exact `Annexure A - Asset Register (Version 4.1...)` internal structure | **EXTERNAL FILE REQUIRED** | The WhatsApp screenshot/name is available, but the actual workbook bytes/formulas/row structure are not present. Supply the `.xlsm/.xlsx` file and it can be set/mapped as the approved template without guessing its internals. |

This is the only listed comment that cannot be completed exactly from the currently available source material.

---

## R. Final build safeguards

The one-click Civil 3D 2023 stage/build process now executes the closure work in order before compilation:

1. Existing Civil 3D 2023 compatibility repairs.
2. Road / Platform / Drawing production integration.
3. Final runtime behavior integration.
4. Final annotation/table ribbon integration.
5. Final Sewer/Midblock/profile-style ribbon integration.
6. COGO bounded-overlap repair.
7. Road-only style fallback repair.
8. Dedicated branch-label-layer refresh integration.
9. Midblock Route Planner handoff repair.
10. Automatic bundled profile/band style import hooks.
11. Sewer / Stormwater / Water per-alignment profile isolation.
12. `Validate-August10CommentClosure.ps1` hard gate.
13. Existing source sanitation / Roslyn diagnostic.
14. Civil 3D 2023 .NET build and installation.

A Windows GitHub Actions workflow, `.github/workflows/validate-final-comment-closure.yml`, also executes steps 2–12 without requiring Autodesk DLLs. Its purpose is to catch PowerShell syntax errors, stale source markers, missing commands and broken staged integrations before the user's local Autodesk build.

## S. Final acceptance gate

Code-side comment closure is complete except for the exact Annexure-A workbook mapping noted above. The next local acceptance step is:

1. Close Civil 3D 2023.
2. Pull/download the latest `main`.
3. Run the existing one-click Civil 3D 2023 build/install process.
4. The build must first print that final comment-closure validation passed.
5. Open the acceptance drawing and test Road Layout, Route Planner, profiles, annotations, platforms and boundary tools.
6. Any Autodesk compile/runtime exception must be recorded with the exact filename, line number/command and error text; do not remove a feature merely to silence the error.
