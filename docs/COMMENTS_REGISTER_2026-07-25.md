# CE Tools comments register — 25 July 2026

Source review file: `CE Tools - Comments - 25-07-2026.docx`.

## Imported colour rules

- **Red:** no update was recorded in the review document; implement first.
- **Yellow:** new active comment; implement through shared infrastructure and discipline batches.
- **Green:** previously corrected; preserve and regression-test.
- **Struck through:** superseded by a newer comment; exclude from active scope.

## Imported counts

The colour-aware document review found:

- 4 active red requirements;
- 313 active yellow requirements after removing repeated wording within each section;
- 8 green regression requirements;
- 42 struck-through requirements excluded as superseded.

Repeated cross-discipline wording—annotative text, scale-aware tables, marker circles, popup reports, manual table placement, COGO/MText output, overlap management, automatic refresh and Excel export—is implemented through shared services rather than duplicated separately in every command.

## Current branch and validation boundary

- Branch: `followup/comments-2026-07-25`
- Draft PR: `#37`
- Base: the exact PR #36 head that compiled successfully in Civil 3D 2023.
- Source normalizers, command-registry audits, existing regression validators and host-independent geometry tests run in GitHub Actions.
- Autodesk-dependent compilation and runtime testing remain mandatory in Civil 3D 2023 and Civil 3D 2024.
- No installation or merge is permitted until the exact final head passes those local tests.

## Red requirements — coding complete

### Project Setup

- [x] Replace separate project setup prompts with one WPF popup window.
- [x] Preload existing DWG values.
- [x] Keep review confirmation and one-transaction storage.
- [x] Keep an optional drawing table showing all saved project results.
- [x] Preserve project clear/restore backup behaviour.
- [ ] Civil 3D 2023 runtime test.
- [ ] Civil 3D 2024 runtime test.

### Alignment labels

- [x] Route the legacy alignment-label workflow to shared annotation settings.
- [x] Present 1.8, 2.0 and 5.0 text-height choices instead of inheriting values such as 5000.
- [x] Retain marker-circle and MLeader/MText/COGO output choices.
- [ ] Civil 3D 2023 runtime test.
- [ ] Civil 3D 2024 runtime test.

### Parking count and numbering

- [x] Generate every single-row parking bay as an individual closed four-sided polyline.
- [x] Generate every double-row parking bay as an individual closed four-sided polyline.
- [x] Keep generated bays directly selectable by report, count and numbering commands.
- [x] Use shared 1.8, 2.0 and 5.0 numbering height.
- [x] Retain popup validation and rejected-object explanations.
- [ ] Civil 3D 2023 runtime test.
- [ ] Civil 3D 2024 runtime test.

## Yellow requirements — coding coverage complete

### A. Project, ribbon, styles and undo/redo

- [x] Prefix CE Tools panel, flyout and command labels with `CE -` while retaining the `CE TOOLS` tab.
- [x] Add `CE_TOOLSPALETTE`, a modeless searchable launcher with individual buttons for second-screen use.
- [x] Add `CE_PROJECTSTYLES` with Road, Stormwater, Sewer, Water and Platform style schedules read from the active Civil 3D drawing.
- [x] Store alignment, label-set, profile, profile-view, band-set, point, surface, corridor, code-set, assembly, pipe, structure, pressure-pipe, fitting and appurtenance choices in the DWG.
- [x] Add `CE_PROJECTSTYLEINFO` and `CE_PROJECTSTYLECLEAR`.
- [x] Add `CE_UNDOSETTINGS`, `CE_UNDO` and `CE_REDO` while retaining native AutoCAD undo/redo rules.

### B. Coordinate Tools and Survey Utilities

- [x] Use shared 1.8, 2.0 and 5.0 annotation settings.
- [x] Use Point Name, X-Coordinate, Y-Coordinate and Z-Coordinate wording without the superseded point-number table column.
- [x] Provide MLeader, MText and COGO output choices with small marker circles.
- [x] Create linked coordinate registers with manual new-table placement or an existing linked table.
- [x] Refresh coordinate tables from moved COGO/AutoCAD points.
- [x] Link generated coordinate annotations, markers and crosses to source points.
- [x] Link polyline-vertex COGO points to source vertex order and refresh them after source changes.
- [x] Preserve linked polyline direction arrows and add multiple-polyline reverse support.
- [x] Use compact scale-aware table geometry and shared overlap controls.

### C. Drawing, Cleanup and Hatch Tools

- [x] Add shared annotative conversion, scale-aware table sizing and overlap-resolution commands.
- [x] Add geometry-only or geometry-plus-annotation Colour 250 handling.
- [x] Preserve linked direction arrows and use default practical arrow sizing.
- [x] Add Cleanup Manager review/popup workflow.
- [x] Add Hatch settings/review popup workflow.
- [x] Preserve Bellmouth Densifier and existing drawing utilities.

### D. Feature Line Tools

- [x] Preserve explicit `CE_FLRAISEX` mutation instead of report-only behaviour.
- [x] Preserve surface-selection popup `CE_FLSURFACEUI`.
- [x] Preserve constant-grade-between-endpoints workflow.
- [x] Preserve linked stepped offsets, information, refresh and detach commands.
- [x] Add `CE_FLREPORT2` popup/table report.
- [x] Add `CE_FLAPPEARANCE` for colour and Civil 3D site assignment.
- [x] Add `CE_FLVERTEXLABELS` for shared Point Name/X/Y/Z annotation at all selected vertices.
- [x] Use shared annotation, table and automatic-refresh infrastructure.

### E. Alignment, Profile and Surface Tools

- [x] Preserve popup alignment reports, station/offset query and shared annotation output.
- [x] Add `CE_PROFILEREPORT2` for all-profile inventory and optional drawing table.
- [x] Add `CE_PROFILEELEVATION2` with profile picker, popup/table and optional annotation.
- [x] Add `CE_SURFACEREPORT2` for all-surface inventory.
- [x] Add `CE_SURFACEELEVATION2` with surface picker, popup/table and optional annotation.
- [x] Add `CE_SURFACECOMPARE2` with base/final surface picker, cut/fill difference popup/table and optional annotation.
- [x] Preserve reversible surface audit/correction/simplification workflows.
- [x] Add shared rebuild and refresh commands.

### F. Corridor and Parking Tools

- [x] Preserve `CE_CORREPORTUI`, `CE_CORBASEUI` and `CE_CORLABELX` popup/table/annotation workflows.
- [x] Preserve explicit `CE_CORREBUILDX`, which calls corridor rebuild after review.
- [x] Retain shared 1.8, 2.0 and 5.0 corridor annotations with marker circles.
- [x] Create countable/numberable closed parking bays.
- [x] Retain parking report, validation/count, shared-height numbering and rejected-object explanations.
- [x] Preserve perpendicular skew-width validation, 2500 mm target and reversible correction outlines.

### G. Stormwater, Sewer, Water and Pipe Networks

- [x] Preserve stormwater sequencing with automatic or user-selected main branch.
- [x] Preserve linked stormwater alignments, profile views, style selection and network-part display.
- [x] Preserve sewer automatic/selected-main sequencing, branch alignments, styles, label spacing and profiles.
- [x] Preserve water main/branch sequencing, alignments, profiles and linked valve/hydrant/air/scour review markers.
- [x] Add `CE_NETWORKREPORT2` for gravity/pressure network summary and optional drawing table.
- [x] Add `CE_NETWORKPARTREPORT2` for selected pipe, structure, fitting and appurtenance data.
- [x] Add `CE_SERVICEPROFILES` as one popup launcher for stormwater, sewer and water sequencing/alignment/profile workflows.
- [x] Add `CE_NETWORKDATA` for network reports, discipline production information and refresh.
- [x] Correct generic service-length extraction by checking native length and endpoint properties instead of defaulting to 1 m.

### H. Quantities, BOQ and Reports

- [x] Preserve linked discipline BOQs and Excel exports for Road, Platform, Stormwater, Sewer, Water and Bulk Water.
- [x] Preserve matching rates when linked BOQs refresh.
- [x] Keep Road and Platform as separate disciplines.
- [x] Add `CE_BOQCENTER`, consolidating build, refresh, information, discipline exports, Total Length, Total Area and global refresh.
- [x] Add linked sewer excavation schedule `CE_SEWEREXCAVATION`.
- [x] Calculate pipe length, diameter, average cover, trench width/depth, excavation, bedding, pipe displacement and backfill.
- [x] Store pipe handles and assumptions for `CE_SEWEREXCAVATIONREFRESH`.
- [x] Add `CE_SEWEREXCAVATIONINFO` and `CE_SEWEREXCAVATIONEXPORT`.
- [x] Include sewer excavation schedules in `CE_REFRESHALL`.
- [x] Add `CE_REPORTCENTER` for full, discipline, network, feature-line, profile and surface reports.

### I. Dynamic Cross Sections and Intersections

- [x] Preserve linked dynamic cross-section creation, refresh, information, detach and monitoring.
- [x] Preserve linked dynamic intersections from feature lines, corridors and curves.
- [x] Preserve update managers and explicit refresh commands.
- [x] Integrate shared annotative/table/overlap and global refresh controls.

### J. Road and Plan Production, Books, Printing and Refresh

- [x] Add `CE_ROADPRODUCTION` as one road-production window.
- [x] Add `CE_ROADALIGN` for sequential named alignments from selected open polylines.
- [x] Use Project Style Centre alignment and label-set choices where available.
- [x] Add `CE_ROADPROFILES` for EG profiles and styled profile views from a selected surface.
- [x] Add `CE_ROADCORRIDORS` for alignment/profile/assembly corridor creation through compatible Civil 3D APIs.
- [x] Add `CE_ROADPRODUCTIONINFO` popup/table status report.
- [x] Add `CE_PRODUCTIONCENTER` for project summary, A4/A3 client books, A1/A0 construction layouts, registers and refresh.
- [x] Add `CE_PRINTCENTER` and `CE_BATCHPUBLISH` using AutoCAD's native Plot/Publish interfaces.
- [x] Add `CE_OUTPUTLOCATION` explaining DWG layout, Excel and PDF output locations.
- [x] Add `CE_REFRESHALL`, `CE_AUTOREFRESH` and `CE_REFRESHSTATUS` for linked coordinates, BOQs, sewer excavation, surfaces and corridors.

### K. Typical Details Library and Dynamic Variants

- [x] Preserve master-library root, search, category and approved-DWG insertion workflows.
- [x] Preserve review/register workflows for DWG, DXF and PDF source assets.
- [x] Preserve dynamic generated variants for Trench Drain, Pipe Trench, Valve Chamber, Kerb and Headwall.
- [x] Preserve parameter edit, refresh, information, BOQ, Excel export, review status, detach and clear workflows.
- [x] Keep source DWGs read-only while generated drawing objects and BOQs update.
- [ ] Populate and visually standardise the complete office master library after the user's remaining DWG/DXF/PDF assets are uploaded.
- [ ] Engineering review of uploaded detail dimensions, reinforcement, materials and notes.

## Shared automatic refresh and presentation foundation

- [x] `CE_PRESENTATIONTOOLS` consolidates annotation, table, overlap, reverse, refresh and rebuild controls.
- [x] `CE_AUTOREFRESH` monitors accepted source changes and defers refresh safely to application idle.
- [x] `CE_REFRESHALL` refreshes dynamic coordinate followers, linked coordinate registers, linked BOQs, sewer excavation tables, surfaces and corridors.
- [x] `CE_REBUILDALL` and `CE_REBUILDSERVICES` rebuild accessible Civil 3D design objects.
- [x] Existing dynamic-section and dynamic-intersection monitors remain enabled.

## Green requirements preserved

Regression coverage retains these previously corrected behaviours:

- project information can be recovered after clear;
- coordinate-system assignment opens Autodesk's native selection interface;
- polyline direction arrows work and remain linked;
- Bellmouth Densifier remains available;
- constant grades between feature-line endpoints remain available;
- Total Length remains working;
- Total Area remains working;
- existing linked BOQ, client-book, dynamic section, dynamic intersection, surface-correction and typical-detail workflows remain registered;
- other green-highlighted behaviour is not intentionally rolled back.

## Superseded comments

Struck-through items are not implementation targets. A newer non-struck comment takes precedence, and old behaviour must not be reintroduced merely to satisfy superseded wording.

## Remaining gates before merge

1. Build the exact final PR head for Civil 3D 2023 Release x64.
2. Correct any Autodesk API/compiler issues revealed by the local build.
3. Install into a backed-up test bundle and complete the runtime checklist.
4. Repeat compilation and runtime validation in Civil 3D 2024.
5. Upload and review remaining office typical-detail source assets.
6. Keep PR #37 draft and unmerged until all applicable gates pass.
