# Civil 3D 2026 port — isolation and acceptance gates

## Non-negotiable compatibility boundary

- Baseline: `main` (Civil 3D 2023 production version). Never amend it for an unverified 2026 API fix.
- Work only on `civil3d-2026-isolated-port` until the 2026 host-specific adapter has been compiled and exercised.
- Maintain command names, UI labels, calculation methods, geometric definitions, output structure, defaults, and data persistence semantics.
- Resolve version differences using conditional compilation / host-specific adapter files or isolated services, not edits that change the 2023 behavior.
- Preserve the 2023 .NET Framework 4.8 binary and installation.
- Do not run the existing general `Install-CE-Tools.ps1` to trial 2026: it deletes the entire installed bundle and could remove the working 2023 adapter.
- Never rely on a compile alone as proof of Civil 3D feature correctness.

## Existing version foundation observed

- The project maps 2023 to net48 and 2026 to net8.0-windows.
- `PackageContents.xml` includes separate 2023 and 2026 component entries.
- The current 2023 pre-build/finalizer chain contains source-rewriting scripts; building on an isolated worktree avoids mutation of the production checkout.
- The 2026 build requires Autodesk AutoCAD/Civil 3D 2026 reference DLLs from an installed 2026 host.

## Release gating

1. Build the isolated 2026 preview against installed 2026 assemblies, recording full compiler errors and warnings.
2. Fix 2026 API incompatibilities only in the protected 2026 branch. Run shared core and API-contract tests.
3. Launch Civil 3D 2026 in an isolated test profile, load only the 2026 compiled adapter, confirm ribbon startup and no duplicate command registration.
4. Run open, create, edit, undo, regenerate, save, reopen, and second-run regression tests on copies of drawings.
5. Validate by CE Tools disciplines: Home/production, Survey, Platform, Road/corridor, Stormwater, Sewer, Water/Bulk Water, Parking, Flood, CAD/quantities/export.
6. Stress-test known-sensitive commands: road sheet creation and rerun, profile viewport fit and band annotations, feature-line grading, network structure/pipe modifications, surface rebuild, batch actions.
7. Verify the 2023 DLL hash and 2023 command test baselines are unchanged.
8. Package the verified 2026 DLL separately with a version-specific install path; never overwrite or delete the shared installed 2023 bundle.

## Current status

- A protected port branch and isolated preview build entry point exist.
- The code has **not** been compiled against a real Civil 3D 2026 installation in this session.
- No 2026 runtime tests have been executed, and no release is declared production-ready.
