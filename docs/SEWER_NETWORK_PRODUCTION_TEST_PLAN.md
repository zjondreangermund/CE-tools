# Sewer Network Production — Civil 3D Validation Plan

Validate the exact pull-request head in Civil 3D 2023 and Civil 3D 2024 before merge.

## 1. Build and ribbon

- Build the plugin against each installed Autodesk managed-assembly set.
- Load the exact bundle and confirm there are no command-registration errors.
- Confirm the Civil 3D 2023-compatible CE TOOLS ribbon still loads.
- Confirm **Sewer Network Production** contains sequence, selected-main, alignment, refresh, format, profile, settings and information commands.

## 2. Existing automatic sequence

- Run `CE_SEWSEQ` in EntireNetwork mode on a connected tree-shaped gravity network.
- Confirm every pipe and structure receives one branch assignment.
- Confirm names follow `Branch-1`, `MH1.1`, `P1.1`, etc.
- Confirm shared junctions keep the first branch that claimed them.
- Confirm cancellation and Undo restore the original names and descriptions.

## 3. Selected-main whole-network sequence

- Run `CE_SEWSEQMAIN` and select one source network part.
- Select two structures defining the intended Branch-1 main.
- Confirm the complete route between them becomes Branch-1.
- Confirm remaining network portions become Branch-2, Branch-3, etc. in a repeatable order.
- Confirm both selected structures must belong to the source network.
- Confirm the same structure cannot be selected twice.
- Confirm references, disconnected groups, loops and unconnected pipes are rejected without changes.
- Confirm collision-safe temporary names never remain after completion or Undo.

## 4. Branch alignments

- Run `CE_SEWALIGN` after both automatic and selected-main sequencing.
- Confirm one alignment is created per branch and curved pipes are represented acceptably.
- Confirm branch labels are created and tagged.
- Confirm repeated execution replaces only CE-generated sewer alignments and labels.
- Confirm unrelated alignments remain untouched.

## 5. Settings and formatting

- Run `CE_SEWSETTINGS` and enter exact office alignment/profile/profile-view/band-set style names.
- Save, close and reopen the drawing; confirm settings persist in the DWG.
- Run `CE_SEWFORMAT` and confirm the selected alignment style is applied to every CE sewer alignment.
- Confirm CE branch labels receive the selected height and are repositioned without cumulative drift on repeated runs.
- Confirm invalid style names or locked layers stop before partial commits.

## 6. Explicit alignment refresh

- Modify source network geometry after alignments exist.
- Run `CE_SEWREFRESH`.
- Confirm linked source networks are resolved from alignment metadata and passed to `CE_SEWALIGN`.
- Confirm the existing preview/confirmation appears before alignments are replaced.
- Erase or detach a source network and confirm stale links are reported rather than guessed.

## 7. Profiles and profile views

- Create an existing-ground surface covering all sewer alignments.
- Run `CE_SEWPROFILE` and choose the insertion point, views per row and spacing.
- Confirm one surface profile and one profile view are created for each CE sewer alignment.
- Confirm configured profile, label-set, profile-view and band-set styles are applied.
- Confirm profile views are arranged in the requested grid.
- Confirm pipes and structures whose descriptions match the branch are added to the correct profile view where supported.
- Confirm branch title text is readable and does not cover the profile view.
- Re-run the command and confirm CE-generated profile objects refresh without duplicates while unrelated profile objects remain.
- Modify the EG surface and confirm native Civil 3D surface-profile linkage updates as expected.

## 8. Information and drawing safety

- Run `CE_SEWINFO` and verify network, alignment, label, profile-view and settings counts.
- Test two open drawings and confirm settings and links are drawing-specific.
- Test save/reopen, Audit, Undo/Redo and workspace changes.
- Confirm no cross-document changes, command locks or event loops occur.

## Release boundary

GitHub Actions validates source shape, command uniqueness and core non-Autodesk tests only. It does not prove Autodesk API compatibility. Keep the PR draft and unmerged until both supported Civil 3D versions compile and pass this plan.
