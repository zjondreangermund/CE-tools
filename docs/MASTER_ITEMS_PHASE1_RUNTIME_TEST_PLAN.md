# CE Tools Master Items Phase 1 runtime test plan

Use this plan only after the exact branch head compiles successfully in Civil 3D 2023. Repeat every applicable section in Civil 3D 2024 before merging.

## Safety

- Use disposable DWG copies.
- Keep the installed CE Tools bundle backup.
- Do not use production XREF files for the first split/backup tests.
- Hydraulic results are preliminary screening values and must not be issued as certified design calculations.

## 1. Ribbon and command registry

Confirm the following menus and commands appear and execute without duplicate-command errors:

- Parking: `CE_PARKOPTIONS`, `CE_PARKOPTIONSREFRESH`, `CE_PARKOPTIONSINFO`, `CE_PARKOPTIONSCLEAR`.
- Grading review: `CE_GRADINGDIAGNOSTICS`, `CE_LOWSLOPE`, `CE_LOWPOINTS`, `CE_GRADINGREVIEWCLEAR`.
- Background/XREF: `CE_BACKGROUNDTOOLS`, `CE_BACKGROUNDREVIEW`, `CE_BACKGROUNDLIGHT`, `CE_XREFSPLIT`, `CE_XREFINFO`, `CE_XREFBACKUP`.
- Setting out: `CE_SETTINGOUTTOOLS`, `CE_SETTINGOUTPOINTS`, `CE_SETTINGOUTREFRESH`, `CE_SETTINGOUTEXPORT`, `CE_SETTINGOUTINFO`.
- Hydraulic review: `CE_HYDRAULICTOOLS`, `CE_CATCHMENTQUICK`, `CE_RATIONALFLOW`, `CE_CULVERTREVIEW`, `CE_PUMPREVIEW`, `CE_HYDRAULICCLEAR`.

## 2. Boundary parking alternatives

Create a closed, irregular parking boundary in a disposable DWG.

1. Run `CE_PARKOPTIONS`.
2. Enter a target count and the project bay/aisle dimensions.
3. Confirm 90°, 60° and 45° alternatives appear with different capacity/pitch values.
4. Create each alternative in separate disposable drawings.
5. Confirm every generated bay is one closed polyline and is accepted by `CE_PKCOUNTX` and `CE_PKNUMBER2`.
6. Confirm no bay corner lies outside the boundary.
7. Grip-edit the boundary, run `CE_PARKOPTIONSREFRESH`, and confirm old linked bays are replaced rather than duplicated.
8. Run `CE_PARKOPTIONSINFO` by selecting both a source boundary and a generated bay.
9. Run `CE_PARKOPTIONSCLEAR` and confirm only linked bays are removed.
10. Test a boundary with arc segments and a concave boundary.

## 3. Low-slope and low-point diagnostics

Create feature lines, 2D/3D polylines and lines with known grades above and below 0.5%.

1. Run `CE_LOWSLOPE` with 0.5%.
2. Confirm only segments with absolute grade below 0.5% receive red review linework and labels.
3. Confirm negative and positive grades are evaluated by absolute magnitude.
4. Run `CE_LOWPOINTS` and verify local/global minima against known elevations.
5. Confirm source objects remain unchanged.
6. Run `CE_GRADINGREVIEWCLEAR` and confirm only CE review graphics are erased.

## 4. Background drawing management

Use a messy disposable background containing several layers, colours, blocks and a locked layer.

1. Run `CE_BACKGROUNDREVIEW` and compare reported layer/type/colour counts with the selection.
2. Run `CE_BACKGROUNDLIGHT` in Copy mode.
3. Confirm originals are unchanged and copies are on `CE-BG-*` layers with ByLayer colour.
4. Confirm generated copies remain selected and appear in Properties.
5. Repeat in Move mode and confirm no geometry is deleted.
6. Confirm locked-layer objects are skipped safely.

## 5. XREF splitting and revision backup

Use selected disposable geometry and a new output folder.

1. Run `CE_XREFSPLIT` with Keep originals.
2. Confirm the output DWG exists and the XREF is attached at the requested base point.
3. Open the XREF source and verify required layers/blocks/styles travelled with the WBLOCK output.
4. Repeat with Replace originals and confirm objects are erased only after successful attachment.
5. Run `CE_XREFINFO` and verify path/status.
6. Run `CE_XREFBACKUP`; confirm a timestamped copy appears under `Revisions` and the source hash remains unchanged.
7. Test relative and absolute XREF paths.

## 6. Linked setting-out schedules

Create COGO points and DBPoints over two known Civil 3D surfaces.

1. Run `CE_SETTINGOUTPOINTS` for Platform.
2. Select ground/design surfaces and confirm Description, X, Y, Ground, Design and Difference columns.
3. Verify `Difference = Design - Ground`.
4. Repeat schedule types Road Horizontal, Road Vertical and Junction.
5. Move a point or change a surface, then run `CE_SETTINGOUTREFRESH`.
6. Run `CE_REFRESHALL` and confirm the setting-out table also refreshes.
7. Delete one linked point and verify it is reported as missing without clearing valid rows.
8. Run `CE_SETTINGOUTEXPORT`; open the XLSX without an Excel repair warning and compare values.
9. Run `CE_SETTINGOUTINFO` and verify source/surface handles and row state.

## 7. Rational-method flow review

Use independently calculated test values.

1. Run `CE_RATIONALFLOW`.
2. Confirm all return periods 1:2, 1:5, 1:10, 1:20, 1:25, 1:50 and 1:100 are requested.
3. Verify each result against `Q = C i A / 360` for area in hectares and intensity in mm/h.
4. Export XLSX and compare every value.
5. Confirm the popup/table states that this is preliminary screening.

## 8. Culvert review

Test one circular and one box culvert with hand-calculated Manning full-flow capacities.

1. Verify area, wetted perimeter, hydraulic radius, capacity and velocity.
2. Verify the number-of-barrels calculation.
3. Confirm the screen clearly states that inlet/outlet control and flood-level analysis remain required.
4. Test invalid zero/negative input rejection and command cancellation.

## 9. Pump duty-point review

Use a known Hazen-Williams test case.

1. Verify friction head, total dynamic head and power calculations.
2. Test one pump rating that passes and one that fails.
3. Confirm the output requires manufacturer curve, NPSH, surge and complete system verification.

## 10. Quick catchment review

Create a closed boundary over a Civil 3D surface with a known low region.

1. Run `CE_CATCHMENTQUICK` with drawing units per metre set correctly.
2. Verify boundary area and perimeter independently.
3. Test coarse and fine sample spacing; confirm fine spacing can find a lower sampled minimum.
4. Confirm samples outside the surface are skipped safely.
5. Create the candidate low-point marker and clear it with `CE_HYDRAULICCLEAR`.
6. Confirm the output does not claim hydrological delineation or flood simulation.

## 11. Undo, save and reopen

For each geometry-producing workflow:

- test UNDO/REDO;
- save, close and reopen;
- verify XData/Xrecord links survive;
- refresh linked parking and setting-out outputs;
- verify clear commands do not remove source design objects.

## Failure evidence

For any failure record:

1. Civil 3D version and exact CE Tools commit;
2. command name;
3. complete command-line error text;
4. screenshot of the drawing and command line;
5. source object type and whether its layer was locked/reference-based;
6. whether the failure happened before preview, during confirmation or after transaction commit.
