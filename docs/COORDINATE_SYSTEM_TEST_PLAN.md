# Coordinate Systems Test Plan

Target host: Civil 3D 2023.

Commands:

- `CE_COORDSYS`
- `CE_COORDSYSINFO`
- `CE_COORDSYSASSIGN`
- `CE_COORDSYSSEARCH`
- `CE_COORDSYSCLEAR`

Always test on a copy of a drawing with known coordinates.

## Build and load

1. Build the Release configuration against Civil 3D 2023.
2. Install the user bundle.
3. Restart Civil 3D.
4. Confirm the Project category exposes Coordinate Systems after the grouped-ribbon conversion.
5. Confirm every direct command starts correctly from the command line.

## CE_COORDSYSINFO

Test drawings with:

- no selected coordinate-system zone;
- a metric projected coordinate system;
- a geographic coordinate system;
- a feet-based coordinate system;
- transformation settings disabled;
- transformation settings enabled.

Verify:

- the assigned code matches Drawing Settings > Units and Zone;
- description, category, projection, datum and unit are correct;
- drawing units, angular units and drawing scale are correct;
- transformation state is reported correctly;
- the command makes no drawing changes.

## CE_COORDSYSSEARCH

Search using:

- an exact known code;
- part of a code;
- country or region text;
- datum text;
- projection text;
- unit text;
- text that has no matches.

Verify:

- matching codes are returned;
- code, description, category, projection, datum and unit are readable;
- the result list is limited to 25 entries;
- the command asks for a refined search when more matches exist;
- malformed library entries are skipped safely;
- the command makes no drawing changes.

## CE_COORDSYSASSIGN

1. Record coordinates of several known drawing points.
2. Enter an invalid code.
3. Cancel before confirmation.
4. Press Enter at the Yes/No prompt.
5. Assign a valid code.
6. Save, close and reopen the DWG.
7. Rerun the command with the same code.

Verify:

- invalid codes make no changes;
- the preview shows current and proposed systems;
- the warning states that existing geometry is not transformed;
- Enter and No leave the original assignment unchanged;
- Yes assigns the selected code;
- point coordinates and geometry remain unchanged;
- the assigned code persists after save/reopen;
- assigning the already active code reports a no-op;
- a failed assignment attempts to restore the original code.

## CE_COORDSYSCLEAR

1. Start with a drawing containing an assigned coordinate system.
2. Cancel at confirmation.
3. Confirm clearing.
4. Save, close and reopen the drawing.
5. Run Clear again when no zone is selected.

Verify:

- the current system is previewed;
- Enter and No make no changes;
- Yes changes the selected zone to none;
- point coordinates and geometry remain unchanged;
- the cleared state persists after save/reopen;
- clearing an already unassigned drawing reports a no-op.

## Project metadata coordination

The first alpha stores the Project Setup `Coordinate System` field separately from the actual Civil 3D drawing setting. During validation, record any mismatch. A later project-wizard batch will synchronise or validate these two values explicitly.

## Release gate

Do not mark Coordinate Systems production-ready until:

- Civil 3D 2023 compiles without errors;
- all commands pass with the installed Civil 3D coordinate-system library;
- assignment and clearing persist after save/reopen;
- invalid codes and cancellation leave the original drawing setting unchanged;
- existing geometry is confirmed not to move or transform;
- metric and feet-based systems are tested;
- known limitations are documented.
