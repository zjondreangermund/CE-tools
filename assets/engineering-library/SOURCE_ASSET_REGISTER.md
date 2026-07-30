# Uploaded Phase 8 Source Asset Register

## Population status

The controlled catalog now contains **33 uploaded source records**:

- **24 PDF records** covering general civil notes, the drawing register, road general layouts/plan-long sections/setting-out/details, stormwater details, sewer layouts/longitudinal sections/details, and water layouts/details;
- **9 XLSX records** covering junction setting-out, left-centre-right road levels, sewer/water quantities, sewer structures/pipes, road horizontal/vertical alignment data, and water structures.

Every record is initially assigned:

- revision `UPLOAD-01`;
- approval status `ForReview`;
- active state `true`;
- an exact SHA-256 value calculated from the original uploaded bytes.

## Latest drawing additions

- `CE-R-100`: roads general layout and locality plan;
- `CE-R-101` and `CE-R-102`: road plan views and longitudinal sections;
- `CE-S-100`: sewer general layout and locality plan;
- `CE-S-101` and `CE-S-102`: sewer layout sheets;
- `CE-W-100`: water general layout and locality plan;
- `CE-W-102`: water layout sheet 2.

## Model-data workbooks

- `SEWER STRUCTURE DATA.xlsx`: node name, coordinates, surface/rim/invert elevations, depth and node type;
- `SEWER PIPE DATA.xlsx`: pipe name, description, size, length and slope;
- `HORIZONTAL DATA.xlsx`: road element type, length, radius, speed, direction, station and control-point data;
- `VERTICAL DATA.xlsx`: PVI station/elevation, grades, curve type/length, K value, radius and design speed;
- `WATER STRUCTURE DATA.xlsx`: node name/type, coordinates, surface/invert elevations and depth.

Ten uploaded files carrying a `(1)` suffix were byte-identical duplicates. They are excluded so each unique source is registered once.

## Review boundary

The records are controlled references, not approved office standards. An authorised engineer/office reviewer must confirm revision, project applicability, standards, formulas, measurement rules, coordinate convention, datum, units and approval status before any record is changed to `Reviewed` or `Approved`.

The source PDF and XLSX files remain read-only. Controlled drawing insertion continues to support reviewed DWG assets only.

## Validation

`scripts/Validate-UploadedAssetCatalog.py` enforces:

- exactly 33 uploaded records;
- unique AssetId and RelativePath values;
- PDF/XLSX source types;
- valid 64-character SHA-256 identities;
- `UPLOAD-01` revision;
- `ForReview` approval status;
- active records below the `Source/` relative root.
