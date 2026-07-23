# CE Feature Line Tools validation checklist

## Test drawing

Create a copy of a Civil 3D 2023 drawing containing:

- at least three ordinary grading feature lines;
- one feature line on a locked layer;
- one data-shortcut/reference feature line where available;
- one feature line with PI points only;
- one feature line with additional elevation points;
- one feature line whose points are relative to a surface.

## CE_FLREPORT

- Preselect several ordinary feature lines and run `CE_FLREPORT`.
- Confirm each line reports 2D length, 3D length, minimum/maximum elevation, minimum/maximum grade and point count.
- Confirm the combined totals match Civil 3D properties.
- Confirm unsupported selected objects are reported as skipped.
- Confirm the command makes no drawing changes.

## CE_FLRAISE

- Select multiple absolute-elevation feature lines and enter `0.250`.
- Confirm every point rises exactly 0.250 drawing units.
- Undo once and confirm all selected feature lines return to their original elevations.
- Repeat with `-0.250` and confirm the lines lower correctly.
- Test a relative-to-surface feature line and confirm the relative offsets change by the entered value without removing the surface relationship.
- Confirm locked-layer and reference feature lines are skipped.
- Force an error in a test copy and confirm no partial changes are committed.

## CE_FLSETELEV

- Select multiple ordinary feature lines and enter one absolute elevation.
- Confirm every PI and elevation point receives the entered elevation.
- Confirm a relative point becomes absolute at the entered elevation.
- Undo once and confirm all selected feature lines return to their original elevations and relative settings.
- Confirm locked-layer and reference feature lines are skipped.

## CE_FLTOOLS menu and ribbon

- Run `CE_FLTOOLS` and test `Report`, `RaiseLower` and `SetElevation`.
- Confirm the **CE Tools > Feature Lines > Feature Line Tools** ribbon button launches the menu.
- Confirm direct commands `CE_FLREPORT`, `CE_FLRAISE` and `CE_FLSETELEV` also work.
