# Uploaded Phase 8 Source Asset Register

## Population status

The controlled catalog now contains 20 uploaded source records:

- 16 PDF records covering general civil notes, drawing register, road setting-out/details, stormwater details, sewer longitudinal sections/details, and water layout/details;
- 4 XLSX records covering junction setting-out, left-centre-right road levels, sewer quantities, and water quantities.

Every record is initially assigned:

- revision `UPLOAD-01`;
- approval status `ForReview`;
- active state `true`;
- an exact SHA-256 value calculated from the original uploaded bytes.

## Review boundary

The records are controlled references, not approved office standards. An authorised engineer/office reviewer must confirm revision, project applicability, standards, formulas, measurement rules and approval status before any record is changed to `Reviewed` or `Approved`.

The source PDF and XLSX files remain read-only. Controlled drawing insertion continues to support reviewed DWG assets only.

## Validation

`scripts/Validate-UploadedAssetCatalog.py` enforces:

- exactly 20 uploaded records;
- unique AssetId and RelativePath values;
- PDF/XLSX source types;
- valid 64-character SHA-256 identities;
- `UPLOAD-01` revision;
- `ForReview` approval status;
- active records below the `Source/` relative root.
