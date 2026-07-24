# Review Comments Batch 7 — Dynamic Sections, Reports and Plan Production

## Scope

This plan validates the final review-comments batch from issue #17:

- automatic cross-section creation from a user-drawn section line;
- sampled surfaces and intersected design elements;
- labels, dimensions, layerworks and utility details;
- deferred automatic refresh after grip or design-object changes;
- full and discipline design reports;
- linked project summary sheets;
- A4/A3 client and A1/A0 construction-book layouts;
- drawing-book index export.

Commands under test:

### Dynamic cross sections

- `CE_XSTOOLS`
- `CE_XSCREATE`
- `CE_XSREFRESH`
- `CE_XSINFO`
- `CE_XSDETACH`
- `CE_XSMONITOR`

### Reports and production

- `CE_REPORTTOOLS`
- `CE_REPORTFULL`
- `CE_REPORTDISC`
- `CE_REPORTEXPORT`
- `CE_REPORTROAD`
- `CE_REPORTPLATFORM`
- `CE_REPORTSTORM`
- `CE_REPORTSEWER`
- `CE_REPORTWATER`
- `CE_REPORTBULKWATER`
- `CE_SUMMARYSHEET`
- `CE_SUMMARYREFRESH`
- `CE_SUMMARYINFO`
- `CE_DRAWINGBOOK`
- `CE_BOOKINDEX`

## Release boundary

GitHub Actions cannot load Autodesk AutoCAD or Civil 3D managed assemblies.
Keep this PR in draft until it compiles and passes the checks below in both:

1. Civil 3D 2023, x64, Release.
2. Civil 3D 2024, x64, Release.

The implementation uses a deferred update manager. It records drawing changes in
`Database.ObjectModified` and `Database.ObjectAppended`, then refreshes linked
sections later from `Application.Idle` only while the editor is quiescent. It
does not mutate a drawing inside the database event callback.

Summary sheets use explicit `CE_SUMMARYREFRESH`. Drawing-book plot devices,
CTB/STB files and canonical printer-media names remain office/workstation
specific and are not assigned automatically in this batch.

## Build and load checks

For both Civil 3D versions:

- Compile the full plugin against the installed Autodesk assemblies.
- Confirm document collection events compile and attach correctly.
- Confirm `Database.ObjectModified` and `Database.ObjectAppended` handlers load.
- Confirm `Editor.IsQuiescent` and `Document.LockDocument()` calls compile.
- Confirm `CivilSurface.FindElevationAtXY` compiles.
- Confirm `Entity.IntersectWith` compiles with the selected overload.
- Confirm `LayoutManager.Current.CreateLayout` compiles.
- Load the bundle using the normal CE Tools deployment process.
- Confirm no duplicate-command, duplicate-ribbon or event-handler exception.
- Open and close multiple drawings and verify the manager detaches cleanly.

## Dynamic-section test drawing

Prepare a drawing containing a representative road and services corridor:

- Existing-ground and design Civil 3D surfaces.
- Feature lines with non-zero elevations.
- Road kerb and channel linework.
- Sidewalk, parking and driveway layer boundaries.
- V-drains and open channels.
- Stormwater, sewer and water pipes crossing the section line.
- Manholes, catchpits, hydrants and valves near the section line.
- Road signs or blocks.
- Design objects that do not intersect the section line.
- Objects on deliberately ambiguous layers for classification checks.

Use realistic Z values. Include one surface hole and one section line extending
beyond the surface boundary.

## Cross-section creation

1. Draw an AutoCAD Line across the road and utilities.
2. Run `CE_XSCREATE`.
3. Select the line.
4. Pick an insertion point away from the plan design.
5. Enter horizontal and vertical display factors.
6. Enter a surface sample interval.
7. Enter the plan capture half-width.
8. Review the command-line preview.
9. Confirm creation.

Verify:

- The source line receives an extension-dictionary Xrecord named
  `CE_DYNAMIC_SECTION`.
- Every generated object receives `CE_DYNAMIC_SECTION_GENERATED`.
- The generated section has a title and reported datum.
- A horizontal axis and vertical/elevation grid are created.
- Offset and elevation labels are legible.
- Existing-ground and design surfaces are sampled independently.
- Surface samples outside the surface are skipped without cancelling the full
  section.
- Intersected feature lines and curves produce section markers.
- Captured nearby blocks/points/utilities appear only inside the specified
  half-width.
- Utility markers show an available diameter or size property.
- Layerworks and road features are labelled from layer/object information.
- An aligned width dimension is created.
- The intersected-design-elements schedule contains surfaces and section items.
- Generated text respects normal drawing text height rather than legacy
  2500/5000 values.
- One AutoCAD Undo removes the created linked section.

Repeat with an open lightweight polyline containing exactly two vertices.
Confirm closed polylines and polylines with more than two vertices are rejected.

## Section-line direction

Create two identical section lines with opposite directions.

Verify:

- Left/right offset ordering reverses with the source-line direction.
- Surface profile geometry follows the selected line direction.
- Intersected item offsets use the same direction convention.
- The schedule and labels remain internally consistent.

## Manual refresh

1. Create a linked section.
2. Modify surface elevations.
3. Move a feature line.
4. Change a pipe or structure position.
5. Run `CE_XSREFRESH`.

Verify:

- The preview shows current profile and feature counts.
- Choosing No leaves existing generated geometry unchanged.
- Choosing Yes replaces all generated objects in one transaction.
- Old generated handles are removed from the link record.
- New generated handles resolve correctly.
- Existing geometry is retained when no design elements are found.
- One Undo restores the previous generated section state.

## Deferred automatic refresh

1. Run `CE_XSMONITOR` and confirm the manager is active.
2. Grip-move one endpoint of the linked section line.
3. Complete the AutoCAD grip command.
4. Wait for AutoCAD to become idle.

Verify:

- No drawing mutation occurs while the grip command is active.
- A refresh occurs after the editor becomes quiescent.
- The section width, surface profiles and offsets update.
- The source Xrecord remains attached to the moved line.
- The update does not recursively queue itself.
- AutoCAD remains responsive after several rapid grip edits.

Then modify a design feature or append new crossing linework.
Verify all linked sections in the active drawing are coalesced and refreshed.

Test with two linked sections and rapid multiple object edits. Confirm there are
no endless refresh loops or duplicate generated objects.

## Multi-document manager checks

1. Open two Civil 3D drawings.
2. Create linked sections in both.
3. Modify the inactive drawing through a normal command after activating it.
4. Switch between drawings.
5. Close one drawing.

Verify:

- Each database is attached once.
- The inactive document is not locked or mutated while another document is
  active.
- Pending refresh waits until its document is active and quiescent.
- Closing a drawing detaches database events.
- Closing Civil 3D produces no event-handler exception.

## Cross-section information and detach

Run `CE_XSINFO` by selecting:

- the source line;
- a generated profile;
- a generated label;
- the generated schedule.

Verify the same source is resolved and the pop-up reports:

- schema;
- source handle;
- current section-line length;
- insertion coordinates;
- horizontal and vertical factors;
- sample interval;
- capture half-width;
- valid and stale generated handles;
- automatic plus explicit update mode.

Run `CE_XSDETACH` twice:

1. Keep generated geometry.
2. Delete generated geometry.

Verify the source link is removed in both cases. Kept objects must have their
generated-owner records removed and behave as ordinary AutoCAD entities.

## Full and discipline reports

Populate project metadata using `CE_PROJECTSETUP`, then run:

- `CE_REPORTFULL`
- `CE_REPORTROAD`
- `CE_REPORTPLATFORM`
- `CE_REPORTSTORM`
- `CE_REPORTSEWER`
- `CE_REPORTWATER`
- `CE_REPORTBULKWATER`
- `CE_REPORTDISC`

Verify:

- Reports open in the shared grid pop-up.
- Optional drawing tables can be placed.
- Objects are grouped by discipline, layer and runtime type.
- Count, available length, area and volume values are reported.
- Linked BOQ and linked cross-section status appears in report notes.
- Layout count and project/client metadata are included.
- Unsupported/unreadable objects do not cancel the report.
- Ambiguous layers are classified as General rather than silently forced into
  a wrong discipline.
- Classification results are reviewed against project standards.

## Report Excel export

Run `CE_REPORTEXPORT` for All and each discipline.

Verify:

- A valid `.xlsx` workbook is produced.
- Excel opens it without repair warnings.
- The report columns match the on-screen report.
- Count, length, area and volume cells are numeric where populated.
- Project, client, layout and link summary values are included.
- Excel COM automation and Office installation are not required to create the
  workbook.

## Linked project summary sheet

1. Run `CE_SUMMARYSHEET`.
2. Pick an insertion point.
3. Review and confirm.

Verify:

- A bordered presentation-style summary is created.
- Project name, client, town, country, coordinate system, standards, template
  and units are displayed.
- Discipline object counts and available length/area/volume totals are shown.
- Linked BOQ, linked cross-section and layout readiness are shown.
- Standard A4/A3/A1/A0 layout status is shown.
- The frame is the summary anchor and stores `CE_PROJECT_SUMMARY_SHEET`.
- Generated summary objects store `CE_PROJECT_SUMMARY_GENERATED`.
- One Undo removes the complete summary.

Change project metadata, add a layout and modify design objects. Run
`CE_SUMMARYREFRESH`.

Verify:

- cancellation leaves the summary unchanged;
- confirmed refresh replaces generated summary objects transactionally;
- the new frame remains the linked anchor;
- `CE_SUMMARYINFO` resolves from the frame or any generated summary object;
- stale handles are reported rather than causing failure.

## Drawing-book layouts

Run `CE_DRAWINGBOOK` in a drawing with several paper-space layouts.

Verify creation or refresh of:

- `CE-CLIENT-A4` — 297 × 210 mm frame.
- `CE-CLIENT-A3` — 420 × 297 mm frame.
- `CE-CONSTRUCTION-A1` — 841 × 594 mm frame.
- `CE-CONSTRUCTION-A0` — 1189 × 841 mm frame.

For each layout verify:

- the paper-space frame dimensions are correct in millimetres;
- the project title and intended issue purpose are shown;
- a drawing register is created;
- a note clearly states that PC3, CTB/STB and canonical media assignment are
  workstation-specific;
- the Layout object stores `CE_DRAWING_BOOK_LAYOUT`;
- rerunning the command refreshes only CE Tools generated book objects;
- existing non-CE layout content is not erased;
- no duplicate layouts are created;
- each layout remains editable by the drafter.

Assign the office-approved PDF PC3 and media manually, then publish sample A4,
A3, A1 and A0 PDFs. Confirm paper orientation and clipping.

## Drawing-book index export

Run `CE_BOOKINDEX` and open the workbook in Excel.

Verify:

- all four standard book layouts are listed;
- each is marked Available or Missing correctly;
- existing project layouts are also listed;
- the project name is included;
- workbook opens without repair warnings.

## Dynamic-link boundary checks

Confirm the following are true and documented:

- Linked cross sections monitor grip and design-object modifications while CE
  Tools is loaded.
- Refresh is deferred until AutoCAD is idle and quiescent.
- Summary sheets use explicit `CE_SUMMARYREFRESH`.
- BOQs continue to use explicit `CE_BOQREFRESH`.
- Layout paper frames are generated, but office plot configuration is not
  guessed or silently replaced.
- Classification is an initial layer/name/type mapping and is not claimed as a
  complete national or client standard library.

## Regression checks

Confirm these commands remain available and unchanged:

- `CE_BMVERT`
- `CE_TLENGTH`
- `CE_TAREA`
- Batch 1 project and standards tools.
- Batch 2 shared report pop-ups.
- Batch 3 shared annotation settings.
- Batch 4 corridor, feature-line and parking repairs.
- Batch 5 linked survey-coordinate tools.
- Batch 6 linked BOQs and discipline Excel exports.

## Acceptance record

Record separately for Civil 3D 2023 and 2024:

- build result and assembly path;
- bundle load result;
- section-line creation result;
- surface sampling result;
- feature/utility intersection result;
- grip-edit automatic refresh result;
- multi-document event result;
- detach result;
- full and discipline report result;
- summary create/refresh result;
- drawing-book layout result;
- A4/A3/A1/A0 PDF plot result;
- Excel report/index open result;
- regression results;
- DWG names, tester and date.
