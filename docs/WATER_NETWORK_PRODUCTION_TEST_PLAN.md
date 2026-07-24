# Water Network Production — Civil 3D 2023/2024 Validation Plan

This plan must be completed against the exact pull-request head before merge.
GitHub Actions validates source shape and host-independent tests only; it does
not compile or run Autodesk assemblies.

## 1. Exact-head build

Build Release x64 against both installed hosts:

- Civil 3D 2023 / AutoCAD 2023 managed assemblies;
- Civil 3D 2024 / AutoCAD 2024 managed assemblies.

Confirm no source edits are made after the tested commit.

## 2. Ribbon regression

Open Civil 3D and verify:

- the **CE TOOLS** tab appears;
- Stormwater, Sewer and Water production flyouts all appear under Utilities;
- every Water flyout entry launches the intended command;
- the Civil 3D 2023 `RibbonMenuItem` compatibility fix remains intact;
- no existing Project, Survey, Drawing, Geometry, Corridor, Parking, Standards,
  Analysis or Production command disappears.

## 3. Settings persistence

Run `CE_WATERSETTINGS`, save the DWG, close and reopen it, then run
`CE_WATERINFO`.

Verify persistence of:

- alignment and alignment-label-set styles;
- profile, profile-label-set, profile-view and band-set styles;
- alignment, profile and asset-review layers;
- plan label height;
- isolating-valve and hydrant spacing;
- asset marker radius.

Test blank style names and exact office style names.

## 4. Polyline routes

Create several open 2D and 3D polylines with different lengths.

Run `CE_WATERSEQ` and verify:

- the longest selected route is previewed as `W-MAIN`;
- remaining routes are sequenced `W-B01`, `W-B02`, etc.;
- cancel leaves all source objects unchanged;
- confirmation writes traceable CE XData;
- rerunning produces a predictable sequence;
- closed, zero-length and unsupported objects are rejected.

Run `CE_WATERALIGN` and verify:

- one Civil 3D alignment is created per accepted source;
- source polylines remain in the drawing;
- styles and layers are applied;
- labels are staggered sufficiently for review;
- only CE-generated water alignments and labels are replaced on rerun;
- non-CE alignments remain untouched.

Edit source geometry and run `CE_WATERREFRESH`. Verify the alignment follows
the current source and stale source handles are reported rather than guessed.

## 5. Pressure-network sources

Use Civil 3D pressure-network test drawings from both supported versions.
Test straight, curved and branched pressure pipes where available.

Verify:

- pressure-pipe objects are discovered without a version-specific type crash;
- start/end geometry is read correctly;
- `CE_WATERSEQ` and `CE_WATERALIGN` give clear feedback for unsupported host
  object shapes;
- network fittings and appurtenances are not renamed or moved without an
  explicit supported operation;
- references and data shortcuts are not silently edited.

The reflection-based pressure API must be checked against the exact installed
2023 and 2024 object models.

## 6. Profiles and profile views

Create a valid existing-ground surface and run `CE_WATERPROFILE`.

Verify:

- one EG profile and profile view is created per CE water alignment;
- selected styles, bands and profile layer are applied;
- repeated runs replace only CE water profile output;
- profile-view grid spacing is usable at project drawing scale;
- pressure-network parts are displayed where the installed API supports the
  part-to-profile-view method;
- failure to add pressure parts does not destroy the valid profile or view;
- deleting a source surface or alignment gives a clear failure message;
- one Undo reverses a confirmed generation transaction where supported.

## 7. Controlled asset placement

Run `CE_WATERPLACE` on selected CE water alignments.

Verify the preview reports quantities for:

- isolating/gate valve review markers;
- fire-hydrant review markers;
- air-valve review markers at 3D-polyline local high points;
- scour-valve review markers at 3D-polyline local low points.

Confirm:

- no marker is created before confirmation;
- spacing changes alter the preview and generated results;
- each marker and label stores route, source handle, asset type and station;
- marker labels clearly state their review reason;
- `CE_WATERPLACEREFRESH` removes and regenerates only CE water asset markers;
- valid non-CE blocks, symbols and notes remain untouched;
- overlapping marker/label conditions are reviewed on short and dense networks.

These outputs are engineering-review markers, not final pressure-network
appurtenances. Confirm final design checks include hydraulic performance,
coverage, isolation zones, chambers, cover, thrust restraint, operation,
maintenance access and authority requirements.

## 8. Drawing safety and regression

Test:

- locked output layers;
- xrefs and data references;
- save/reopen and AUDIT;
- repeated commands;
- command cancellation at every prompt;
- model-space/paper-space context;
- UCS changes;
- large coordinates;
- multi-document use;
- Undo/Redo;
- all Stormwater and Sewer production commands after loading this branch.

## Release boundary

Do not merge until the exact head compiles and passes this plan in Civil 3D
2023 and Civil 3D 2024. Pressure-network reflection and profile-view overloads
must be verified on the installed Autodesk assemblies. Valve and hydrant review
markers must not be represented as an approved hydraulic design or as native
pressure-network appurtenances unless exact-host insertion is separately added
and validated.
