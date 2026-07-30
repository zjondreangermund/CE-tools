# CE Tools Master Items Phase 3 runtime test plan

Run only after the exact Phase 3 branch head compiles in Civil 3D 2023. Repeat in Civil 3D 2024 before merging.

## Safety and modelling limits

- Use disposable DWG copies and disposable TIN surfaces.
- Keep a backup of the installed CE Tools bundle.
- Confirm drawing units per metre before every analysis.
- Treat all routes, catchments, fill depths and hydrographs as preliminary screening.
- Do not issue legal flood lines, culvert designs, mitigation works or affected-property conclusions from these tools alone.

## 1. Ribbon and command registry

Confirm the Hydraulic & Catchment Review menu includes:

- `CE_HYDROLOGYTOOLS`
- `CE_SURFACEFLOW`
- `CE_CATCHMENTDELINEATE`
- `CE_HYDROGRAPHCOMPARE`
- `CE_HYDROLOGYCLEAR`

Confirm no duplicate-command registration error appears during startup.

## 2. Surface sampling and grid limit

Create a simple sloping TIN surface and rectangular closed boundary.

1. Run `CE_SURFACEFLOW` using a coarse grid.
2. Confirm reported rows, columns and active-cell count against independent calculations.
3. Confirm cells outside the boundary and outside the TIN are inactive.
4. Repeat with a concave boundary and a boundary containing arc segments.
5. Enter a spacing that would exceed 250,000 cells and confirm analysis stops before allocation.
6. Test drawing units per metre values of 1, 1000 and another project value; verify converted lengths and hectares.

## 3. Depression filling

Create a TIN containing one known enclosed depression.

1. Run `CE_SURFACEFLOW`.
2. Compare the reported filled-cell count and maximum fill depth with independent surface measurements.
3. Confirm the generated route exits the filled depression along the lowest spill path.
4. Repeat with two depressions and a flat saddle.
5. Verify the source TIN surface and closed boundary remain unchanged.

## 4. D8 route tracing

Use a monotonic surface with one known outlet.

1. Run using MaximumAccumulation.
2. Confirm the route terminates at the expected outlet and does not cycle.
3. Run using Pick and select several upstream points.
4. Compare generated route length and contributing area with the sampled grid.
5. Test a surface containing inactive holes and confirm the route does not cross unsampled cells.
6. Save, close and reopen; confirm generated review graphics remain removable.

## 5. Outlet catchment delineation

1. Pick a known outlet and run `CE_CATCHMENTDELINEATE`.
2. Compare catchment-cell count and area with an independently traced grid.
3. Confirm exposed-cell edges form the expected preliminary perimeter.
4. Confirm the longest review route reaches the selected outlet.
5. Repeat at coarse, medium and fine spacing; document changes in area and boundary shape.
6. Select an outlet near the analysis-boundary edge and one near a surface gap.
7. Confirm the model clearly identifies the result as grid screening.

## 6. Review graphics and clear

1. Generate two routes and two catchments.
2. Confirm all generated objects are on `CE-HYDROLOGY-REVIEW` and have `CE_HYDROLOGY_REVIEW` XData.
3. Run `CE_HYDROLOGYCLEAR`.
4. Confirm only CE-generated route, perimeter, marker and label graphics are erased.
5. Confirm TIN surfaces, boundaries and unrelated linework remain.
6. Test UNDO/REDO around create and clear.

## 7. Modified-rational hydrograph comparison

Use independently calculated values.

1. Run `CE_HYDROGRAPHCOMPARE` with area 10 ha, rainfall intensity 50 mm/h, pre coefficient 0.35 and post coefficient 0.75.
2. Verify rational peak values using `Q = C i A / 360`.
3. Verify pre/post time-to-peak, plateau and recession values for the entered times of concentration and storm duration.
4. Test a storm duration shorter than time of concentration and confirm the reduced effective peak.
5. Export Excel and compare every time-series value.
6. Confirm the workbook opens without an Excel repair warning.
7. Test invalid coefficients above 1.0 and zero/negative inputs.

## 8. Performance and memory

1. Record execution time and memory for approximately 10,000, 50,000, 100,000 and 250,000 cells.
2. Confirm cancellation/input validation occurs before oversized allocation.
3. Repeat analyses without closing Civil 3D and check for accumulating review objects or memory leaks.
4. Confirm a failed sample or route does not leave partial review graphics.

## Failure evidence

Record:

1. exact CE Tools commit and Civil 3D version;
2. command and complete command-line text;
3. surface type/name and boundary type;
4. grid spacing, units per metre, rows, columns and active cells;
5. screenshot of plan graphics and command line;
6. independently expected outlet/catchment/peak value;
7. whether failure occurred during sampling, priority flood, route/catchment extraction, confirmation, graphics transaction or Excel export.
