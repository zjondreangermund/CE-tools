# Surface Utilities Test Plan

Target host: Civil 3D 2023.

Commands:

- `CE_SFTOOLS`
- `CE_SFREPORT`
- `CE_SFELEV`
- `CE_SFLABEL`
- `CE_SFCOMPARE`

Always test on a copy of a drawing.

## Build and ribbon

1. Build Release against Civil 3D 2023.
2. Install the user bundle.
3. Restart Civil 3D.
4. Confirm the **Surfaces** panel appears on the **CE Tools** ribbon.
5. Confirm **Surface Tools**, **Surface Elevation** and **Compare Surfaces** launch the correct commands.

## CE_SFREPORT

Test selections containing:

- one TIN surface;
- several surfaces;
- a grid surface where available;
- a volume surface;
- a data-reference surface;
- an out-of-date surface;
- mixed surfaces and non-surface objects;
- a preselection set.

Verify:

- name, runtime type and style are correct;
- minimum, maximum and mean elevations match Surface Properties;
- point count and XY extents match Surface Properties;
- volume, reference, out-of-date and locked states are correct;
- unsupported objects are skipped without stopping the command;
- the command makes no drawing changes.

## CE_SFELEV

1. Select a surface.
2. Pick several known points inside the boundary.
3. Compare elevations with Civil 3D inquiry tools.
4. Pick outside the outer boundary.
5. Pick inside a surface hide boundary.
6. Test under a rotated UCS.

Verify:

- X and Y are WCS coordinates;
- elevation matches Civil 3D;
- boundary and hide-boundary failures are reported safely;
- rotated-UCS input is transformed correctly;
- the command creates no drawing objects.

## CE_SFLABEL

1. Select a surface and pick a valid point.
2. Place the MLeader in several directions.
3. Test different drawing text sizes and MLeader styles.
4. Test under a rotated UCS.
5. Pick outside the surface boundary.
6. Cancel during target or label placement.
7. Undo once.

Verify:

- label contains surface name, easting, northing and elevation;
- arrow points to the picked plan location;
- one undo removes the complete label;
- invalid or cancelled operations create no partial objects.

## CE_SFCOMPARE

1. Select an existing/base surface.
2. Select a proposed/comparison surface.
3. Pick known fill, cut and equal-elevation points.
4. Compare the reported difference with manual calculations.
5. Pick outside one surface only and outside both surfaces.
6. Select the same surface twice.
7. Test under a rotated UCS.

Verify:

- difference equals `Proposed - Existing`;
- positive difference is classified **Fill**;
- negative difference is classified **Cut**;
- near-zero difference is classified **Level**;
- surface names and both elevations are correct;
- invalid points fail safely;
- the command makes no drawing changes.

## Release gate

Do not mark the surface utilities production-ready until:

- Civil 3D 2023 compiles without errors;
- all command and ribbon tests pass;
- boundary behaviour and rotated UCS are verified;
- proposed-minus-existing cut/fill signs are confirmed;
- one-step undo and safe cancellation are confirmed for labels;
- known limitations are documented.
