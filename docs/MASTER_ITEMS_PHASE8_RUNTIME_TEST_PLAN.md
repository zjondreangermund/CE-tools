# CE Tools Master Items Phase 8 runtime test plan

Use a disposable Civil 3D drawing and a temporary asset-library folder. Do not point initial tests at approved production standards.

## 1. Build gate

Run:

```powershell
.\scripts\Build-CE-Tools-Master-Items-Phase8.ps1 -Version 2023 -Configuration Release
```

Required result: zero compiler errors and the Phase 8 DLL copied into the repository bundle. Repeat for 2024 after the 2023 runtime pass.

## 2. Ribbon and command registration

Confirm **Integration → Engineering Asset Library** contains:

- Engineering Asset Library Tools
- Engineering Asset Library Settings
- Create Engineering Asset Catalog
- Audit Engineering Asset Catalog
- Search Engineering Asset Library
- Insert Controlled DWG Asset
- Inserted Asset Information
- Check Inserted Asset Revisions

Run each command by name once to confirm command registration.

## 3. Template safety

1. Run `CE_ASSETCATALOGTEMPLATE` in an empty temporary folder.
2. Confirm the CSV and these folders exist:
   - Typical Details
   - Standards
   - Symbols
   - Furniture 2D
   - Furniture 3D
   - Specifications
3. Run the command again with the same path.
4. Confirm the existing catalog is not overwritten.

## 4. Settings

1. Run `CE_ASSETLIBSETTINGS`.
2. Select the test catalog.
3. Set drawing units per metre correctly for the test drawing.
4. Test Approved, Reviewed and All visibility modes.
5. Save/reopen the drawing and confirm the settings persist.

## 5. Catalog audit

Create test catalog rows for:

- valid approved DWG with matching SHA-256;
- missing source file;
- approved source with blank checksum;
- changed source with mismatching checksum;
- duplicate AssetId;
- duplicate source path;
- superseded asset marked active;
- unsupported asset type;
- approved asset without ApprovedBy.

Run `CE_ASSETCATALOGAUDIT` and verify each condition appears with the expected severity/action. Export the audit to Excel and open the workbook.

## 6. Search and approval visibility

1. Add assets across Road, Stormwater, Sewer, Water, Landscape and Drawing Production.
2. Add tags such as kerb, headwall, trench drain, valve chamber, fire hydrant, tree and title block.
3. Run `CE_ASSETSEARCH` with:
   - AssetId;
   - title words;
   - multiple tag terms;
   - category filter;
   - discipline filter;
   - Approved visibility;
   - Reviewed visibility;
   - All visibility.
4. Confirm inactive and superseded assets do not appear.
5. Confirm the result display stops at 100 records while reporting the full match count.

## 7. Controlled DWG insertion

Prepare one simple test DWG with a known SHA-256 and catalog units.

1. Run `CE_ASSETINSERT`.
2. Select the approved test asset.
3. Confirm the review shows AssetId, title, revision, status, approver, source, checksum, scale and rotation.
4. Confirm cancellation creates nothing.
5. Insert the asset and check:
   - source DWG modified time/hash remain unchanged;
   - one block reference is created;
   - insertion point, rotation and unit scale are correct;
   - block name includes AssetId/revision/checksum prefix;
   - XData contains catalog/source/revision/checksum/scale/time metadata.
6. Insert the same asset again and confirm the block definition is reused safely.
7. Change the source file and confirm checksum mismatch blocks insertion.
8. Confirm missing checksum blocks insertion.
9. Confirm superseded/inactive assets cannot be inserted.
10. Confirm Reviewed/Draft assets require explicit internal-review confirmation.
11. Confirm PDF/PNG/DXF entries are searchable but not passed to the DWG insertion workflow.

## 8. Information and revision checking

1. Select an inserted block and run `CE_ASSETINFO`.
2. Confirm all insertion metadata and current source state are shown.
3. Run `CE_ASSETREVISIONCHECK` with:
   - unchanged source/catalog;
   - changed source hash;
   - missing source;
   - missing catalog;
   - same AssetId with a new approved revision;
   - catalog record changed to Superseded;
   - catalog record removed.
4. Confirm no block is automatically replaced, erased or edited.
5. Export the revision report to Excel.

## 9. Drawing safety

- Undo/redo insertion.
- Save/reopen and re-run information/revision checks.
- Test model space and a layout.
- Test locked layers and read-only/network source folders.
- Confirm source files are never saved, renamed, moved, deleted or overwritten.
- Confirm unrelated blocks/entities are unchanged.

## 10. Engineering governance

Before production use, the office must approve:

- catalog naming and revision rules;
- approval statuses and authorised reviewers;
- units-per-metre values;
- source licensing/copyright;
- title blocks, notes, legends, north arrows, fonts, dimensions, logos and sheet numbering;
- each typical detail and civil/furniture asset;
- replacement/update procedures for previously inserted blocks.

Passing this test plan validates the software workflow only. It does not certify the engineering content of any asset.
