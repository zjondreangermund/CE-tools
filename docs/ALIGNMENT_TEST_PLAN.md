# Alignment Utilities Test Plan

Target host: Civil 3D 2023.

Commands:

- `CE_ALTOOLS`
- `CE_ALREPORT`
- `CE_ALSTOFF`
- `CE_ALLABEL`

Always test on a copy of a drawing.

## Build and load

1. Build the Release configuration against Civil 3D 2023.
2. Install the user bundle.
3. Restart Civil 3D.
4. Confirm the **Alignments** panel appears on the **CE Tools** ribbon.
5. Confirm **Alignment Tools** and **Station & Offset** buttons launch the correct commands.

## CE_ALREPORT

Test selections containing:

- one centerline alignment;
- several alignments;
- an offset alignment;
- a siteless alignment;
- a site alignment;
- a data-reference alignment;
- mixed alignments and non-alignment objects;
- a preselection set.

Verify:

- alignment name and type are correct;
- start and end stations respect station equations;
- length matches Alignment Properties;
- site, style, profile count and reference state are correct;
- total length equals the sum of counted alignments;
- unsupported objects are skipped without stopping the command;
- the command makes no drawing changes.

## CE_ALSTOFF

1. Select an alignment.
2. Pick a point on the alignment.
3. Pick points to the right and left while facing increasing station.
4. Pick a point at a known station and offset.
5. Test an alignment containing station equations.
6. Pick beyond the start and beyond the end.
7. Test under a rotated UCS.

Verify:

- equation-aware station matches Civil 3D inquiry tools;
- raw station is reported separately;
- positive offset is reported as Right;
- negative offset is reported as Left;
- points on the alignment report zero offset;
- out-of-range picks fail safely;
- UCS input is transformed correctly to WCS.

## CE_ALLABEL

1. Select an alignment.
2. Pick a valid station/offset point.
3. Place the label on both sides of the picked point.
4. Test current text sizes and different MLeader styles.
5. Test a station-equation alignment.
6. Test an out-of-range point.
7. Undo once.

Verify:

- the MLeader contains alignment name, station and offset side;
- station text respects station equations;
- offset magnitude and left/right side are correct;
- the arrow points to the picked location;
- one undo removes the complete label;
- an out-of-range or cancelled operation creates nothing;
- failures do not leave partial database objects.

## Release gate

Do not mark the alignment utilities production-ready until:

- Civil 3D 2023 compiles without errors;
- all command and ribbon tests pass;
- station equations and left/right signs are verified against Civil 3D;
- one-step undo and safe cancellation are confirmed;
- known limitations are documented.
