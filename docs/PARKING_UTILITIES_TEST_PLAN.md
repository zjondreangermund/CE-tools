# Parking Utilities Test Plan

Target host: Civil 3D 2023.

Commands:

- `CE_PKTOOLS`
- `CE_PKROW`
- `CE_PKDOUBLE`
- `CE_PKCOUNT`
- `CE_PKNUMBER`

Always test on a copy of a drawing.

## Build and load

1. Build the Release configuration against Civil 3D 2023.
2. Install the user bundle.
3. Restart Civil 3D.
4. Confirm the **Parking** panel appears on the **CE Tools** ribbon.
5. Confirm the Parking Tools, Single Row and Double Row buttons launch the correct commands.

## CE_PKROW

Test with:

- a horizontal AutoCAD Line;
- a vertical AutoCAD Line;
- a diagonal AutoCAD Line;
- a straight segment within a multi-segment 2D polyline;
- left and right side choices;
- 90-degree bays;
- angled bays such as 45, 60 and 75 degrees;
- a baseline exactly divisible by bay width;
- a baseline with an unused remainder;
- a baseline shorter than one bay;
- a source object on a locked layer;
- a sloping 3D line and an arc polyline segment.

Verify:

- bay count equals `floor(plan baseline length / bay width)`;
- divider spacing is measured along the selected baseline;
- bay depth matches the entered divider length;
- left and right are correct relative to baseline direction;
- angle is measured from the increasing baseline direction;
- the source line or polyline remains unchanged;
- new geometry uses the source layer;
- the rear boundary joins the first and last divider tips;
- pressing Enter or choosing No at preview creates nothing;
- one undo removes all geometry created by the command;
- locked, sloping and curved source geometry fails safely.

## CE_PKDOUBLE

Test with:

- 90-degree opposing rows;
- angled opposing rows;
- different aisle widths;
- horizontal, vertical and diagonal baselines;
- exact and non-exact bay-width divisions;
- a locked source layer;
- cancellation at preview.

Verify:

- both rows contain the same bay count;
- total bay count is twice the per-row count;
- the selected baseline acts as the aisle centreline;
- aisle-edge separation equals the entered aisle width;
- divider lines point away from the aisle on both sides;
- inner and rear boundaries are complete;
- one undo removes the complete double-row result;
- no geometry remains after cancellation or failure.

## CE_PKCOUNT

Test selections containing:

- one block-based parking bay;
- repeated blocks with the same name;
- several block names;
- closed bay polylines on one layer;
- closed bay polylines on several layers;
- mixed blocks, closed polylines and unsupported objects;
- a preselection set.

Verify:

- total count is correct;
- block totals are grouped by block definition name;
- closed-polyline totals are grouped by layer;
- unsupported objects are skipped;
- the command makes no drawing changes.

## CE_PKNUMBER

Test:

- block-based bays;
- closed-polyline bays;
- mixed selections;
- prefix `P`, starting number 1 and increment 1;
- a different prefix and starting number;
- increments greater than 1 and negative increments;
- zero increment rejection;
- different text heights;
- objects on locked layers;
- cancellation at preview;
- one-step undo.

Verify:

- one centred MText number is placed per accepted bay;
- labels use the selected bay object's layer;
- numbering follows the selection-set order;
- the prefix, start and increment are applied correctly;
- skipped objects do not consume a number;
- pressing Enter or choosing No creates no labels;
- failures do not leave partial numbering labels.

## Release gate

Do not mark Parking Tools production-ready until:

- Civil 3D 2023 compiles without errors;
- every command and ribbon test passes;
- left/right and angle direction are checked visually on horizontal, vertical and diagonal baselines;
- aisle width and double-row geometry are measured;
- counting and numbering are checked on the company's parking blocks;
- one-step undo, safe cancellation and locked-layer handling are confirmed;
- known limitations are documented.
