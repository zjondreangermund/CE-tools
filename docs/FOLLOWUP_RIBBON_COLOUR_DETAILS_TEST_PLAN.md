# CE Tools Ribbon, Colour 250 and Typical Details Validation Plan

## Purpose

This follow-up records the first live Civil 3D 2023 findings after the merged review-comment batches:

- the plugin commands loaded but the CE TOOLS tab was blank;
- Civil 3D 2023 did not expose the expected `RibbonRow` source type;
- `RibbonMenuButton.Items` rejected ordinary `RibbonButton` objects;
- the original Colour 250 workflow did not explicitly control annotation colour overrides; and
- a master Typical Details catalogue is required.

## Required hosts

Run the complete plan in:

- Civil 3D 2023;
- Civil 3D 2024.

Use copies of representative production drawings.

## Ribbon regression

1. Build from the exact PR head for Civil 3D 2023.
2. Install the generated application bundle and restart Civil 3D.
3. Confirm the CE TOOLS tab contains Project, Survey, Drawings, Geometry, Corridors, Site Design, Utilities, Standards & Details, Analysis and Production.
4. Open every flyout and confirm no `InvalidOperationException` is written at the command line.
5. Start at least one command from every panel.
6. Repeat in Civil 3D 2024.
7. Confirm optional generated icons do not prevent text menus from appearing.

## Colour 250

1. Run `CE_COLOR250` and choose `GeometryOnly`.
2. Select geometry, dimensions, MText, MLeaders, Civil 3D labels and attributed blocks.
3. Confirm geometry changes to ACI 250 and annotation is reported as excluded.
4. Undo once and confirm all geometry changes are reversed.
5. Run again and choose `IncludeAnnotation`.
6. Confirm DBText, MText, dimensions, leaders, MLeaders, tables and block attributes change where object overrides are supported.
7. Confirm locked-layer objects are skipped without stopping the command.
8. Confirm Civil 3D label components controlled by a label style are reported and checked against the intended style colour.
9. Confirm preselection and normal selection both work.

## Typical Details Phase 1

1. Create a test library with these folders: Roadworks, Stormwater, Sewer, Water, Earthworks, Parking, Landscaping, Structures, Standard Construction Notes and General Details.
2. Add representative DWG, DXF and PDF files.
3. Run `CE_DETAILSETROOT` and save the master folder.
4. Save, close and reopen the DWG; confirm `CE_DETAILINFO` reports the stored folder.
5. Run `CE_DETAILSEARCH` using terms such as `kerb inlet`, `headwall`, `trench drain`, `fire hydrant` and `valve chamber`.
6. Confirm inaccessible folders do not stop the entire search.
7. Run `CE_DETAILINSERT`, select an approved DWG and enter insertion point, scale and rotation.
8. Confirm the detail is inserted as a uniquely named block and has a `CE_TYPICAL_DETAIL_LINK` source record.
9. Insert the same DWG twice and confirm duplicate block-name conflicts are avoided.
10. Undo the inserted block and verify the drawing remains valid.
11. Confirm DXF and PDF assets are indexed but not silently inserted in Phase 1.
12. Confirm only reviewed office details are used for issue drawings.

## Not yet claimed

This phase does not claim automatic engineering approval, missing-dimension detection, text/layer standardisation, parametric detail generation, dynamic source refresh or BOQ linkage. Those remain planned follow-up phases and require separate Civil 3D validation.
