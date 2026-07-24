# CE Tools Next Item List — 24 July 2026

This list records the additional requirements supplied after the first live Civil 3D 2023 installation.

## Completed source work in the current follow-ups

- [x] Replace incompatible `RibbonRow`/ordinary flyout button usage with Civil 3D 2023/2024-compatible ribbon item handling.
- [x] Report the actual ribbon exception at the command line instead of silently leaving an empty tab.
- [x] Add Colour 250 scope: geometry only or geometry including annotation.
- [x] Apply available dimension, leader, text and attributed-block colour overrides.
- [x] Add Typical Details Phase 1: configure a master folder, search DWG/DXF/PDF assets, classify them and insert approved DWG details as traceable blocks.
- [x] Add Stormwater Network Production source: main/branch sequencing, network/polyline alignments, style settings, profile generation, profile views, source traceability and explicit refresh.

The stormwater implementation remains **draft and unvalidated in Autodesk Civil 3D** until the exact pull-request head compiles and passes the Civil 3D 2023/2024 manual test plan.

## Next implementation order

1. **Utility Network Production — Sewer**
   - extend existing sequencing to complete network/branch naming;
   - create or refresh alignments and sewer profile views;
   - apply labels/styles and reduce overlap;
   - keep source parts, alignments, profiles and drawing outputs traceable and explicitly refreshable.

2. **Utility Network Production — Water**
   - create alignment/profile workflows for water and pressure networks;
   - sequence and label mains and branches;
   - add controlled placement rules for isolating/gate valves, hydrants, air valves and scour valves.

3. **Surface Correction and Performance**
   - detect zero elevations, spikes, holes and extreme high/low points;
   - report likely buildings, trees, poles, signs, overhead lines and structure-invert contamination;
   - preview corrections before modification;
   - simplify surfaces using controlled performance targets.

4. **Dynamic Intersections**
   - create multiple intersections from feature lines and/or corridors;
   - keep intersection geometry relative to the selected design objects;
   - provide explicit refresh, information and detach workflows.

5. **Parking Skew Validation**
   - check perpendicular bay width rather than only skewed edge length;
   - compare against project standards such as a 2500 mm minimum;
   - display compliant dimensions in green and failures in red;
   - provide a correction workflow without changing valid geometry.

6. **Typical Details Phase 2 — Standards Review**
   - review title format, revision table, notes, legends, north arrow, fonts, dimensions, logo, sheet numbering, layers, lineweights, scales and symbols;
   - identify missing dimensions, notes, callouts and labels;
   - produce a consistency and improvement report.

7. **Typical Details Phase 3 — Dynamic Details**
   - parameter-driven detail variants such as trench width/depth, concrete strength, reinforcement and grating type;
   - linked refresh when parameters change;
   - quantity and BOQ linkage where geometry is measurable.

## Commercial reference

The supplied Autodesk AEC Collection and IDAS quotation is retained only as a commercial benchmark. It is not a technical requirement, licensing entitlement or current-price source for CE Tools.

All design automation must remain reviewable, reversible and subject to engineer/authority approval before issue.
