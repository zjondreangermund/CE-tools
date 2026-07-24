# Review Comments Batch 5 — Civil 3D Test Plan

## Scope

This plan validates the linked survey-coordinate workflows introduced for the
23 July 2026 review comments.

Commands under test:

- `CE_COORDPICK2`
- `CE_COORDCROSS2`
- `CE_COORDTABLE2`
- `CE_COORDREFRESH`
- `CE_COORDPOLY2`
- Existing `CE_PLDIR`

The legacy `CE_COORDINATE`, `CE_COORDPICKX`, `CE_COORDCROSSX` and
`CE_COORDPOLY` commands remain available during validation.

## Build matrix

Compile the complete plug-in against:

1. Civil 3D 2023 / AutoCAD 2023 managed assemblies.
2. Civil 3D 2024 / AutoCAD 2024 managed assemblies.
3. .NET Framework 4.8, Release configuration.

Reject the batch if there are ambiguous AutoCAD/Civil type references, missing
API members, command-registration warnings or bundle-load errors.

## Test drawing preparation

Create a clean metric drawing containing:

- One lightweight 2D polyline with at least five vertices.
- One 3D polyline with different elevations.
- One reversed copy of the 2D polyline.
- Several ordinary AutoCAD points.
- Several Civil 3D COGO points.
- A rotated UCS.
- Model-space and paper-space test areas.

Set `PDMODE` so AutoCAD point anchors are visible during validation.

## 1. Shared coordinate settings

Run `CE_ANNOTSETTINGS` and test each height:

- 1.8
- 2.0
- 5.0

Test marker circle Yes and No. Test MLeader, MText and COGO output.
Confirm the settings persist after saving, closing and reopening the drawing.

## 2. Picked coordinate workflow

Run `CE_COORDPICK2` with each annotation output.

Verify:

- The picked UCS point is transformed correctly into WCS coordinates.
- MLeader and MText use the selected text height.
- Marker circles respect the saved setting.
- COGO output creates one point, not duplicate points.
- The COGO raw description contains Y, X and Z values.
- `None` creates no table.
- `New` creates a compact table with one data row.
- `Existing` appends the point to the selected linked table.
- Cancelling at any prompt does not create partial output.

## 3. Linked coordinate register

Create a new register with `CE_COORDTABLE2` from mixed COGO and AutoCAD
points.

Verify:

- An empty or unsupported selection does not create a table.
- The table contains exactly five columns:
  - Point
  - Point Name
  - Y / Northing
  - X / Easting
  - Z / Elevation
- Rows are not oversized or mostly empty.
- Table width remains reasonable at 1.8, 2.0 and 5.0 text heights.
- Unsupported objects are counted as rejected.
- The table follows the current drawing table style.

Move an AutoCAD point and edit a COGO point coordinate. Run
`CE_COORDREFRESH` and confirm both rows update.

Erase one source point and refresh. Confirm valid rows remain and the command
reports a missing source without clearing the whole table.

Attempt to refresh an ordinary AutoCAD table and confirm it is rejected as an
unlinked table.

## 4. Coordinate-cross workflow

Run `CE_COORDCROSS2` and test combinations of:

- COGO point Yes/No.
- Cross linework Yes/No.
- Annotation Yes/No.
- Register New/Existing/None.

Verify:

- COGO annotation output automatically enables a COGO point.
- Cross linework is centred on the picked coordinate.
- The selected annotation height controls cross and marker sizing.
- A linked table row uses the source COGO point or AutoCAD point anchor.
- No duplicate COGO point is created when COGO is selected for both point and
  annotation output.
- Undo removes the command output as one user operation where Civil 3D permits.

## 5. Polyline vertex points

Run `CE_COORDPOLY2` on the prepared 2D and 3D polylines.

Verify:

- One COGO point is created at every distinct stored vertex.
- Point order follows the polyline direction.
- Reversing the polyline reverses the generated sequence.
- A closing duplicate vertex is not repeated.
- Generated names/descriptions use the entered prefix and sequence.
- The table columns are Point, Point Name, Y, X and Z in that order.
- The table is linked to the generated COGO points.
- Moving a generated COGO point followed by `CE_COORDREFRESH` updates its row.
- A failure while creating the table removes generated COGO points where
  possible.

Compare curved lightweight-polyline vertex coordinates with AutoCAD LIST and
Civil 3D point data. Only stored vertices should be created; arc tessellation
is outside this batch.

## 6. Direction arrows

Run existing `CE_PLDIR` on open, closed and reversed polylines.

Verify:

- Arrows follow the stored direction.
- Reversed polylines show reversed arrows after refresh/replacement.
- The command remains functional after loading the Batch 5 build.

## 7. Regression checks

Confirm these commands still load and operate:

- `CE_BMVERT`
- `CE_TLENGTH`
- `CE_TAREA`
- `CE_COORDINATE`
- `CE_COORDPICKX`
- `CE_COORDCROSSX`
- `CE_COORDPOLY`
- `CE_PLDIR`
- `CE_CORREBUILD`
- `CE_FLRAISE`

## Release decision

Keep the PR in draft until:

- Civil 3D 2023 compilation passes.
- Civil 3D 2024 compilation passes.
- Linked table refresh is confirmed in saved/reopened DWGs.
- UCS coordinate tests pass.
- One-step Undo behaviour is accepted.
- No previously working ribbon or command-line tool is removed.
