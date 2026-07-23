# User validation feedback — 24 July 2026

This document records the field feedback supplied after testing the early CE Tools build. It is a delivery backlog, not a claim that every listed function is complete.

## Already addressed in the latest source, but requiring a new combined build and live retest

- Entire gravity-network sewer sequencing with dotted branch names `MHn.x` and `Pn.x`.
- Branch ordering from the highest cover/rim structure, with the longest principal route first.
- Sewer branch alignments and visible `Branch-n` plan labels.
- Direction arrows for ordinary polylines.
- Polyline-vertex COGO points following polyline direction, with sequential descriptions and an XYZ table.
- Combined drawing cleanup using OVERKILL, AUDIT and PURGE.
- Linked stepped-offset feature lines with stored source/offset relationship and controlled refresh.
- Change selected objects to AutoCAD colour 250.
- Project standards selection and the searchable design-standards library.
- Compact CE Tools ribbon panels and module dropdowns.

The user's comment that sewer renaming worked for only one line relates to the earlier installed build. The latest source must be compiled and installed before the entire-network workflow can be retested.

## Current implementation batch

### Hatch and material display tools

- Transparent associative hatch creation.
- User-entered pattern, scale, angle, ACI colour and transparency.
- Batch hatch editing.
- Match hatch display settings.
- Send hatches behind profile/section grids, labels and other linework.
- Future civil presets for roads, gutters, kerbs, sidewalks, platforms, manholes, structures and pipes.

## Priority delivery sequence

### 1. Stabilisation and regression testing

- Compile all accumulated source against Civil 3D 2023.
- Resolve API differences before adding more Civil 3D-heavy modules.
- Test the compact ribbon visually at common widths.
- Test one-step undo, locked/reference objects, cancellation and no-partial-commit behaviour.
- Add performance timing and fatal-error logging for large drawings.

### 2. Project templates and coordinate intelligence

- Project Wizard using project name, client, town/country, units, template, standards and coordinate system.
- Town-to-coordinate-system assistance, including a verified Windhoek/LO-zone workflow.
- Hemisphere, quadrant, axis-order and sign checks before design or Excel export.
- Warnings when drawing orientation or coordinates are inconsistent with the selected project system.
- Do not rotate or transform geometry without an explicit preview and confirmation.

### 3. Background drawing cleanup and revision management

- Architectural/survey background audit.
- Layer classification and remapping.
- Duplicate/overlap cleanup.
- Text and dimension presentation cleanup.
- Performance audit for unnecessary objects, nested blocks, proxy objects and large hatches.
- Xref packaging and automatic revision backups.
- Viewport/detail relocation tools that preserve the displayed detail after model-space movement.

### 4. Naming and relationship engine

- Reliable road, pipe-network, feature-line and alignment naming rules.
- Propagate the approved design name to profiles, profile views, corridors, sample-line groups, section views, reports and quantity outputs.
- Never rename referenced objects without explicit confirmation.

### 5. Geometry repair and grading intelligence

- Alignment-from-object preflight and curve/PI validation repair.
- Detect invalid polyline endpoint/curve conditions before alignment creation.
- Highlight grading areas flatter than a user limit, initially 0.5 percent.
- Parking master-slope workflows: to low point, centre-to-edge and edge-to-centre.
- Dynamic grading relationships and controlled updates.

### 6. Dynamic sections, profiles and plan production

- Dynamic detailed sections along a picked polyline.
- Multiple section views with consistent labels, dimensions, notes and service/feature display.
- Batch profile-view cleanup, band-set changes and best-fit extents.
- Label-overlap handling and drag-state standards for points, alignments, profiles, assemblies, corridors, pipes and sections.
- Faster sheet creation and batch plotting for concept, preliminary, tender, construction and as-built submissions.

### 7. Engineering data tables, reports and BOQ

- Pipe-network schedules: pipes, structures, bends, angles, material/type, sizes, slopes and descriptions.
- Platform, road, horizontal-alignment, vertical-alignment and junction setting-out tables.
- Road cross-section data at 5 m, 10 m or 20 m intervals.
- Design-model report generation.
- Standards-based BOQ generation to Excel, split by work type and layerworks.
- Link schedules and quantities to the same model-data engine so drawing tables and BOQ values agree.

### 8. Automatic parking design

- Boundary-based parking generation.
- Required-bay target, for example 120 bays.
- Compare 90-degree, 60-degree and 45-degree alternatives.
- Dynamic boundary grip changes and controlled regeneration.
- Standards checks for bay size, aisle width, accessible bays and circulation.

### 9. Typical details and civil object library

- Searchable typical-details database.
- Project insertion with revision/source tracking.
- Civil and landscaping 2D/3D furniture library.
- Use only assets that CE Tools is licensed to distribute; Twinmotion or third-party assets cannot simply be copied into CE Tools without permission.

### 10. Analysis and simulation

- Quick low-point and culvert flow checks.
- Pump suitability checks for water, sewer and rising mains.
- Flood workflow covering 2D/3D display, return periods, affected areas, pre/post-development flow, cross sections and output tables.
- Road/corridor drive-through review and error highlighting.
- Surface, catchment and culvert data exchange with supported external applications.

### 11. External software integration

Target adapters include AutoCAD, Civil 3D, IDAS, InfraWorks, Twinmotion, HEC-RAS, Grading Optimization, Vehicle Tracking, Revit and Plex-Earth.

CE Tools may automate installed and licensed software through supported APIs, file formats or command interfaces. It cannot legally or technically provide another vendor's application, licensed content or runtime when that product is not installed or authorised. Every adapter must therefore:

- detect whether the product/version is installed;
- use an official or documented exchange route;
- fail safely when unavailable;
- preserve coordinate systems, units and object naming;
- report exactly what was imported/exported;
- avoid blocking the core CE Tools commands when an optional product is absent.

## Performance principles

- Load heavy modules only when their command is called.
- Keep event-driven updates controlled and debounced.
- Prefer explicit refresh for first releases of complex relationships.
- Cache read-only catalogue data, not live Civil 3D object references.
- Break large operations into preview, validation and commit stages.
- Write diagnostic logs for fatal or repeatable failures without storing confidential drawing contents.
- Use xrefs and project file separation where this improves performance and sharing.

## Production document vision

The final project output should support:

- A0/A1 construction drawings for site use.
- A3/A4 client drawing book with summaries and key design information.
- Project-specific summary slides.
- Design report.
- Quantity report and BOQ.
- Setting-out and network schedules.
- Revision and approval history.

All outputs must derive from the same named Civil 3D model objects to reduce conflicting information.
