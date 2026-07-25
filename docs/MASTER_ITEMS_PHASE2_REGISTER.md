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

## Next Phase 2 groups

- [ ] Standards-based road, parking, sidewalk and drainage quantity templates.
- [ ] Batch profile-view cleanup, fit, band-set and label-placement controls.
- [ ] Detailed section annotation/dimensioning for selected services.
- [ ] Civil 3D design-model report generator.
- [ ] Project-wide XREF discipline splitter and revision comparison dashboard.

## Engineering boundary

The parking grading guide is design-assistance geometry. Final issue requires review of:

- drainage paths and low points;
- tie-ins to roads, kerbs, channels, structures and entrances;
- maximum/minimum grades and accessibility requirements;
- surface triangulation and contour behaviour;
- cut/fill and layerworks quantities;
- stormwater capture and overflow routes.
