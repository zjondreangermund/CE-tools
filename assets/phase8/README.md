# CE Tools Phase 8 uploaded engineering assets

This folder contains the controlled catalogue metadata for the uploaded project drawings and schedules.

## Catalogue checkpoint

- 20 source records
- 16 PDF drawings/details/notes
- 4 XLSX schedules and quantity workbooks
- every source record initially marked `ForReview`
- revision initially recorded as `Unconfirmed`
- SHA-256 calculated from the original uploaded bytes
- original source files copied without modification into the downloadable asset package

## Package layout

- `Drawings/General`
- `Drawings/Road`
- `Drawings/Sewer`
- `Drawings/Stormwater`
- `Drawings/Water`
- `Schedules/Road`
- `Schedules/Sewer`
- `Schedules/Water`

## Use in CE Tools

1. Extract `CE_Phase8_Asset_Package.zip` into a controlled office/project folder.
2. Run `CE_ASSETLIBSETTINGS` and select the extracted `asset-catalog.csv`.
3. Run `CE_ASSETCATALOGAUDIT`.
4. Review every `ForReview` record and confirm the title-block revision, approval status, approver and date.
5. Replace PDF-only detail records with controlled DWG counterparts where drawing insertion is required.

## Important boundaries

- PDF and XLSX records are searchable and auditable.
- Controlled drawing insertion currently supports DWG assets only.
- `ForReview` and `Unconfirmed` are deliberate safeguards; the uploaded documents do not by themselves prove office approval or professional authority.
- The catalogue does not modify the uploaded source assets.
