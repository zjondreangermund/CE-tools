# CE Tools Next Item List — 24 July 2026

This list records the additional requirements supplied after the first live Civil 3D 2023 installation.

## Completed source work in the current follow-ups

- [x] Replace incompatible `RibbonRow`/ordinary flyout button usage with Civil 3D 2023/2024-compatible ribbon item handling.
- [x] Report the actual ribbon exception at the command line instead of silently leaving an empty tab.
- [x] Add Colour 250 scope: geometry only or geometry including annotation.
- [x] Apply available dimension, leader, text and attributed-block colour overrides.
- [x] Add Typical Details Phase 1: configure a master folder, search DWG/DXF/PDF assets, classify them and insert approved DWG details as traceable blocks.
- [x] Add Stormwater Network Production source: main/branch sequencing, network/polyline alignments, style settings, profile generation, profile views, source traceability and explicit refresh.
- [x] Add Sewer Network Production source: automatic and selected-main whole-network sequence, branch alignments, explicit source refresh, alignment formatting, EG profiles, profile views and network-part displays.
- [x] Add Water Network Production source: polyline/pressure-pipe route sequencing, linked alignments, EG profiles/profile views, explicit refresh, and controlled isolating-valve, hydrant, air-valve and scour-valve review markers.
- [x] Add Surface Correction and Performance source: zero/spike/low/extreme screening, hole/open-edge review, likely object-contamination screening, reversible corrected surfaces, reversible grid-simplified surfaces, generated-surface register and restore/removal workflow.
- [x] Add Dynamic Intersections source: feature-line/corridor/curve path extraction, plan intersection and elevation comparison, linked markers/register, explicit refresh/info/detach and deferred idle refresh with source preservation.
- [x] Add Parking Skew Validation source: minimum-area oriented bay measurement, true perpendicular width in millimetres, green pass/red fail dimensions, source-linked reports, failed-bay correction outlines and clear/information workflows while preserving existing parking commands.
- [x] Add Typical Details Phase 2 Standards Review source: read-only DWG/DXF inventory and heuristic standards checks, explicit manual PDF review boundary, stored traceable findings, settings, reports and library review.
- [x] Add Typical Details Phase 3 Dynamic Details source: trench drain, pipe trench, valve chamber, kerb and headwall variants; linked parameters and regeneration; source hash traceability; review status; preliminary quantity schedules; BOQ-ready Xrecord linkage; Excel export; information, detach and clear workflows.
- [x] Add ribbon icon performance source: per-session `ImageSource` cache, one generic cached command icon, unique top-level icons, `TextOnly/Cached/Full` modes and safe fallback. Cached is the default.

All follow-up implementations remain **draft and unvalidated in Autodesk Civil 3D** until their exact pull-request heads compile and pass the Civil 3D 2023/2024 manual test plans.

## Next implementation and validation order

1. **Exact-head stacked Autodesk validation**
   - compile PRs #28–#36 against Civil 3D 2023 and 2024 assemblies;
   - execute each supplied manual validation plan against the exact tested commit;
   - record host build, commit SHA, DLL SHA-256, tester, date and defects;
   - fix defects on the owning stacked branch and retest from that exact new head.

2. **Stack consolidation and release planning**
   - only after every required host test passes, agree a safe rebase/merge order;
   - preserve the confirmed Civil 3D 2023 ribbon implementation and command registry;
   - create one release candidate bundle and repeat smoke/regression testing before any production issue.

3. **Future Typical Details Phase 4 candidates**
   - office-approved template mapping and controlled parameter-schema catalogues;
   - engineer-approved reinforcement/bar schedule logic where reliable data exists;
   - controlled authority/standard rule packs;
   - expanded measurable detail families and project BOQ aggregation;
   - no automatic approval or source-template overwrite.

## Commercial reference

The supplied Autodesk AEC Collection and IDAS quotation is retained only as a commercial benchmark. It is not a technical requirement, licensing entitlement or current-price source for CE Tools.

All design automation must remain reviewable, reversible and subject to engineer/authority approval before issue.
