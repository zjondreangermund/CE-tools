# CE Tools Master Items Phase 8 register

Base checkpoint: exact Phase 7 head `c8dc99d421e9fe0168418592f1562c36b92644fe`.

## Source implemented

- [x] CSV engineering asset catalog with a 10,000-record safety limit.
- [x] Stable AssetId, title, category, discipline, type, path, revision and active-state metadata.
- [x] Draft, ForReview, Reviewed, Approved and Superseded approval states.
- [x] ApprovedBy and ApprovalDateUtc traceability fields.
- [x] Explicit source UnitsPerMetre and drawing-units-per-metre conversion.
- [x] Tags and descriptions for deterministic search.
- [x] SHA-256 source identity.
- [x] Non-overwriting template creation.
- [x] Standard folders for Typical Details, Standards, Symbols, Furniture 2D, Furniture 3D and Specifications.
- [x] Missing-file, blank/changed checksum, duplicate ID/path, unsupported type and approval-state diagnostics.
- [x] Active-only, approval-filtered search by ID/title/category/discipline/tags/description.
- [x] Drawing-specific catalog path, drawing units and approval visibility settings.
- [x] Controlled DWG insertion from a read-only side database.
- [x] Checksum mismatch prevents insertion.
- [x] Superseded/inactive assets cannot be inserted.
- [x] Non-approved assets require explicit internal-review confirmation.
- [x] Block names include AssetId, revision and checksum prefix.
- [x] Inserted blocks store AssetId, revision, approval status, catalog/source paths, source checksum, scale and insertion UTC in XData.
- [x] Inserted-asset information and current source-state review.
- [x] Project-wide inserted-asset revision/catalog/source comparison.
- [x] Excel export for audit and revision reports without Office COM automation.
- [x] Dedicated host-independent catalog tests.
- [x] Dedicated source validator, command-registry audit and ordered Phase 8 build/CI chain.

## Commands

- `CE_ASSETLIBTOOLS`
- `CE_ASSETLIBSETTINGS`
- `CE_ASSETCATALOGTEMPLATE`
- `CE_ASSETCATALOGAUDIT`
- `CE_ASSETSEARCH`
- `CE_ASSETINSERT`
- `CE_ASSETINFO`
- `CE_ASSETREVISIONCHECK`

## Important boundary

Phase 8 provides the controlled library framework. It does not create or automatically approve the actual engineering standards, typical details, title blocks, symbols, specifications, 2D furniture or 3D furniture. Those source assets must be supplied, reviewed, revised and approved through the user's office/engineer governance process.

Catalog status and reviewer fields are recorded metadata. CE Tools does not verify professional authority, structural/hydraulic adequacy, local-authority acceptance, copyright/licensing or suitability for a particular project.

## Runtime pending

- [ ] Civil 3D 2023 Release compilation.
- [ ] Civil 3D 2023 runtime test using the Phase 8 test plan.
- [ ] Civil 3D 2024 Release compilation.
- [ ] Civil 3D 2024 runtime test.
- [ ] Office-approved catalog and asset population.
- [ ] Irregular units, duplicate block names, locked drawings and network-drive access tests.
- [ ] Large catalog performance test near the 10,000-record limit.
