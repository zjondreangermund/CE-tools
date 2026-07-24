# Review Comments Batch 6 — BOQ and Excel Validation Plan

## Scope

This plan validates the Batch 6 linked bill-of-quantities framework and Excel
exports introduced for issue #17.

Commands under test:

- `CE_BOQTOOLS`
- `CE_BOQBUILD`
- `CE_BOQREFRESH`
- `CE_BOQINFO`
- `CE_BOQEXPORT`
- `CE_BOQROAD`
- `CE_BOQPLATFORM`
- `CE_BOQSTORM`
- `CE_BOQSEWER`
- `CE_BOQWATER`
- `CE_BOQBULKWATER`

Preserved regression commands:

- `CE_TLENGTH`
- `CE_TAREA`
- `CE_BMVERT`

## Release boundary

GitHub Actions cannot load Autodesk AutoCAD or Civil 3D managed assemblies.
This branch must remain a draft until it compiles and passes the checks below
in both Civil 3D 2023 and Civil 3D 2024.

The first Batch 6 release provides explicit linked refresh through
`CE_BOQREFRESH`. It does not claim event-driven automatic recalculation.

## Build matrix

Run the repository build script for:

1. Civil 3D 2023, x64, Release.
2. Civil 3D 2024, x64, Release.

Confirm:

- `BillOfQuantitiesCommands.cs` compiles against each Autodesk API version.
- `System.IO.Compression` and `System.IO.Compression.FileSystem` resolve from
  .NET Framework 4.8.
- No Excel installation or Microsoft Office Interop reference is required.
- The application bundle contains the new command assembly.
- Loading the bundle produces no duplicate command or ribbon errors.

## Test drawing preparation

Create a clean metric test drawing containing:

- Open lines and polylines for kerbs, channels, V-drains and road markings.
- Closed polylines and hatches for surfacing, sidewalks, parking, driveways,
  layerworks and platform areas.
- Blocks for road signs, manholes, catchpits, valves, hydrants, meters and
  pump items.
- At least one Civil 3D gravity pipe and structure.
- At least one water or pressure-network object when available.
- A 3D solid with measurable volume.
- Unsupported annotation objects for rejection testing.

Place objects on clearly named layers, including examples such as:

- `RD-SURFACING`
- `RD-BASECOURSE`
- `RD-KERB-CHANNEL`
- `RD-V-DRAIN`
- `RD-MARKING`
- `RD-SIGN`
- `PK-DRIVEWAY-LAYERWORKS`
- `SW-PIPE-450`
- `SW-MANHOLE`
- `SEWER-PIPE-160`
- `WATER-MAIN-250`
- `BULK-WATER-PUMP`

## Linked BOQ creation

Run `CE_BOQBUILD` for each discipline option:

- General
- Road
- Platform
- Stormwater
- Sewer
- Water
- BulkWater

For every run:

1. Select a mixture of valid and unsupported objects.
2. Enter the correct drawing-units-per-metre value.
3. Review the command-line preview.
4. Confirm the mutation.
5. Place the BOQ table.

Verify:

- No table is created when zero usable quantities are found.
- Rejected objects are reported with reasons.
- The table contains ten columns: Item, Discipline, Section, Description,
  Unit, Quantity, Rate, Amount, Source Count and Source/Size.
- Lengths are reported in metres.
- Areas are reported in square metres.
- volumes are reported in cubic metres.
- blocks and scheduled structures are reported as counts.
- quantities respect the drawing-units-per-metre conversion.
- pipe diameters or other available size properties appear in Source/Size.
- the table stores an extension-dictionary Xrecord named `CE_BOQ_LINKS`.
- one AutoCAD Undo removes the created table.

## Road classification checks

Select representative road objects and verify classification for:

- Surfacing.
- Basecourse, subbase and subgrade layerworks.
- Kerbs.
- Kerbs and channels.
- V-drains.
- Road markings.
- Road signs.
- Parking and driveway surfacing.
- Parking and driveway layerworks.
- Sidewalk surfacing.
- Sidewalk layerworks.

Check both correctly named layers and deliberately ambiguous layers. Record
classification mistakes for later standards-library mapping rather than
silently accepting them as final design-standard quantities.

## Platform and earthworks checks

Test closed platform boundaries, grading areas and 3D solids.

Verify:

- Closed platform geometry produces area quantities.
- Layerwork keywords produce layerwork sections.
- cut/fill or earthwork solids produce volume when the Autodesk object exposes
  a usable volume property.
- geometry without a measurable volume safely falls back to a supported area or
  length quantity, or is rejected.

## Pipe-network checks

Test stormwater, sewer, water and bulk-water selections.

Verify:

- Civil pipe length is read from an available runtime length property.
- structures and scheduled fittings are counted.
- available diameter properties are shown.
- unsupported Civil 3D object types are rejected without aborting the complete
  selection.
- reference or proxy objects do not corrupt the table.

## Rate preservation and explicit refresh

1. Create a linked BOQ.
2. Enter rates manually into several Rate cells.
3. Change source geometry lengths and areas.
4. Move a source object to another classification layer.
5. Erase one linked source object.
6. Run `CE_BOQREFRESH`.

Verify:

- a preview appears before replacement.
- cancelling leaves the existing table unchanged.
- confirmed refresh updates current quantities.
- matching items retain their entered rates.
- Amount is recalculated as Quantity multiplied by Rate.
- stale handles are reported and removed from the refreshed link record.
- newly regrouped items receive a blank rate when their item key changed.
- the existing table is not cleared when no linked source produces a usable
  quantity.
- one Undo restores the previous table state after a confirmed refresh.

## Link information

Run `CE_BOQINFO` and verify the pop-up reports:

- schema version.
- discipline.
- drawing units per metre.
- stored source handles.
- resolvable and stale source counts.
- current BOQ row count.
- explicit refresh model.

Decline optional information-table placement and confirm no drawing mutation.
Then repeat and place the optional information table.

## Linked Excel export

1. Create and edit a linked BOQ.
2. Run `CE_BOQEXPORT`.
3. Choose Yes for refresh.
4. Save to a new `.xlsx` file.
5. Open the workbook in Microsoft Excel.

Verify:

- Excel opens the workbook without repair warnings.
- the worksheet name matches the discipline.
- the first two rows are frozen.
- the second row has filtering enabled.
- all BOQ columns and displayed rates are exported.
- quantity, rate, amount and source-count cells are numeric where populated.
- Unicode unit symbols `m²` and `m³` display correctly.
- export works on a workstation without Excel running.
- an existing workbook path is replaced only after the standard save-dialog
  confirmation behaviour.

Repeat with No for refresh and confirm the currently displayed table is
exported unchanged.

## Direct discipline Excel exports

Run each command with discipline-specific geometry:

- `CE_BOQROAD`
- `CE_BOQPLATFORM`
- `CE_BOQSTORM`
- `CE_BOQSEWER`
- `CE_BOQWATER`
- `CE_BOQBULKWATER`

Verify each command:

- respects implied selection and normal selection.
- requests drawing units per metre.
- reports rejected objects.
- creates a valid `.xlsx` workbook without placing a drawing table.
- uses the expected discipline and sections.

## Unit conversion tests

Repeat a known 1000-drawing-unit line with:

- 1 drawing unit per metre: expected 1000 m.
- 1000 drawing units per metre: expected 1 m.

Repeat equivalent known areas and volumes and verify squared and cubed
conversion factors.

Reject zero, negative, NaN or infinite conversion values.

## Regression tests

Confirm the following remain unchanged and callable from the ribbon and command
line:

- `CE_TLENGTH` still totals selected curve lengths by layer.
- `CE_TAREA` still totals supported closed areas by layer.
- `CE_BMVERT` still performs bellmouth densification.
- Batch 3 annotation settings remain available.
- Batch 4 corridor and feature-line workflows remain available.
- Batch 5 linked coordinate and polyline-point workflows remain available.

## Acceptance record

Record for each Civil 3D version:

- build result and assembly path.
- command load result.
- linked-table creation result.
- refresh and Undo result.
- Excel open/repair result.
- pipe-network quantity result.
- known unit-conversion result.
- regressions passed or failed.
- tester name, DWG name and date.
