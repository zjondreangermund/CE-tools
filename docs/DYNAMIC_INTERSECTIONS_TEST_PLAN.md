# Dynamic Intersections — Civil 3D 2023/2024 Validation Plan

This plan must be completed against the exact pull-request head before merge.
GitHub Actions validates source shape and host-independent tests only. It does
not compile or run Autodesk assemblies.

## 1. Exact-head build

Build Release x64 against both installed hosts:

- Civil 3D 2023 / AutoCAD 2023 managed assemblies;
- Civil 3D 2024 / AutoCAD 2024 managed assemblies.

Confirm the tested commit matches the pull-request head exactly.

## 2. Ribbon and lifecycle

Verify the **CE TOOLS** tab contains **Dynamic Intersections** under Geometry
and that all commands launch:

- `CE_INTTOOLS`
- `CE_INTSETTINGS`
- `CE_INTCREATE`
- `CE_INTREFRESH`
- `CE_INTINFO`
- `CE_INTDETACH`
- `CE_INTMONITOR`

Confirm both `DynamicSectionUpdateManager` and
`DynamicIntersectionUpdateManager` initialise and terminate without duplicate
event subscriptions. Close and reopen multiple drawings and Civil 3D itself.

## 3. Settings persistence

Run `CE_INTSETTINGS`, save, close and reopen the DWG, then rerun the settings
and monitor commands.

Verify persistence of:

- output layer;
- marker radius;
- label height;
- XY intersection tolerance;
- elevation-warning difference;
- maximum curve sampling segment;
- maximum generated intersection count;
- corridor feature-code filter.

## 4. AutoCAD curve intersections

Test lines, arcs, 2D polylines, 3D polylines and splines.

Verify:

- at least two sources are required;
- source geometry is never modified;
- plan intersections are found at segment interiors and shared endpoints;
- interpolated elevations are correct for 3D segments;
- duplicated segment hits at a polyline vertex are deduplicated;
- parallel and collinear overlaps are not silently converted into one point;
- curve sampling length affects curved-source accuracy predictably;
- more than the configured maximum intersections is rejected before output;
- zero intersections still create a linked set and a readable empty register.

## 5. Feature-line intersections

Use editable site feature lines, relative-elevation feature lines and referenced
feature lines where available.

Verify:

- all supported PI/elevation points are extracted in correct order;
- 3D intersection elevations match Civil 3D inquiry values;
- feature-line names and handles appear in labels and the source register;
- source feature lines remain unchanged;
- unsupported/reflected point overloads fail clearly without deleting previous
  generated output.

## 6. Corridor feature-line intersections

Use corridors with multiple baselines, regions, assemblies and feature-line
codes. Test feature-line-code filters such as Top, ETW, Daylight and Datum.

Verify:

- baseline/corridor feature-line collections are discovered in Civil 3D 2023
  and Civil 3D 2024;
- the code filter reduces extracted paths as expected;
- duplicate paths returned through multiple API collection routes are removed;
- corridor rebuilds are reflected after automatic or explicit refresh;
- no corridor, baseline, region, assembly or target is modified;
- large corridors remain within acceptable refresh time;
- unsupported host object graphs are reported rather than recursively hanging.

The implementation creates linked CE plan-intersection graphics; it does not
claim Autodesk-native intersection-object or corridor-intersection authoring.

## 7. Generated output

Run `CE_INTCREATE` and verify:

- the preview reports source count, path count, tested segment pairs,
  intersection count and elevation warnings;
- cancellation creates nothing;
- a selectable DBPoint anchor is created at the register insertion point;
- every hit has a circle, cross, label and register row;
- labels show both sources/paths, both elevations and the elevation difference;
- differences above the configured warning threshold show `CHECK`;
- title, register and markers use the configured layer and text sizes;
- generated objects store an owner link to the anchor;
- the anchor stores schema, set name, insertion point, source handles/names and
  generated handles;
- repeated sets coexist without deleting one another.

Check dense labels and large registers at normal project plotting scales.
Automatic staggering is a review aid, not a complete collision solver.

## 8. Explicit refresh

Move/grip-edit curves and feature lines, rebuild corridors and run
`CE_INTREFRESH`.

Verify:

- only the selected linked set is regenerated;
- old generated objects are removed only when they carry the correct CE owner
  record;
- source handles and register insertion point are preserved;
- intersections that disappear are removed from the new output;
- new intersections are added;
- a missing source keeps the previous output and reports the missing handle;
- a source that no longer exposes usable paths keeps previous output and gives
  a clear message.

## 9. Deferred automatic refresh

With linked sets present:

- grip-edit each source type;
- append unrelated drawing geometry;
- rebuild a corridor;
- use Undo and Redo;
- switch between multiple open drawings;
- save, close and reopen drawings.

Verify:

- database event callbacks only queue work;
- refresh runs later on `Application.Idle`;
- the active editor must be quiescent;
- document locking succeeds;
- repeated edits are coalesced;
- generated-object writes do not recursively trigger endless refresh;
- a failed refresh is deferred/reported without destroying valid output;
- `CE_INTMONITOR` reports initialisation, linked sets and pending status.

## 10. Information and detach

Run `CE_INTINFO` and verify source handles, names, current types and live/missing
states. Check the optional table.

Run `CE_INTDETACH` in both modes:

- **Keep**: anchor/link is removed, generated owner records are removed and the
  remaining graphics become ordinary drawing objects;
- **Delete**: anchor and all linked generated objects are erased.

Verify unrelated CE intersection sets and non-CE geometry remain untouched.

## 11. Safety and regression

Test:

- locked output layer;
- frozen/off output layer and DBPoint visibility/PDMODE;
- xrefs and data shortcuts;
- model-space and paper-space contexts;
- UCS changes and large coordinates;
- multiple documents;
- save/reopen/AUDIT/PURGE;
- Undo/Redo;
- every previously implemented Surface, Water, Sewer, Stormwater, Feature Line,
  Corridor, Cross Section, BOQ and Production workflow.

## Release boundary

Do not merge until the exact head compiles and passes this plan in Civil 3D
2023 and Civil 3D 2024. Feature-line and corridor extraction is reflection-based
and must be verified against both installed Autodesk object models. The output
is a linked plan-intersection review workflow, not automatic design approval,
vertical-clearance approval or native Autodesk intersection/corridor authoring.
