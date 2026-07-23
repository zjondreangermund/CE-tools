# CE Hatch Tools — AutoCAD/Civil 3D 2023 validation plan

## Release gate

Do not mark `CE_HATCHTOOLS` production-ready until creation, editing, matching and draw-order tests pass in Civil 3D 2023 with one undo operation and no partial result after cancellation or an exception.

## CE_HATCHCREATE

1. Create closed lightweight polylines representing a road surface, gutter, kerb, sidewalk and platform.
2. Select all boundaries and run `CE_HATCHCREATE`.
3. Test patterns `ANSI31`, `SOLID`, `AR-CONC`, `GRAVEL` and `EARTH` where available in the installed pattern library.
4. Test scales `0.1`, `1`, `10` and a project-scale value.
5. Test angles `0`, `45`, `90` and `-45` degrees.
6. Test ACI colours `1`, `8`, `30`, `250` and `255`.
7. Test transparency values `0`, `30`, `60` and `90` percent.
8. Confirm one associative hatch is created per supported boundary on layer `CE-HATCH`.
9. Confirm every hatch is sent behind its boundary, profile/section grid, labels and linework.
10. Change a boundary and confirm the associative hatch updates.
11. Confirm `TRANSPARENCYDISPLAY=1` shows transparency and plot transparency can be enabled in page setup.
12. Confirm one `UNDO` removes the complete batch.

## Boundary and safety cases

- Open polyline: skipped before creation.
- Circle, ellipse, closed spline and region: validate supported boundary behaviour.
- 2D polyline and 3D/non-planar polyline: confirm valid planar boundaries work and invalid boundaries abort without partial creation.
- Boundary on a locked layer: skipped.
- Mixed supported/unsupported selection: preview and result counts must be correct.
- Invalid pattern name: transaction must abort and no hatches may remain.
- Cancel every prompt and press Enter at confirmation: no changes.

## CE_HATCHEDIT

1. Select multiple hatches with different current settings.
2. Enter one pattern, scale, angle, ACI colour and transparency.
3. Confirm all editable selected hatches receive the chosen values.
4. Confirm locked-layer hatches are skipped.
5. Confirm one `UNDO` reverses the complete edit.
6. Confirm an invalid pattern name leaves every selected hatch unchanged.

## CE_HATCHMATCH

1. Create a source hatch with a distinctive pattern, scale, angle, colour and transparency.
2. Select multiple target hatches and run `CE_HATCHMATCH`.
3. Confirm all visual settings match the source.
4. Include the source in the target selection and confirm it is ignored safely.
5. Include non-hatch objects and locked-layer hatches; confirm they are skipped.
6. Confirm one `UNDO` reverses the complete match operation.

## CE_HATCHBACK

1. Place hatches above profile and section view linework.
2. Run `CE_HATCHBACK` on selected hatches.
3. Confirm hatches move behind linework while retaining their relative draw order.
4. Test model space and paper space separately.
5. Confirm locked-layer hatches are skipped and one `UNDO` restores the prior draw order.

## Ribbon

Confirm `CE Tools > Drawings > Hatch Tools` contains:

- Hatch Tools
- Create Transparent Hatches
- Edit Hatch Settings
- Match Hatch Settings
- Send Hatches Behind Linework

Confirm no duplicate buttons or old CE Tools panels remain after restarting Civil 3D.
