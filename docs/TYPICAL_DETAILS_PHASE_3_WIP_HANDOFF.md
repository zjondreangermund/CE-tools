# CE Tools Phase 3 WIP Handoff — 25 July 2026

## Saved state

Branch: `followup/typical-details-dynamic`

Base branch: `followup/typical-details-standards-review`

The current branch contains the initial `DynamicTypicalDetailCommands.cs` implementation. All source is committed to GitHub. Nothing in this branch is merged.

## Phase 3 goal

Complete Typical Details Phase 3 — Dynamic Details with parameter-driven detail variants, linked refresh, measurable quantity schedules, review status, and BOQ-ready output while preserving the approved source template.

## Work already started

- Added `src/CE.Tools.Civil3D/DynamicTypicalDetailCommands.cs`.
- Initial implementation covers the dynamic-detail command and data model foundation.
- Source template preservation and reviewable generated output are the intended design boundaries.

## Work still required

1. Inspect `DynamicTypicalDetailCommands.cs` for exact Civil 3D 2023/2024 compile compatibility.
2. Confirm and finalise all command names and eliminate any registry collisions.
3. Add the Phase 3 commands to the `Standards & Details` ribbon using `RibbonMenuItem`; do not reintroduce `RibbonRow` or ordinary `RibbonButton` objects inside flyout menus.
4. Update `scripts/Validate-CommandRegistry.py` with the final Phase 3 commands and minimum command count.
5. Add `scripts/Validate-DynamicTypicalDetails.py`.
6. Add the new validator to `.github/workflows/core-tests.yml`.
7. Add `docs/TYPICAL_DETAILS_PHASE_3_TEST_PLAN.md` for Civil 3D 2023 and 2024.
8. Update `docs/NEXT_ITEM_LIST_2026-07-24.md` to mark Phase 3 source work complete and record the next implementation/validation phase.
9. Run GitHub validators and core geometry tests.
10. Build the exact PR head locally against Autodesk Civil 3D 2023 and 2024 assemblies.
11. Test create, edit parameters, refresh, quantity schedule, BOQ linkage, detach/clear, Undo, save/reopen, and source-template preservation.
12. Keep the PR draft and unmerged until exact-head Autodesk compilation and runtime tests pass.

## Icon performance follow-up

Ribbon startup may feel slow because icons are generated and assigned for every menu and every flyout command while the ribbon is being built.

Recommended optimisation order:

1. Add a static cache in `RibbonVisuals` keyed by command/menu ID and size so each `ImageSource` is generated only once per session.
2. Prefer embedded pre-rendered PNG resources for common panel/menu icons instead of drawing every icon at startup.
3. Use large icons only for top-level flyout buttons; keep command items text-only or use one cached small generic icon.
4. Lazy-load optional command icons after the ribbon is visible, or disable flyout-item icons through a setting such as `CE_RIBBONICONS TextOnly/Cached/Full`.
5. Keep the existing try/catch fallback so icon failure can never leave the CE TOOLS tab blank.

The safest first fix is caching plus text-only flyout items. It should improve startup without changing command behaviour.

## Current stacked PR order

- PR #28 — ribbon compatibility, Colour 250 and Typical Details Phase 1
- PR #29 — Stormwater Network Production
- PR #30 — Sewer Network Production
- PR #31 — Water Network Production
- PR #32 — Surface Correction and Performance
- PR #33 — Dynamic Intersections
- PR #34 — Parking Skew Validation
- PR #35 — Typical Details Phase 2 — Standards Review
- Phase 3 WIP branch is stacked above PR #35

Do not merge or rebase the stack until the exact-head Civil 3D validation strategy is agreed.