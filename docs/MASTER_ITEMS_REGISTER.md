# CE Tools master item register

Source: `CE Tools - Items.docx` supplied by the project owner.

## Status meanings

- **Implemented:** source exists and is covered by source validation; Autodesk build/runtime may still be pending.
- **Foundation:** useful first-stage workflow exists, but the complete long-term requirement remains broader.
- **Outstanding:** no complete implementation yet.
- **External dependency:** requires an installed third-party product, licensed SDK/API or approved exchange format.

## Existing implemented/foundation areas

- Entire-network sewer sequencing, branch naming and service production.
- Polyline direction arrows and linked polyline-vertex COGO points/tables.
- Cleanup, hatch, colour, annotation and project-style workflows.
- Feature-line, alignment, profile, surface, corridor and parking reporting.
- Stormwater, sewer and water alignment/profile production.
- Dynamic cross sections/intersections and typical-detail framework.
- Linked BOQs, Excel exports, sewer excavation schedules and client/drawing books.

## Phase 1 — native Civil 3D productivity

Source implementation is recorded below. Every checked item still requires exact-head Civil 3D 2023 and 2024 compilation/runtime validation.

### Parking planning

- [x] Analyse a selected closed boundary — `CE_PARKOPTIONS`.
- [x] Present 90°, 60° and 45° parking alternatives.
- [x] Show capacity and target-bay compliance.
- [x] Create each accepted bay as a closed polyline.
- [x] Store a source-boundary link and refresh after boundary grip edits — `CE_PARKOPTIONSREFRESH`.
- [ ] Add slope-master alternatives and dynamic grip-driven parking optimisation.

Additional commands: `CE_PARKOPTIONSINFO`, `CE_PARKOPTIONSCLEAR`.

### Grading and drainage diagnostics

- [x] Highlight selected segments below the configured minimum grade, default 0.5% — `CE_LOWSLOPE`.
- [x] Identify candidate local/global low points — `CE_LOWPOINTS`.
- [ ] Derive full drainage directions and catchment flow paths across a surface.
- [x] Create a quick rational-method flow calculator and preliminary culvert review — `CE_RATIONALFLOW`, `CE_CULVERTREVIEW`.
- [x] Add a surface/catchment analysis foundation for later full flood simulation — `CE_CATCHMENTQUICK`.
- [x] Add a preliminary pump duty-point screen — `CE_PUMPREVIEW`.

Review graphics remain separate from source design geometry and can be removed with `CE_GRADINGREVIEWCLEAR` or `CE_HYDRAULICCLEAR`.

### Background and XREF management

- [x] Audit architectural/survey backgrounds — `CE_BACKGROUNDREVIEW`.
- [x] Classify selected layer, object-type, colour and locked-layer concentration.
- [x] Create controlled light-background copies or move selected objects while keeping the result selected — `CE_BACKGROUNDLIGHT`.
- [x] Export selected discipline groups to separate DWGs and attach them as XREFs — `CE_XREFSPLIT`.
- [x] Report attached XREF paths and states — `CE_XREFINFO`.
- [x] Create timestamped revision backups — `CE_XREFBACKUP`.
- [ ] Add a project-wide XREF discipline splitter and revision comparison/rollback dashboard.

### Setting-out schedules

- [x] Platform point schedule: description, X, Y, ground, design and difference — `CE_SETTINGOUTPOINTS`.
- [x] Road horizontal/vertical/junction point schedules through the schedule-type selector.
- [x] Linked refresh after COGO point or selected surface changes — `CE_SETTINGOUTREFRESH` and `CE_REFRESHALL`.
- [x] Excel export — `CE_SETTINGOUTEXPORT`.
- [ ] Cross-section schedules at configurable 5 m/10 m/20 m intervals.
- [ ] Network asset schedules linked directly to BOQ data.

### Phase 1 hydraulic boundaries

The Phase 1 hydraulic commands are preliminary engineering-review tools, not final certified hydraulic models:

- Rational Method scenarios use user-entered, project-specific rainfall intensities for return periods 1:2, 1:5, 1:10, 1:20, 1:25, 1:50 and 1:100.
- Culvert capacity is a full-flow Manning screen and does not replace inlet/outlet-control analysis.
- Pump screening uses a simplified Hazen-Williams duty point and does not replace manufacturer curve, NPSH, surge or system-curve assessment.
- Quick catchment review samples a selected surface on a grid; it is not automatic hydrological delineation or a flood simulation.

## Phase 2 — design automation

- [ ] Dynamic parking grip-fitting and multiple layout optimisation.
- [ ] Road/platform grading optimisation and master feature-line creation.
- [ ] Full standards-based road, parking, sidewalk and drainage quantity templates.
- [ ] Automated profile-view cleanup, best fit, band-set batching and label placement.
- [ ] Automated detailed section annotation/dimensioning for all services.
- [ ] Full Civil 3D model design report generator.

## Phase 3 — hydraulic and simulation systems

- [ ] 2D/3D flood simulation and animated flow visualisation.
- [ ] Pre/post-development hydrographs and calibrated return-period modelling.
- [ ] Affected-area maps, flood tables and mitigation guidance.
- [ ] Automatic catchment delineation, flow routing and culvert positioning/sizing.
- [ ] Full water/sewer pump selection with manufacturer curves and rising-main hydraulics.
- [ ] Road-drive simulation and design-error highlighting.

Phase 1 foundations completed for rational flow, culvert capacity, pump duty-point and sampled catchment/low-point review. These do not close the Phase 3 requirements.

## Phase 4 — external-product integration

- [ ] InfraWorks import/export and model synchronisation.
- [ ] Twinmotion export, materials, furniture and performance workflow.
- [ ] HEC-RAS exchange and result import.
- [ ] Revit, IDAS, Vehicle Tracking, Grading Optimization and Plex-Earth exchange.
- [ ] Product capability detection and clear installed-host requirements.

Direct access to another vendor's software cannot be embedded without its installed product and supported API. CE Tools will use official APIs or open exchange formats and will not silently download or bypass software requirements.

## Phase 5 — libraries and project delivery

- [ ] Complete engineer-approved Typical Details database.
- [ ] Civil/landscape 2D and 3D object library.
- [ ] Design-standards reference library and office templates.
- [ ] Automatic A0/A1 site drawings plus A3/A4 client book.
- [ ] Project-specific summary slides.
- [ ] Revision snapshots, rollback and project closeout packages.

Existing drawing/client-book and revision foundations remain available, but the complete library and automatic project-specific slide workflow are still outstanding.

## Development boundary

This register tracks source implementation separately from:

1. Civil 3D 2023 compilation/runtime testing;
2. Civil 3D 2024 compilation/runtime testing;
3. engineering review of calculations and typical details;
4. third-party API and licensing validation.
