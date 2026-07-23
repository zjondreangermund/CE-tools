# Review Comments Batch 1 Validation Plan

This plan validates the first implementation batch from the CE Tools review received on 23 July 2026.

## Supported hosts

Run every applicable test in:

- Civil 3D 2023
- Civil 3D 2024
- A new metric drawing
- A saved drawing reopened after metadata changes

## 1. Project Setup pop-up and table

1. Run `CE_PROJECTSETUP`.
2. Complete all project fields.
3. Confirm that a review pop-up displays every field before save.
4. Select **Save**.
5. Confirm that a second information pop-up appears after save.
6. Select **Place Table** and choose an insertion point.
7. Confirm that the table contains a merged title row, Field/Value headings and all eight project fields.
8. Save, close and reopen the drawing.
9. Run `CE_PROJECTINFO` and confirm that the same values appear.
10. Place a second table and confirm that no metadata changes occur.

## 2. Recoverable project clear

1. With project metadata present, run `CE_PROJECTCLEAR`.
2. Confirm that the clear review pop-up shows the values that will be removed.
3. Select **Cancel** and confirm that `CE_PROJECTINFO` still reports the values.
4. Run `CE_PROJECTCLEAR` again and select **Clear**.
5. Confirm that `CE_PROJECTINFO` reports no active metadata.
6. Run `CE_PROJECTRESTORE`.
7. Confirm that the restore pop-up shows the exact pre-clear values.
8. Select **Restore** and confirm that `CE_PROJECTINFO` shows the recovered values.
9. Run `CE_PROJECTRESTORE` again and confirm that no stale backup is available.
10. Repeat the clear operation, then create a new project setup; confirm that the old backup cannot overwrite the new setup.

## 3. Native coordinate-system assignment

1. Run `CE_COORDSYSASSIGN` from the command line and from the ribbon.
2. Confirm that Autodesk's native coordinate-system selection window opens.
3. Select a valid Namibia coordinate-system code and accept it.
4. Run `CE_COORDSYSINFO` and confirm that the selected code is reported.
5. Run `CE_COORDSYSCODE` and confirm that advanced direct-code assignment remains available.
6. Enter an invalid code and confirm that the drawing assignment is not changed.
7. Confirm that assigning or clearing a coordinate system does not move existing geometry.

## 4. Standards source-file selection

1. Run `CE_STANDARDSELECT`.
2. Confirm that a file-selection window opens before metadata prompts.
3. Test at least one `.dwt` or `.dwg` source and one document source such as `.pdf` or `.xlsx`.
4. Confirm that the review pop-up includes:
   - full standards-file path
   - file type
   - last-modified UTC value
   - SHA-256 checksum
   - project standards fields
5. Select **Cancel** and confirm that existing standards metadata is unchanged.
6. Run the command again, select **Save**, and confirm that the record is stored.
7. Run `CE_STANDARDINFO` and verify all values.
8. Select **Place Table** and confirm that the standards register is inserted correctly.
9. Save, close and reopen the drawing; confirm persistence.
10. Change the source file, select it again and confirm that the checksum changes.
11. Run `CE_STANDARDCLEAR`, cancel once, then clear and confirm that the project Standards summary is also cleared.

## 5. Regression checks

- Bellmouth Densifier still runs.
- Total Length still runs.
- Total Area still runs.
- Existing project metadata created with schema version 1 remains readable.
- Existing standards metadata created with schema version 1 remains readable.
- Ribbon contains no duplicate CE Tools panels or command buttons after reload.
- No command leaves a partial table or partial metadata record after cancellation or an exception.

## Release gate

Do not merge the batch until:

- core tests pass in GitHub Actions;
- the Civil 3D project compiles for 2023 and 2024 on a workstation with Autodesk references;
- all manual tests above are recorded as pass/fail;
- any native `MAPCSASSIGN` command-name difference discovered in a host version is corrected.
