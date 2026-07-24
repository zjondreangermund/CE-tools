# CE Tools Review Comments — Batch 3 Validation Plan

## Scope

This batch implements shared annotation controls requested in the review received on 23 July 2026:

- selectable annotation heights of **1.8**, **2.0** and **5.0** drawing units;
- optional marker circles at annotation reference points;
- selectable **MLeader**, **MText** or **COGO point** output where COGO output is appropriate;
- consistent settings stored in the current DWG;
- shared annotation workflows for survey, alignment, profile, surface, feature line, corridor and parking tools.

## Build matrix

Compile and load using:

- Civil 3D 2023 / AutoCAD 2023 managed assemblies;
- Civil 3D 2024 / AutoCAD 2024 managed assemblies;
- .NET Framework 4.8, x64.

Run the GitHub source validations and core geometry tests before manual Civil 3D testing.

## Shared settings — `CE_ANNOTSETTINGS`

1. Start `CE_ANNOTSETTINGS` from **Drawings → Drawing Tools → Annotation Settings**.
2. Select **Small**, save, reopen and confirm the stored height is 1.8.
3. Repeat for **Standard** = 2.0 and **Large** = 5.0.
4. Test marker **Yes** and **No**.
5. Test output **MLeader**, **MText** and **COGO**.
6. Save the DWG, close and reopen it, then confirm the settings persisted.
7. Save As to a new DWG and confirm the settings travel with the drawing.
8. Confirm no 2500- or 5000-unit annotation text is created.
9. Confirm cancellation leaves the existing settings unchanged.

## Common annotation behaviour

Run each applicable command with all three text heights.

For MLeader output:

- verify one MLeader is created;
- verify the arrow/reference vertex is at the selected target;
- verify the text uses the selected height;
- verify the marker circle appears only when enabled.

For MText output:

- verify one MText object is created at the chosen text position;
- verify no leader line is created;
- verify the target marker circle appears only when enabled.

For COGO output:

- verify one Civil 3D COGO point is created at the correct XYZ position;
- verify its raw description contains the CE Tools annotation information;
- verify visible point labelling follows the current Civil 3D point label style;
- verify the optional marker circle is created separately from the COGO point;
- force a failure where possible and confirm partially created COGO points are removed.

## Alignment annotation — `CE_ALLABELX`

- Select an alignment and pick points on the left, right and directly on the alignment.
- Verify station equations display correctly.
- Verify signed offset is converted to an absolute value plus Left/Right/On alignment.
- Pick beyond the alignment range and confirm no annotation is created.
- Test MLeader, MText and COGO output.

## Profile annotation — `CE_PRLABELX`

- Select a profile and enter stations at the start, middle and end.
- Verify profile name, station, elevation and grade.
- Verify the annotation target follows the profile's parent alignment plan location.
- Enter a station outside the profile range and confirm cancellation.
- Test MLeader, MText and COGO output.

## Surface annotation — `CE_SFLABELX`

- Test a TIN surface at multiple internal points.
- Verify Easting, Northing and surface elevation.
- Pick outside the surface boundary and confirm no annotation is created.
- Test MLeader, MText and COGO output.

## Survey coordinate annotation

### `CE_COORDPICKX`

- Pick points in World and rotated UCS configurations.
- Verify the stored output uses WCS Easting, Northing and elevation.
- Test all output types and marker states.

### `CE_COORDCROSSX`

- Verify the cross is centred at the picked point.
- Verify cross size scales from the selected 1.8, 2.0 or 5.0 height.
- Verify the selected annotation output is created.
- Undo and verify the cross and annotation can be removed cleanly.

## Feature-line annotation — `CE_FLLABELX`

- Select an ordinary grading feature line.
- Verify name/handle fallback, 2D and 3D lengths, elevation range and grade range.
- Try a derived corridor or survey feature object and confirm it is rejected.
- Test MLeader, MText and COGO output.
- Confirm `CE_FLRAISE` remains available and still edits elevations.

## Corridor annotation — `CE_CORLABELX`

- Select a corridor and verify name, baseline count, region count, surface count and out-of-date state.
- Verify the command allows MLeader and MText only.
- When saved annotation output is COGO, confirm the corridor workflow safely falls back to MLeader.
- Confirm `CE_CORREBUILD` remains available and still performs the controlled rebuild workflow.

## Parking numbering — `CE_PKNUMBERX`

- Select block references and closed parking polylines.
- Verify sequential prefix, starting number and increment.
- Verify only 1.8, 2.0 or 5.0 text height is used through shared settings.
- Verify optional marker circles are centred on accepted bays.
- Include open polylines, unsupported entities and locked-layer objects and verify they are rejected.
- Confirm all accepted labels are committed in one transaction and one Undo removes the numbering batch.

## Ribbon checks

Confirm the following entries launch the new shared workflows:

- Drawings → Annotation Settings
- Survey → Picked Coordinate Annotation
- Survey → Coordinate Cross + Annotation
- Feature Line Tools → Feature Line Annotation
- Alignment Tools → Station-Offset Annotation
- Profile Tools → Profile Annotation
- Surface Tools → Surface Annotation
- Corridor Tools → Corridor Annotation
- Parking Tools → Number Bays

Confirm the following existing working commands remain present:

- Bellmouth Densifier
- Total Length
- Total Area
- Polyline Vertex COGO Points
- Corridor Rebuild
- Feature Line Raise / Lower

## Integration boundary

The new direct ribbon annotation buttons use the shared settings layer. Legacy parent keyword menus and legacy label command names remain available for compatibility until the new commands are compiled and manually validated in Civil 3D 2023 and 2024.
