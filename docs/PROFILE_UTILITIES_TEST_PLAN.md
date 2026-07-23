# Profile Utilities Test Plan

Target host: Civil 3D 2023.

Commands:

- `CE_PRTOOLS`
- `CE_PRREPORT`
- `CE_PRELEV`
- `CE_PRLABEL`

Always test on a copy of a drawing.

## Build and ribbon

1. Build the Release configuration against Civil 3D 2023.
2. Install the user bundle and restart Civil 3D.
3. Confirm the **Profiles** panel appears on the **CE Tools** ribbon.
4. Confirm **Profile Tools** and **Station & Elevation** launch the expected commands.

## CE_PRREPORT

Test:

- existing-ground surface profiles;
- layout/design profiles;
- static profiles;
- offset profiles where available;
- data-reference profiles;
- profiles with and without PVIs;
- mixed selections containing non-profile objects;
- preselected profiles.

Verify profile name, type, parent alignment, equation-aware start/end station, length, style, update mode, PVI count and reference state against Civil 3D properties.

## CE_PRELEV

1. Select a profile and enter a valid station.
2. Test the start station, end station and intermediate stations.
3. Test tangent and vertical-curve locations.
4. Test a parent alignment containing station equations.
5. Enter stations below and above the profile range.

Verify elevation and instantaneous grade against Civil 3D inquiry tools. Confirm out-of-range stations fail safely and make no drawing changes.

## CE_PRLABEL

1. Select a profile and enter a valid station.
2. Place the MLeader on both sides of the alignment.
3. Test a station-equation alignment.
4. Test current drawing text sizes and MLeader styles.
5. Test invalid stations and cancelled label placement.
6. Undo once.

Verify:

- the arrow points to the parent alignment's plan location at the entered station;
- the label contains profile name, equation-aware station, elevation and grade;
- one undo removes the complete label;
- invalid or cancelled operations create no partial objects.

## Release gate

Do not mark the profile utilities production-ready until the Civil 3D 2023 build succeeds and all report, elevation, station-equation, plan-position, label and undo tests pass.
