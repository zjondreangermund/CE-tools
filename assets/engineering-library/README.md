# CE Tools Engineering Asset Library

This folder contains the controlled Phase 8 asset-management register and the generated `engineering-assets.csv` catalog.

## Populated uploaded records

The catalog contains 20 uploaded source records:

- 16 PDF drawing, notes, layout, longitudinal-section and typical-detail records;
- 4 XLSX setting-out and quantity template records.

All uploaded records are initially classified as `ForReview` with revision `UPLOAD-01`. Presence in the catalog does not mean that an asset is engineer-approved, authority-approved or current for another project.

`asset-pack.sha256` records the SHA-256 identity calculated from each original uploaded file. `scripts/Validate-UploadedAssetCatalog.py` checks record count, unique IDs/paths, checksum format, asset type and initial review status in CI.

## Source pack

The original PDF and XLSX bytes are distributed as a separate controlled source pack using the same relative `Source/` folder structure as the catalog. This keeps large project reference files outside source-code normalisation while allowing `CE_ASSETCATALOGAUDIT` to verify the package after it is extracted to a controlled office location.

## Commands

- `CE_ASSETLIBTOOLS`
- `CE_ASSETLIBSETTINGS`
- `CE_ASSETCATALOGTEMPLATE`
- `CE_ASSETCATALOGAUDIT`
- `CE_ASSETSEARCH`
- `CE_ASSETINSERT`
- `CE_ASSETINFO`
- `CE_ASSETREVISIONCHECK`

PDF and XLSX records are searchable and auditable. Controlled drawing insertion currently supports reviewed DWG assets only.
