# Flood Analysis Phase 1 — Civil 3D Validation Plan

## Scope

This additive phase introduces `CE_FLOODQUICK`, a preliminary catchment screening workflow. It does not alter existing CE Tools commands.

The command:

- reads the area of a selected closed planar catchment boundary;
- calculates rational-method peak flows for 2, 5, 10, 20, 25, 50 and 100-year return periods;
- compares pre- and post-development runoff;
- recommends the first standard circular culvert diameter whose Manning full-flow capacity exceeds the 100-year post-development flow;
- places a calculation table in model space.

## Required checks

1. Build for Civil 3D 2023 and 2024.
2. Run `python scripts/Validate-FloodAnalysis.py`.
3. Test metre drawings using 10,000 square drawing units per hectare.
4. Test a non-metre drawing with the correct conversion entered.
5. Confirm open and non-planar boundaries are rejected.
6. Confirm coefficients greater than 1.0 are rejected.
7. Confirm every standard return period requires a positive rainfall intensity.
8. Independently verify one rational-method result using Q = C i A / 360.
9. Independently verify one Manning full-flow capacity.
10. Cancel before table placement and confirm no objects are created.
11. Place a table, undo once and confirm it is removed.
12. Save, reopen and confirm the table persists.

## Engineering limitations

This is a screening tool, not final hydraulic design. Rainfall intensity must come from an approved IDF source and be appropriate to the catchment time of concentration. Final culvert design must consider inlet/outlet control, headwater, tailwater, blockage, freeboard, debris, erosion protection, road overtopping and authority requirements.

Surface flow paths, low-point detection, affected-area mapping, cross sections, HECRAS exchange and animated 2D/3D simulation belong to later flood-analysis phases.
