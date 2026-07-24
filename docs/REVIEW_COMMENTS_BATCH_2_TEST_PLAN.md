# CE Tools Review Comments — Batch 2 Validation Plan

## Scope

This plan validates the shared report-presentation batch requested in the review received on 23 July 2026.

The batch adds pop-up grids and optional AutoCAD tables for:

- Alignments
- Profiles
- Surfaces
- Corridors
- Corridor baselines and regions
- Feature lines
- Parking bay groups

## Build matrix

Run the complete build for:

- Civil 3D 2023 / AutoCAD 2023 managed assemblies
- Civil 3D 2024 / AutoCAD 2024 managed assemblies
- .NET Framework 4.8, x64

The repository source-shape validation and core geometry tests must pass before manual Civil 3D testing.

## Shared pop-up and table checks

For every report command below:

1. Start the command from the CE Tools ribbon.
2. Select one valid object.
3. Confirm a modal pop-up opens and remains readable at 100%, 125% and 150% Windows scaling.
4. Confirm the first column remains visible while horizontally scrolling.
5. Resize the pop-up and confirm the grid resizes without clipping buttons.
6. Close the pop-up and confirm no drawing objects are created.
7. Run again, select **Place Table**, choose an insertion point and confirm one AutoCAD table is created.
8. Confirm the table title, column headings and data match the pop-up.
9. Confirm table text height is limited to the CE Tools range of 1.8 to 5.0 drawing units.
10. Undo once and confirm the table is removed.
11. Cancel at the insertion-point prompt and confirm no table is created.

## Alignment report — `CE_ALREPORTUI`

- Select one alignment and verify name, type, start/end station, length, site, style, profile count and reference state.
- Select multiple alignments and verify the total length in the pop-up note.
- Mix alignments with unsupported objects and verify unsupported objects are counted as skipped.
- Verify station equations are reflected in displayed station strings where present.

## Profile report — `CE_PRREPORTUI`

- Verify profile name, type, parent alignment, station range, length, style, update mode, PVI count and reference state.
- Test layout, surface and static profiles where available.
- Select multiple profiles and verify total length.

## Surface report — `CE_SFREPORTUI`

- Verify surface name, runtime type, style, point count, elevation range, mean elevation, XY extents and state flags.
- Test a TIN surface and a volume surface.
- Include an invalid or inaccessible surface and confirm it is skipped without aborting the full report.

## Corridor report — `CE_CORREPORTUI`

- Verify style, code-set style, baseline count, total regions, surface count, feature-code count, automatic rebuild, out-of-date, reference and region-lock values.
- Verify the maximum triangle side length.
- Test one up-to-date and one out-of-date corridor.
- Confirm `CE_CORREBUILD` still performs the existing controlled rebuild workflow and was not replaced by the report command.

## Corridor baseline and region report — `CE_CORBASEUI`

- Verify one row is created for every region.
- Verify corridor and baseline names, type, alignment, profile, feature line, station range, region range, assembly and processing state.
- Verify a baseline without regions still appears with `<None>` in the region column.

## Feature-line report — `CE_FLREPORTUI`

- Verify name, layer, 2D/3D length, elevation range, grade range, point counts and reference state.
- Mix ordinary feature lines with unsupported derived objects and verify only ordinary grading feature lines are reported.
- Confirm `CE_FLRAISE` still changes elevations through the existing transaction-controlled command.

## Parking report — `CE_PKREPORTUI`

- Select parking bay block references and confirm grouping by effective block definition.
- Select closed bay polylines and confirm grouping by layer.
- Mix open polylines and other objects and verify they are skipped.
- Confirm the pop-up and placed table show the same group totals.

## Ribbon checks

Confirm the following ribbon entries launch the new pop-up/table commands:

- Feature Line Tools → Report
- Alignment Tools → Alignment Report
- Profile Tools → Profile Report
- Surface Tools → Surface Report
- Corridor Tools → Corridor Report
- Corridor Tools → Baselines and Regions
- Parking Tools → Parking Report

Confirm the working Bellmouth Densifier, Total Length, Total Area, Corridor Rebuild and Feature Line Raise / Lower commands remain present.

## Known integration boundary

The direct report buttons use the new pop-up/table commands in this batch. The legacy command-line report commands remain available for backward compatibility. A later integration pass may route the parent `CE_ALTOOLS`, `CE_PRTOOLS`, `CE_SFTOOLS`, `CE_CORTOOLS` and `CE_FLTOOLS` keyword menus to the same presenter after Civil 3D validation confirms the new report layer is stable.
