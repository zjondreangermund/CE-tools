# Stormwater Network Production — Civil 3D Validation Plan

This plan must be completed against the exact pull-request head in both Civil 3D 2023 and Civil 3D 2024 before merge.

## 1. Build and load

- Build `CE.Tools.Civil3D` for `AutoCADVersion=2023` using the installed Autodesk managed assemblies.
- Repeat for `AutoCADVersion=2024`.
- Confirm the application bundle loads without command-registration errors.
- Confirm the CE TOOLS ribbon still appears with the Civil 3D 2023-compatible `RibbonMenuItem` flyouts.
- Confirm the Utilities panel contains separate Stormwater Production and Sewer Network menus.

## 2. Stormwater sequencing — automatic main

- Create a connected tree-shaped gravity network with one clear trunk and at least three side branches.
- Run `CE_SWSEQ`, choose `Automatic`, and select one or more network parts.
- Confirm the preview identifies the longest endpoint-to-endpoint route as `SW-MAIN`.
- Confirm branches are ordered consistently from the main route outward.
- Confirm pipe names use `SW-MAIN-P01`, `SW-B01-P01`, and equivalent sequential forms.
- Confirm structure names use the equivalent `N01` sequence without duplicate-name errors.
- Undo once and confirm all names and descriptions return to their prior state.

## 3. Stormwater sequencing — selected main

- Run `CE_SWSEQ`, choose `SelectMain`, and select two structures on one intended main route.
- Confirm the route between the selected structures becomes `SW-MAIN`.
- Confirm selecting structures from different networks is rejected.
- Confirm selecting the same structure twice is rejected.
- Confirm multiple-network selection is accepted only in Automatic mode.

## 4. Network safeguards

- Confirm referenced networks and referenced parts are rejected without modification.
- Confirm an unconnected pipe is rejected.
- Confirm disconnected groups are rejected.
- Confirm a looped network is rejected with a clear engineering-review message.
- Confirm cancellation at every preview leaves the drawing unchanged.
- Confirm temporary collision-safe names do not remain after a successful run or Undo.

## 5. Alignments from a network

- Run `CE_SWALIGN`, choose `Network`, and select parts from the sequenced network.
- Confirm one Civil 3D alignment is created for `SW-MAIN` and every branch.
- Confirm curved pipes are represented with a usable sampled alignment path.
- Confirm the configured alignment style, label-set style and layer are applied.
- Confirm each alignment stores the source pipe handles and CE branch metadata.
- Confirm repeated execution replaces only CE-generated stormwater alignments and labels.
- Confirm unrelated alignments and labels remain unchanged.
- Confirm plan labels are staggered sufficiently to reduce direct overlap.

## 6. Alignments from polylines

- Create open lightweight polylines, including lines and arc segments.
- Run `CE_SWALIGN`, choose `Polylines`, then test both Automatic and SelectMain.
- Confirm the selected/longest main is named `SW-MAIN` and the remaining lines receive sequential branch names.
- Confirm the original polylines are preserved.
- Confirm closed, invalid or unsupported objects are skipped and reported.
- Confirm the generated alignment retains the source polyline handle for traceability.

## 7. Styles and settings

- Run `CE_SWSETTINGS` and enter exact office style names for alignment, alignment label set, profile, profile label set, profile view and band set.
- Confirm the settings persist after saving, closing and reopening the DWG.
- Confirm an invalid style name produces a clear error and no partial creation.
- Confirm blank style settings use the first available drawing style and report the resolved style.
- Confirm locked output layers are rejected without partial changes.

## 8. Profiles and profile views

- Create an existing-ground surface covering every stormwater alignment.
- Run `CE_SWPROFILE` and select the surface, insertion point, views per row and spacing.
- Confirm one surface profile and one profile view are created per CE stormwater alignment.
- Confirm profile views are arranged in the requested grid without direct overlap.
- Confirm configured profile, label-set, profile-view and band-set styles are used.
- Confirm gravity pipes and structures are added to the correct profile views where supported by the installed API.
- Confirm a readable branch title is placed above each profile view.
- Confirm unrelated profiles and profile views remain unchanged.

## 9. Refresh and dynamic behaviour

- Edit source pipe geometry or a source polyline and run `CE_SWREFRESH`.
- Confirm the relevant alignments are rebuilt from current source geometry.
- Re-run `CE_SWPROFILE` and confirm CE-generated profiles/views are refreshed without duplicates.
- Modify the existing-ground surface and rebuild it; confirm the Civil 3D surface profile updates according to native Civil 3D behaviour.
- Erase a source object and confirm the command reports the missing/stale source rather than silently producing misleading output.
- Run `CE_SWINFO` and verify counts, style settings, layers and explicit-refresh boundaries.

## 10. Drawing and session safety

- Test model-space and paper-space context changes.
- Test two open drawings and confirm links/settings remain drawing-specific.
- Test save, close, reopen, Audit and Undo/Redo.
- Confirm no event loop, command lock or cross-document update occurs.
- Confirm the ribbon remains visible after workspace changes and Civil 3D restart.

## Release boundary

Do not merge until both Civil 3D versions compile and the tests above pass. GitHub Actions validates source shape, command uniqueness and non-Autodesk core logic only; it does not prove Autodesk API compatibility or runtime behaviour.
