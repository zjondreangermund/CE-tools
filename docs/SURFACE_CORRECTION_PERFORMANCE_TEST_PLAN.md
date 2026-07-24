# Surface Correction and Performance — Civil 3D 2023/2024 Validation Plan

This plan must be completed against the exact pull-request head before merge.
GitHub Actions validates source shape and host-independent tests only. It does
not compile or run Autodesk assemblies.

## 1. Exact-head build

Build Release x64 against both installed hosts:

- Civil 3D 2023 / AutoCAD 2023 managed assemblies;
- Civil 3D 2024 / AutoCAD 2024 managed assemblies.

Confirm the tested commit matches the pull-request head exactly.

## 2. Ribbon and command regression

Verify the **CE TOOLS** tab contains **Surface Correction** under Geometry and
that these commands launch:

- `CE_SURFCTOOLS`
- `CE_SURFCSETTINGS`
- `CE_SURFAUDIT`
- `CE_SURFCORRECT`
- `CE_SURFSIMPLIFY`
- `CE_SURFCRESTORE`
- `CE_SURFCINFO`

Confirm Stormwater, Sewer, Water, Feature Line, Alignment, Profile and existing
Surface commands remain available and that the Civil 3D 2023 ribbon fix remains
intact.

## 3. Settings persistence

Run `CE_SURFCSETTINGS`, save, close and reopen the DWG, then run
`CE_SURFCINFO`.

Verify persistence of:

- zero-elevation tolerance;
- local spike/low-point tolerance;
- neighbour search radius;
- minimum neighbour count;
- contamination search radius;
- maximum audit vertex count;
- default simplification grid;
- maximum report rows.

## 4. Surface vertex and triangle API validation

Use TIN surfaces created from:

- point files;
- COGO points;
- breaklines;
- contours;
- corridors;
- pasted surfaces;
- data shortcuts/references.

Verify the exact host exposes readable surface vertices and, where available,
triangles through the reflection paths used by CE Tools. Confirm unsupported
surface types fail clearly without modifying anything.

## 5. Audit detection

Prepare controlled test surfaces containing:

- zero and near-zero elevations;
- isolated high spikes;
- isolated low spikes;
- very high and very low global tail points;
- an outer boundary and one or more internal holes;
- buildings, trees, poles, signs, overhead lines, manholes, chamber/invert
  objects and other likely contamination near surface vertices.

Run `CE_SURFAUDIT` and verify:

- no drawing object is changed;
- the pop-up report and optional drawing table show issue type, coordinates,
  magnitude, suggested elevation and reason;
- local medians are reasonable for the chosen neighbour radius;
- hole/open-edge counts match visual TIN inspection where triangle access is
  available;
- contamination screening uses layer/type/name keywords and proximity only;
- false positives and missed objects are documented;
- large surfaces respect the maximum audit-vertex setting and report that the
  audit was sampled.

The audit is a screening tool, not an automatic survey-data approval.

## 6. Reversible corrected surface

Run `CE_SURFCORRECT` with contamination set to both **Keep** and **Exclude**.

Verify:

- the command previews source count, output count, replacements and exclusions;
- Cancel leaves the drawing unchanged;
- the source surface is never opened for destructive editing or erased;
- a separate uniquely named CE corrected surface is created;
- zero/spike/low candidates with enough neighbours use the local median;
- points without a reliable local median are not silently guessed;
- contamination exclusion removes only flagged candidate points from the new
  copy;
- source style is reused where the installed `TinSurface.Create` overload
  supports it;
- CE XData stores generated type, source handle and settings summary;
- surface description states that the original was not modified;
- the generated surface rebuilds and survives save/reopen/AUDIT;
- contours, slopes, drainage paths, breakline fidelity and volumes are compared
  against the source before any engineering use.

## 7. Reversible simplification

Run `CE_SURFSIMPLIFY` on small and large surfaces with multiple grid sizes.

Verify:

- preview counts and reduction percentage are correct;
- output retains cell centroid and local high/low representatives;
- fewer than three retained points is rejected;
- a new uniquely named simplified surface is created;
- the original remains unchanged;
- grid size and source handle are stored in CE metadata;
- output contours, drainage paths, boundaries, design levels and cut/fill
  volumes remain within project tolerances;
- performance improvement is measured in Civil 3D regeneration, save and view
  operations;
- simplification is not used on final design surfaces without engineer review.

## 8. Restore/removal workflow

Run `CE_SURFCRESTORE` and verify:

- only CE generated corrected/simplified surfaces are accepted;
- ordinary Civil 3D surfaces are rejected;
- confirmation names the generated surface and original source handle;
- Cancel changes nothing;
- confirmation erases only the generated copy;
- the original source still exists and is unchanged.

## 9. Information and traceability

Run `CE_SURFCINFO` and verify:

- all generated surfaces are listed;
- generated type, handle, source handle and settings are correct;
- deleted/missing source handles show `Missing` rather than being guessed;
- the optional register table is readable and does not overwrite unrelated
  content.

## 10. Safety and regression

Test:

- locked layers and referenced surfaces;
- xrefs and data shortcuts;
- surfaces with invalid or unavailable extents;
- very large coordinates;
- UCS changes;
- model-space and paper-space contexts;
- multiple drawings;
- repeated audit/correct/simplify cycles;
- Undo/Redo;
- save/reopen/AUDIT/PURGE;
- all previously implemented CE Tools commands after loading this branch.

## Release boundary

Do not merge until the exact head compiles and passes this plan in Civil 3D
2023 and Civil 3D 2024. Vertex, triangle, TIN creation and definition point-add
APIs are reflection-based and must be proven against both installed Autodesk
versions. Audit flags, local-median replacement, contamination screening and
grid simplification must remain reviewable screening/derived-surface tools, not
an automatic approval of survey or design data.
