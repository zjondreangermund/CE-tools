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

### Parking planning

- [ ] Analyse a selected closed boundary.
- [ ] Present 90°, 60° and 45° parking alternatives.
- [ ] Show capacity and target-bay compliance.
- [ ] Create each accepted bay as a closed polyline.
- [ ] Store a source-boundary link and refresh after boundary grip edits.
- [ ] Add slope-master alternatives and low-slope highlighting.

### Grading and drainage diagnostics

- [ ] Highlight selected segments/areas below the configured minimum grade, default 0.5%.
- [ ] Identify candidate low points and drainage directions.
- [ ] Create a quick rational-method flow calculator and culvert review schedule.
- [ ] Add a surface/catchment analysis foundation for later full flood simulation.

### Background and XREF management

- [ ] Audit architectural/survey backgrounds.
- [ ] Classify layers and presentation problems.
- [ ] Create controlled light-background copies without losing selection/property behaviour.
- [ ] Export selected discipline groups to separate DWGs and attach them as XREFs.
- [ ] Record revision backups and refresh links.

### Setting-out schedules

- [ ] Platform point schedule: description, X, Y, ground, design and difference.
- [ ] Road horizontal/vertical/junction schedules.
- [ ] Cross-section schedules at configurable 5 m/10 m/20 m intervals.
- [ ] Network asset schedules linked to BOQ data.

## Phase 2 — design automation

- [ ] Dynamic parking grip-fitting and multiple layout optimisation.
- [ ] Road/platform grading optimisation and master feature-line creation.
- [ ] Full standards-based road, parking, sidewalk and drainage quantity templates.
- [ ] Automated profile-view cleanup, best fit, band-set batching and label placement.
- [ ] Automated detailed section annotation/dimensioning for all services.
- [ ] Full Civil 3D model design report generator.

## Phase 3 — hydraulic and simulation systems

- [ ] 2D/3D flood simulation and animated flow visualisation.
- [ ] Pre/post-development hydrographs and return periods 1:2 through 1:100.
- [ ] Affected-area maps, flood tables and mitigation guidance.
- [ ] Automatic catchment extraction, low-point flow and culvert sizing.
- [ ] Water/sewer pump suitability and rising-main calculations.
- [ ] Road-drive simulation and design-error highlighting.

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

## Development boundary

This register tracks source implementation separately from:

1. Civil 3D 2023 compilation/runtime testing;
2. Civil 3D 2024 compilation/runtime testing;
3. engineering review of calculations and typical details;
4. third-party API and licensing validation.
