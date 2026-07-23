# CE Tools Roadmap

## Vision

Build the civil engineering productivity platform: less clicking, more engineering.

CE Tools starts as a disciplined collection of small, high-value Civil 3D utilities and grows into a professional ribbon product, then into a complete civil engineering design platform.

## Product principles

1. Solve one real engineering problem at a time.
2. Keep workflows simple, predictable and undoable.
3. Preserve the user's drawing and data ownership.
4. Never copy software blindly; study ideas and build a better CE Tools implementation.
5. Test every Autodesk-dependent command in the supported Civil 3D versions before calling it production-ready.
6. Build reusable engines so one utility can later support roads, parking, feature lines, corridors, pipes, kerbs and sidewalks.
7. Keep the company's roots in Namibia while designing for Southern Africa and international users.

## Phase 1 — CE Tools Utilities

Small AutoLISP routines and lightweight Civil 3D add-ins that each solve one practical problem.

### Current alpha utilities

- `CE_BMVERT` — segment line-and-arc polylines by equal chainage.
- `CE_TLENGTH` — total selected curve lengths, including layer subtotals.
- `CE_TAREA` — total selected closed areas, including layer subtotals.
- `CE_COORDINATE` — picked-point labels, COGO point labels, coordinate crosses and setting-out tables.
- `CE_SEWSEQ` — select only the start and end structures, then trace and rename the connected gravity-network path as `Branch-1`, `MH1...` and `P1...`.

### Near-term utility families

- Feature Line Utilities
- Alignment Utilities
- Drawing Cleanup
- Survey Cleanup
- Background Preparation
- Viewport Tools
- Hatch Tools
- Layer Manager
- Excel Tools
- Coordinate and Label Utilities
- Parking Utilities

## Phase 2 — CE Tools Professional

Combine validated utilities into a polished Civil 3D ribbon with:

- consistent command names and icons;
- shared settings and company standards;
- licensing and update management;
- templates, reports and repeatable workflows;
- quality assurance, logging and recovery;
- professional installation and support.

## Phase 3 — CE Tools Platform

A complete civil engineering platform covering:

- Road Design
- Parking Design
- Water
- Sewer
- Stormwater
- Traffic Analysis
- Standards
- AI
- Twinmotion
- InfraWorks
- As-Builts
- Reports
- Drawing Production
- Quantity Calculations
- Project Management
- Digital Engineering

## Development workflow

For every utility:

1. Identify a real engineering problem.
2. Define the minimum-click workflow.
3. Choose the correct technology: AutoLISP, Civil 3D .NET add-in or both.
4. Implement the smallest reliable version.
5. Build and test in Civil 3D.
6. Correct failures and edge cases.
7. Document the command and test plan.
8. Release only after validation.

## Current release gate

Before adding more large commands, the present alpha must:

- compile cleanly against Civil 3D 2023;
- install without manual DLL copying;
- load the CE Tools ribbon;
- pass drawing tests for all five current commands;
- support one-step undo for every modifying command;
- fail safely without partial drawing changes;
- document known limitations.

## Shared segmentation engine direction

The `CE_BMVERT` geometry engine will eventually support multiple outputs:

1. Insert vertices.
2. Break polylines.
3. Create COGO points.
4. Create feature lines.
5. Label chainages.

The same engine will later be reused for roads, parking, kerbs, sidewalks, feature lines, corridors and pipe-related workflows.

## Mission

> CE Tools — Less Clicking. More Engineering.
