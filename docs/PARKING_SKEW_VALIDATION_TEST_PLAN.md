# Parking Skew Validation — Civil 3D 2023/2024 Test Plan

Complete this plan against the exact pull-request head before merge. GitHub
Actions validates source shape and host-independent tests only; it does not
compile or run Autodesk assemblies.

## 1. Exact-head build

Build Release x64 against:

- Civil 3D 2023 / AutoCAD 2023 managed assemblies;
- Civil 3D 2024 / AutoCAD 2024 managed assemblies.

Confirm the tested commit matches the pull-request head exactly.

## 2. Ribbon and regression

Verify **Parking Skew Validation** appears under Site Design and launches:

- `CE_PKSKTOOLS`
- `CE_PKSKSETTINGS`
- `CE_PKSKVALIDATE`
- `CE_PKSKCORRECT`
- `CE_PKSKCLEAR`
- `CE_PKSKINFO`

Confirm existing parking workflows remain available and unchanged:

- `CE_PKROW`
- `CE_PKDOUBLE`
- `CE_PKREPORTUI`
- `CE_PKCOUNTX`
- `CE_PKNUMBER2`
- all legacy count/number commands.

## 3. Settings and units

Run `CE_PKSKSETTINGS`, save, close and reopen the DWG.

Test:

- required width 2500 mm;
- millimetre drawings with `drawing units per millimetre = 1`;
- metre drawings with `drawing units per millimetre = 0.001`;
- tolerance values including 0, 5 and 10 mm;
- review and correction layers;
- text height and dimension offset appropriate to each drawing unit system.

Verify `CE_PKSKINFO` reports the persisted values.

## 4. Perpendicular-width geometry

Create rectangular and skewed/parallelogram bay outlines at multiple angles,
including 0°, 30°, 45°, 60°, 75° and 90°.

For each bay, independently calculate or dimension:

- shortest polygon edge;
- perpendicular distance between the long sides;
- bay length;
- long-axis angle.

Verify CE Tools reports the perpendicular width from the minimum-area oriented
rectangle, not the skewed edge length or world-axis extents. Reproduce examples
where a misleading 1768 mm or 2165 mm projected/skewed value must not be treated
as a compliant 2500 mm perpendicular bay width.

Test non-rectangular four-sided outlines and confirm the result remains a
reviewable oriented-envelope measurement rather than a claim that irregular
geometry is a perfect parking bay.

## 5. Closed polylines

Test:

- clockwise and counter-clockwise polylines;
- duplicate/coincident vertices;
- rotated UCS;
- large coordinates;
- open polylines;
- zero-area polylines;
- curved/bulged outlines;
- more than four vertices.

Verify unsupported/open/curved/degenerate shapes are rejected with clear
reasons and are not dimensioned or corrected.

## 6. Parking blocks

Test static and dynamic parking blocks containing one clear closed straight
outline. Rotate and scale the block.

Verify:

- exploded temporary geometry is measured in world coordinates;
- the largest closed straight outline is used;
- temporary exploded entities are disposed and never added to the drawing;
- xrefs are rejected;
- blocks with no usable outline are rejected;
- source blocks remain unchanged.

Check blocks with text, symbols and multiple closed outlines for false outline
selection. Office block standards may require a dedicated approved outline.

## 7. Green/red validation output

Run `CE_PKSKVALIDATE` on passing and failing bays.

Verify:

- the preview and report list measured width, required width, difference, bay
  length, skew angle, shortest edge and status;
- cancellation changes nothing;
- compliant dimensions and labels use ACI green (3);
- failed dimensions and labels use ACI red (1);
- dimension extension points span the calculated perpendicular width;
- displayed dimension text is in millimetres even when drawing units are metres;
- rerunning refreshes only prior CE skew dimensions/labels for the same source;
- unrelated dimensions, labels and source geometry are untouched;
- the optional drawing table remains readable.

## 8. Failed-bay correction outlines

Run `CE_PKSKCORRECT` on a mixed selection.

Verify:

- only failed measurable bays receive correction outlines;
- compliant source bays are not changed;
- failed source bays and blocks are also not stretched, moved, rotated or erased;
- the separate correction outline keeps the calculated centre, long-axis angle
  and bay length;
- correction width equals the configured required width;
- correction labels identify target width, source handle and retained original;
- correction objects are linked by CE XData and use the correction layer;
- rerunning replaces only prior CE correction graphics for the same source.

The outline is a review/proposed geometry aid. Confirm aisle, kerb, obstruction,
door-swing, accessibility, circulation and authority constraints before adopting
it in the design.

## 9. Clear and information

Run `CE_PKSKCLEAR` in SelectedSources and All modes.

Verify:

- only CE parking skew dimensions, labels and correction outlines are removed;
- source bays and unrelated objects are untouched;
- selection by source handle works after save/reopen;
- missing source handles are shown in `CE_PKSKINFO` rather than guessed.

## 10. Safety and regression

Test:

- locked review/correction layers;
- model-space and paper-space context;
- Undo/Redo;
- save/reopen/AUDIT/PURGE;
- multiple open drawings;
- repeated validation/correction/clear cycles;
- all prior Dynamic Intersection, Surface, Water, Sewer, Stormwater, Parking,
  BOQ and Production workflows.

## Release boundary

Do not merge until the exact head compiles and passes this plan in Civil 3D
2023 and Civil 3D 2024. The oriented rectangle is a geometric screening and
review method. It does not by itself certify a bay against all municipal,
client, accessibility or traffic-engineering standards. Correction outlines
are proposals and intentionally preserve the original source geometry.
