# CE Tools preservation manifest — 30 July 2026

This branch exists to consolidate the working CE Tools source without losing features from older build packs or local snapshots.

## Non-negotiable preservation rules

- Do not overwrite or delete working commands merely to make a build pass.
- Keep Civil 3D 2023 as the primary supported host.
- Keep existing V50, V52, V54, V55 and V60 build snapshots outside the active source until reconciliation is complete.
- Reconcile source file-by-file and command-by-command.
- Build and run validators after each logical group of changes.
- Install only from a compiled application bundle, with the currently installed bundle backed up first.

## Features that must remain present

### Survey and coordinates

- Multiple-polyline vertex selection.
- Optional selectable COGO-point creation.
- Dynamic point labels and tables.
- P1, P2, P3 numbering rather than continuing from an unrelated point number.
- X, Y and Z display, with Z updating when geometry moves.
- Raw-description linking using point name and number prefix.
- Coordinate markers, coordinate crosses and coordinate grids.
- Dynamic table refresh and overlap control.

### Stormwater and sewer

- Alignment creation from selected polylines and existing networks.
- Main-branch-first and automatic branch-sequencing options.
- Correct name, number and branch sequence.
- Profile-view creation with selectable styles and band sets.
- Branch labels offset away from alignment geometry.
- Branch label text-height options using true annotative paper sizes.
- Separate discipline symbols and colours for stormwater and sewer.
- Manhole numbering beginning at .1.
- Correct source, size and pipe-length extraction from the drawing.

### Roads, grading and parking

- Dynamic feature-line workflows.
- Multiple-polyline direction and reverse commands.
- Closed/block-based parking rather than loose linework.
- Dynamic arrows that follow geometry and reverse correctly.
- Boundary-driven parking layout and refresh.
- Parking skew validation and grading diagnostics.
- Alignment, profile, corridor and section production workflows.

### Annotation and presentation

- Floating CE Tools workflow window at startup and through Ctrl+F.
- Discipline workflows for General, Survey, Roads, Stormwater, Sewer, Water, Bulk Water and Flood.
- Popup-based commands rather than command-line-only workflows where implemented.
- Annotative MText, MLeaders, COGO labels and tables using paper sizes 1.8, 2.0, 2.5, 3.5 and 5.0.
- Correct alignment-name sizing and label-overlap management.
- Polished CE TOOLS ribbon and preserved command registry.

### Quantities, BOQs and reports

- Linked water and sewer cost estimates.
- Dynamic BOQ refresh after drawing changes.
- Shared network reports and service-production launchers.
- Road, section, drawing-book and client-book production commands.
- A4/A3 client books and project closeout workflows.

### Standards and assets

- Complete 33-asset library.
- Henties standards commands and DWG standards files where present in preserved snapshots.
- PDFs, templates, spreadsheets, icons and supporting documentation must not be dropped from release packs.

## Known build repairs to retain

- Resolve AutoCAD and Civil 3D 2023 references explicitly.
- Include `System.Windows.Forms` for `DialogResult` usage.
- Qualify WPF `Visibility.Visible` and `Visibility.Collapsed` correctly.
- Preserve corrected floating-window handle ownership.
- Preserve corrected parking `Point2d`/`Vector2d` calculations.
- Preserve nullable-double handling in setting-out schedules.
- Avoid brittle comment and Master Items normalizers during normal builds.
- Build the already-normalized source directly.

## Regression gate

Before merging this branch:

1. Compare every command name between the repository and preserved snapshots.
2. Compare every source, script, document, template and asset path.
3. Confirm the branch-label offset visually in Civil 3D 2023.
4. Compile with zero errors against Civil 3D 2023.
5. Run all repository validators and tests.
6. Install into a backed-up user bundle.
7. Test the ribbon and the major workflows in a copied drawing.
8. Record the tested commit SHA and DLL SHA-256.
