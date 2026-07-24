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

The stormwater, sewer, water, surface and dynamic-intersection implementations remain **draft and unvalidated in Autodesk Civil 3D** until their exact pull-request heads compile and pass the Civil 3D 2023/2024 manual test plans.

## Next implementation order

1. **Parking Skew Validation**
   - check perpendicular bay width rather than only skewed edge length;
   - compare against project standards such as a 2500 mm minimum;
   - display compliant dimensions in green and failures in red;
   - provide a correction workflow without changing valid geometry;
   - preserve existing parking count/number/report workflows.

2. **Typical Details Phase 2 — Standards Review**
   - review title format, revision table, notes, legends, north arrow, fonts, dimensions, logo, sheet numbering, layers, lineweights, scales and symbols;
   - identify missing dimensions, notes, callouts and labels;
   - produce a consistency and improvement report.

3. **Typical Details Phase 3 — Dynamic Details**
   - parameter-driven detail variants such as trench width/depth, concrete strength, reinforcement and grating type;
   - linked refresh when parameters change;
   - quantity and BOQ linkage where geometry is measurable.

## Commercial reference

The supplied Autodesk AEC Collection and IDAS quotation is retained only as a commercial benchmark. It is not a technical requirement, licensing entitlement or current-price source for CE Tools.

All design automation must remain reviewable, reversible and subject to engineer/authority approval before issue.
