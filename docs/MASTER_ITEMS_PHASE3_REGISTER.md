# CE Tools Master Items Phase 3 register

Base checkpoint: exact green Phase 2 branch head.

## Status meanings

- **Source implemented:** code and validators exist; Autodesk compilation/runtime remains pending.
- **Foundation:** useful screening workflow exists, but full calibrated hydrology/hydraulics remains broader.
- **Outstanding:** no complete implementation yet.

## Tested grid-hydrology core

- [x] Priority-flood depression filling.
- [x] Deterministic D8 routing with flat-cell drainage-rank tie breaking.
- [x] Contributing-area accumulation.
- [x] Downstream route tracing with cycle detection.
- [x] Upstream outlet catchment extraction.
- [x] Modified-rational hydrograph generation.
- [x] Host-independent tests for enclosed pits, route termination, single-outlet accumulation, catchments and hydrograph peak flow.

## Civil 3D surface-flow review

- [x] Select a Civil 3D TIN surface and closed lightweight-polyline analysis boundary.
- [x] Sample a bounded regular grid with a maximum of 250,000 cells.
- [x] Enter drawing units per metre for area/length conversion.
- [x] Trace a selected or maximum-accumulation route.
- [x] Report fill cells, maximum fill depth, route length, outlet and contributing area.
- [x] Create removable 3D route, outlet and label graphics.
- [x] Keep the selected surface and boundary read-only.
- [ ] Runtime: verify TIN sampling, arc-boundary approximation, inactive surface gaps and large-grid performance in Civil 3D 2023/2024.

Commands: `CE_HYDROLOGYTOOLS`, `CE_SURFACEFLOW`, `CE_HYDROLOGYCLEAR`.

## Outlet catchment delineation

- [x] Snap a selected outlet point to the nearest active sampled cell.
- [x] Extract every upstream D8 cell contributing to the outlet.
- [x] Calculate grid catchment area in hectares.
- [x] Create exposed-grid-edge perimeter graphics, outlet marker and longest review route.
- [x] Clearly identify the result as preliminary grid catchment screening.
- [ ] Runtime: compare against independently delineated catchments at multiple grid spacings.
- [ ] Foundation boundary: cell-edge perimeter is not a surveyed or legally defined catchment boundary.

Command: `CE_CATCHMENTDELINEATE`.

## Pre/post-development hydrograph review

- [x] Prompt area, rainfall intensity, pre/post runoff coefficients, pre/post times of concentration, storm duration and time step.
- [x] Generate modified-rational pre/post hydrographs.
- [x] Report peak flow, increase and time-series values.
- [x] Create drawing-table output and optional dependency-free Excel export.
- [ ] Runtime: verify hydrograph interpolation, peak timing and workbook output against independent calculations.
- [ ] Foundation boundary: modified-rational screening is not a calibrated rainfall-runoff model.

Command: `CE_HYDROGRAPHCOMPARE`.

## Next Phase 3 groups

- [ ] Depression-storage and affected-area map with depth/volume table.
- [ ] Pre/post return-period hydrographs for 1:2 through 1:100.
- [ ] Flow-path network and culvert candidate-position screening.
- [ ] Specialist-model exchange package and result-import framework.
- [ ] 2D depth/velocity animation and affected-property reporting.
- [ ] Full pump/system curve assessment with manufacturer data.
- [ ] Road-drive simulation and design-error highlighting.

## Engineering boundary

Current Phase 3 tools are regular-grid screening workflows. They do not model:

- unsteady shallow-water equations;
- channel/structure inlet and outlet controls;
- pipe-network surcharge;
- calibrated loss, infiltration or rainfall distributions;
- floodplain roughness and obstruction effects;
- depth-velocity hazard;
- legal flood lines or certified mitigation designs.

Final issue requires calibrated inputs, sensitivity checks, engineer review and specialist software where appropriate.
