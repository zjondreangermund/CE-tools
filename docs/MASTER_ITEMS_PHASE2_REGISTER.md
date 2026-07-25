# CE Tools Master Items Phase 2 register

Base checkpoint: Phase 1 branch `followup/master-items-phase-1`.

## Status meanings

- **Source implemented:** code and source validators exist; Autodesk compilation/runtime remains pending.
- **Foundation:** first-stage design assistance exists; final optimisation/design requirement is broader.
- **Outstanding:** no complete source implementation yet.

## Dynamic boundary parking

- [x] Source implemented: automatically detect modifications to boundaries referenced by `CE_PARK_OPTIONS` bays.
- [x] Source implemented: defer refresh until the active AutoCAD command ends.
- [x] Source implemented: recreate linked parking bays without accumulating duplicates.
- [x] Source implemented: manual refresh-all and monitor status commands.
- [x] Source implemented: report pending boundaries, last refresh and last failure.
- [ ] Runtime: verify grip-edit refresh for rectangular, irregular, concave and arc-segment boundaries in Civil 3D 2023/2024.
- [ ] Outstanding: full combinatorial parking optimiser with islands, circulation, accessible bays, entrances, obstacles and standards conflict resolution.

Commands:

- `CE_PARKAUTOMONITOR`
- `CE_PARKAUTOREFRESHALL`
- `CE_PARKAUTOSTATUS`

## Parking grading guides

- [x] Source implemented: fall from boundary vertices to a selected low point.
- [x] Source implemented: centre crown falling toward both edges.
- [x] Source implemented: both edges falling toward a centre valley.
- [x] Source implemented: configurable slope, default workflow value 2%.
- [x] Source implemented: configurable reference elevation and guide spacing.
- [x] Source implemented: linked 3D polyline guides with refresh, information and clear commands.
- [x] Source implemented: automatic guide refresh through the same source-boundary monitor.
- [ ] Runtime: verify guide elevations and slopes independently.
- [ ] Foundation boundary: guides do not create a finished Civil 3D grading surface, calculate earthworks or certify drainage performance.
- [ ] Outstanding: create/update Civil 3D feature lines and grading surfaces directly after runtime API validation.

Commands:

- `CE_PARKGRADETOOLS`
- `CE_PARKGRADECREATE`
- `CE_PARKGRADEREFRESH`
- `CE_PARKGRADEINFO`
- `CE_PARKGRADECLEAR`

## Standards-based quantity templates

- [x] Source implemented: linked parking/driveway layerworks schedule.
- [x] Source implemented: linked sidewalk layerworks schedule.
- [x] Source implemented: refresh from current source areas and lengths.
- [x] Source implemented: drawing tables and dependency-free Excel export.
- [x] Source implemented: separate quantities for paving, bedding, G5, G6, roadbed, kerbs/channels, markings, signs and allowances where applicable.
- [ ] Runtime: verify every quantity against hand calculations and confirm XLSX files open without repair warnings.
- [ ] Engineering boundary: office templates require project specification, thickness, waste, compaction and measurement-rule review before issue.

## Batch profile-view cleanup

- [x] Source implemented: select and process multiple Civil 3D profile views.
- [x] Source implemented: profile-view style and band-set assignment where supported by the installed Civil 3D API.
- [x] Source implemented: automatic station/elevation fitting and rebuild/update attempts.
- [x] Source implemented: batch report and optional overlap-cleanup handoff.
- [x] Source implemented: explicit unsupported-operation reporting rather than silent success.
- [ ] Runtime: validate Civil 3D 2023 and 2024 style, band-set and elevation-range API behaviour.

## Linked detailed-section annotations

- [x] Source implemented: road, parking, stormwater, sewer and water discipline selection.
- [x] Source implemented: overall width and height dimensions from selected section geometry.
- [x] Source implemented: circular-element diameter labels and discipline notes.
- [x] Source implemented: linked component register with object type, layer, measure and source handle.
- [x] Source implemented: source-handle persistence, information, refresh and clear workflows.
- [x] Source implemented: source section geometry remains unchanged.
- [ ] Runtime: verify AutoCAD dimension styles, table layout, missing-source handling and save/reopen persistence.
- [ ] Foundation boundary: generated drafting must be checked against engineer-approved typical details and project standards.

Commands:

- `CE_SECTIONDETAILTOOLS`
- `CE_SECTIONDETAILCREATE`
- `CE_SECTIONDETAILREFRESH`
- `CE_SECTIONDETAILINFO`
- `CE_SECTIONDETAILCLEAR`

## Next Phase 2 groups

- [ ] Comprehensive Civil 3D design-model report generator.
- [ ] Project-wide XREF discipline splitter and revision comparison dashboard.
- [ ] Finished Civil 3D grading surfaces/feature lines from parking grading guides after runtime API validation.
- [ ] Full parking optimiser with obstacles, islands, circulation and accessible-bay standards.

## Engineering boundary

All Phase 2 outputs remain design/drafting assistance. Final issue requires review of:

- drainage paths and low points;
- tie-ins to roads, kerbs, channels, structures and entrances;
- maximum/minimum grades and accessibility requirements;
- surface triangulation and contour behaviour;
- cut/fill and layerworks quantities;
- stormwater capture and overflow routes;
- every generated section dimension, component description and construction note.
