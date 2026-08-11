# CE Tools – August 11, 2026 Field Test Punch List

Source: field-test comments and screenshots supplied on 2026-08-11.

Status legend: **FIXED-IN-BUILD** = compatibility/build repair applies the fix to the staged source before compilation; **TODO** = not yet claimed fixed.

## P0 – Runtime / build blockers

- **FIXED-IN-BUILD** Platform Production → Platform Slopes: AutoCAD unhandled exception / `Specified argument was out of the range of valid values`.
  - Cause isolated to feature-line elevation writes that iterate `FeatureLinePointType.AllPoints` and then use the loop index with `SetPointElevation(index, ...)`.
  - Build repair now normalizes platform elevation writes to point-based `SetPointRelativeElevation(point, false, elevation)` for platform slopes and stepped-offset child elevation transfer.
- **TODO** Verify Platform Slopes after rebuild on multiple closed platform feature lines and feature lines containing elevation points/arcs.

## Survey / COGO / setting-out

- **TODO** COGO point style is incorrect immediately after vertex setting-out/table creation; verify all COGO creation workflows.
- **TODO** Auto-refresh vertex setting-out after creation, whether or not a table is placed; no manual refresh command should be required.
- **TODO** Coordinate/setting-out tables should synchronize automatically after placement and after linked geometry changes.
- **TODO** COGO point labels move farther away every time overlap resolution is run.
- **TODO** Overlap resolution should move only the selected/problem labels and keep labels close to their COGO points.
- **TODO** Restore Annotation Positions must return COGO labels to their original/initial positions.
- **TODO** Setting-out points/table must stay dynamic when source feature line moves or its slope/elevation changes.
- **TODO** Route Planner/Junction vertex setting-out must work for polylines as well as arcs.
- **TODO** Coordinate register: allow multiple surfaces to be selected before table placement.
- **TODO** Project Information: automatically assign/link the coordinate system from the selected survey location/coordinate-system workflow.

## Junctions / bellmouths / road reserve cleanup

- **TODO** Cross-junction point/vertex sequence is incorrect.
- **TODO** Complete all four quadrants of one cross junction before continuing to the next junction.
- **TODO** Automatically create closed junction trimming boundaries/polylines (non-plot layer) for multiple junctions and trim road-reserve/road-edge geometry inside them.
- **TODO** Bellmouth arcs are offset too far from road edges even when the lane width is correct; generated bellmouth polylines appear correct.
- **TODO** Trim multiple road edges and shoulder/sidewalk edges to the start/end of bellmouths.

## Route Planner / road reserve production

- **TODO** Road reserve centreline command leaves gaps; create continuous centreline geometry through the road reserve.
- **TODO** Add/draw lane widths.
- **TODO** Add/draw sidewalk/shoulder widths.
- **TODO** Add/draw junction bellmouths.
- **TODO** General Road Offset: option to offset to outside of road edges and outside of sidewalk edges.
- **TODO** Route Planner: allow multiple horizontal centreline curves with specified radii where required.
- **TODO** Route Planner text/dimensions must be annotative and display in metres.
- **TODO** Route Planner annotation controls: background mask on/off, arrow size 3, paper text sizes 1.8 / 2.0 / 2.5 / 3.5 / 5.0.
- **TODO** Shift multiple/selected dimensions or text to resolve overlaps.
- **TODO** Convert Curves utility: include polyline-to-arc workflow in addition to curve/arc-to-polyline conversion.

## Roads – alignments, profiles, corridors and styles

- **TODO** Road profile view is reading the wrong profile-view style.
- **TODO** Profile-view band sets are not automatically imported.
- **TODO** Create a best-fit final-design profile including tangents and vertical curves.
- **TODO** Project Civil 3D Styles: support different discipline presets while all disciplines read/link from the same central style source.
- **TODO** Corridor exists in Prospector but is not displayed in the drawing.
- **TODO** Add corridor baselines and regions automatically.
- **TODO** Baselines/Regions command currently routes to a report; correct the command/action mapping.
- **TODO** Road names must be dynamically linked across alignments, profiles, corridors, sections, assemblies and production objects.
- **TODO** Resolve naming mismatch where drawing road name and Civil 3D alignment name differ (example: ROAD-4 vs RD-02).

## Networks – sewer / stormwater / water / bulk water

- **TODO** Create Network from Polyline/Feature Line: allow selecting multiple polylines in one run.
- **TODO** Avoid duplicate pipes, structures, labels and other network objects when rerunning production.
- **TODO** Connect: allow multiple pipes and structures to be selected and connected in one operation.
- **TODO** Close Pipes command is incorrectly triggering BOQ refresh; separate the actions.
- **TODO** Utility Route from Road Reserve: allow offsets for SW, Sewer, Water and Bulk Water from erf boundaries and/or road-reserve edges.

## Sewer midblock / road-reserve routing

- **TODO** Current midblock sewer production creates broken/gapped lines on both sides of erf rows. Create one continuous selected-side route per row.
- **TODO** Choose which side of the erf/road reserve is used for the route.
- **TODO** Route should follow the natural fall/low side where applicable.
- **TODO** Manhole diameter: 1.2 m.
- **TODO** Configurable maximum manhole spacing: 60 m or 80 m.
- **TODO** Place/move manholes about 1.5 m from closest erf corners where needed.
- **TODO** If a manhole lands on an erf boundary or awkward mid-erf location, shift it to the short side while keeping pipe lengths within the selected 60/80 m maximum.
- **TODO** Apply equivalent logic to utilities running inside road reserves.

## Tables / source navigation / PDF import

- **TODO** Table Source Zoom command throws an error.
- **TODO** Table Source Zoom should present a popup to select the linked pipe, structure or other design element represented by the table row/source.
- **TODO** PDF Import should provide a file-selection popup before conversion.
- **TODO** Tables should update automatically when linked design elements move/change; eliminate manual synchronize/refresh where possible.

## Production Centre / Workflow Centre / ribbon

- **TODO** Add a dedicated **CE-PRODUCTION WORKFLOW** ribbon tab and equivalent Workflow Centre entry.
- **TODO** Production disciplines: Project, Survey, Platform, Road, Stormwater, Sewer, Water, Bulk Water, Parking Area and Flood.
- **TODO** Put each discipline's Settings command at the top of its Production Centre subsection.
- **TODO** Production Centre should show only the important end-to-end commands, not every utility command.
- **TODO** Keep full command inventory in **CE-ENGINEERING INTELLIGENCE CENTRE** / existing full Workflow Centre.
- **TODO** Welcome screen when entering CE Tools, with two primary destinations:
  1. CE-PRODUCTION CENTRE – shortest guided production workflows.
  2. CE-ENGINEERING INTELLIGENCE CENTRE – full CE Tools command library.
- **TODO** Dark and Light themes for welcome screen, ribbon and icon set.
- **TODO** Use discipline-guided workflow structure such as:
  - Prepare
  - Create
  - Design
  - Complete
  - Deliver
  - `Run Complete <Discipline> Production`
- **TODO** Example Sewer Production flow:
  - Prepare: Cadastral → Roads → Existing Ground
  - Create: Network → Branches → Structures → Pipes
  - Design: Demand → Pipe sizing → Levels → Hydraulic check
  - Complete: Profiles → Labels → Setting Out → BOQ
  - Deliver: Drawings → Drawing Book → Design Report
- **TODO** Reduce repeated commands and take the shortest reliable route to the end result.

## Verification order

1. Rebuild/install and retest **Platform Slopes**.
2. Multi-object network creation and duplicate prevention.
3. Survey/COGO dynamic refresh and overlap/restore behaviour.
4. Junction sequencing and road/bellmouth cleanup.
5. Road profiles/styles/corridors/name linkage.
6. Sewer midblock/road-reserve production.
7. Production Centre / Workflow Centre restructuring and welcome/theme work.
