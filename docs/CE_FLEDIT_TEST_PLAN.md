# CE_FLEDIT validation checklist

## Create

- Convert one Line, Arc, 2D Polyline, 2D legacy Polyline and 3D Polyline.
- Confirm unsupported selected objects are skipped.
- Confirm objects on locked layers are skipped.
- Confirm invalid geometry cancels the transaction without partial results.
- Confirm created objects are ordinary siteless Civil 3D feature lines.

## Surface

- Select multiple ordinary feature lines and one Civil 3D surface.
- Run with intermediate grade-break points set to No and verify only existing points change.
- Run with intermediate grade-break points set to Yes and verify surface break points are inserted.
- Test a feature line outside the surface boundary and confirm the command fails safely.
- Confirm referenced and locked-layer feature lines are skipped.

## Insert

- Insert an interpolated elevation point on a straight segment.
- Insert an interpolated elevation point on a curved segment.
- Insert a point with a manually entered elevation.
- Attempt to insert on an existing PI or elevation point and confirm no partial change is committed.

## Delete

- Pick near one elevation point and confirm CE Tools identifies the nearest removable point.
- Confirm the default response is No.
- Confirm Yes removes only the chosen elevation point.
- Confirm PI points cannot be removed through this command.
- Confirm one UNDO restores the deleted point.

## General

- Confirm all modifying commands are reversed by one UNDO.
- Confirm source drawings are tested on copies only until the alpha passes validation.
