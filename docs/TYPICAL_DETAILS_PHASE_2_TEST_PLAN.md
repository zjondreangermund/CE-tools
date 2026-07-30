# Typical Details Phase 2 — Civil 3D 2023/2024 Validation Plan

Complete this plan against the exact pull-request head before merge. GitHub
Actions validates source shape and host-independent tests only; it does not
compile or run Autodesk assemblies.

## 1. Exact-head build

Build Release x64 against:

- Civil 3D 2023 / AutoCAD 2023 managed assemblies;
- Civil 3D 2024 / AutoCAD 2024 managed assemblies.

Confirm the tested commit matches the pull-request head exactly.

## 2. Ribbon and Phase 1 regression

Verify **Details Standards Review** appears under Standards & Details and
launches:

- `CE_DETAILREVIEWTOOLS`
- `CE_DETAILREVIEWSETTINGS`
- `CE_DETAILREVIEW`
- `CE_DETAILREVIEWLIB`
- `CE_DETAILREVIEWREPORT`
- `CE_DETAILREVIEWINFO`

Confirm the original Typical Details Phase 1 commands remain available and
unchanged:

- `CE_DETAILTOOLS`
- `CE_DETAILSETROOT`
- `CE_DETAILSEARCH`
- `CE_DETAILINSERT`
- `CE_DETAILINFO`

Also confirm the Civil 3D 2023 ribbon compatibility fix remains intact.

## 3. Settings persistence

Configure and persist:

- approved text styles;
- approved dimension styles;
- preferred layer prefix;
- title/title-block keywords;
- revision keywords;
- notes keywords;
- legend keywords;
- north-arrow keywords;
- company-logo keywords;
- sheet-number keywords;
- scale keywords;
- maximum library files per review;
- maximum findings per file.

Save, close and reopen the DWG. Run `CE_DETAILREVIEWINFO` and verify every
setting and limit is retained.

## 4. Read-only DWG review

Prepare representative approved and intentionally inconsistent DWG details.
Include variations in:

- model-space-only details;
- paper-space title blocks;
- title and drawing-number attributes;
- revision tables and revision blocks;
- notes, legends and north arrows;
- company-logo blocks and raster images;
- text styles and font files;
- dimension styles;
- layer names, colours, linetypes and lineweights;
- explicit entity colour and lineweight overrides;
- scales, viewports and plot-layout information;
- symbols/blocks;
- dimensions, leaders, callouts, labels and long note text.

Run `CE_DETAILREVIEW` and verify:

- the source file is opened through a side database with read sharing;
- the source drawing is never activated, saved or modified;
- the review reports title format, revision table, notes, legends, north arrow,
  fonts, dimensions, logo, sheet numbering, layers, lineweights, scales and
  symbols;
- approved-style comparisons honour the configured lists;
- layer-prefix checks ignore system layers 0 and DEFPOINTS;
- missing dimensions/notes/callouts/labels are reported as review prompts;
- the evidence column identifies counts, names, keywords or examples;
- cancellation and report-table placement affect only the active project DWG,
  never the reviewed detail.

Compare timestamps and hashes of the source DWGs before and after review.

## 5. DXF review

Test ASCII and binary-compatible DXF files from several AutoCAD versions.

Verify:

- the installed host exposes a usable `Database.DxfIn` overload;
- DXF content is loaded into a disposable side database;
- no source DXF is changed;
- any diagnostic log is created only in an approved temporary location and is
  removed or retained according to office policy;
- the same inventory and consistency checks run as for DWG;
- unsupported or corrupt DXF files generate an Error finding without stopping
  the remaining library review.

The reflection-based `DxfIn` path must be tested separately in Civil 3D 2023
and 2024.

## 6. PDF review boundary

Review representative PDF details.

Verify:

- file path, format, size and modified time are recorded;
- category and filename keyword indicators are reported;
- every drawing-content review area is explicitly marked **Manual visual review
  required for PDF content**;
- the tool does not rasterise, OCR, edit, rewrite or claim full inspection of
  the PDF;
- source PDF timestamps/hashes remain unchanged.

Manual PDF checks must cover title, revision table, notes, legend, north arrow,
fonts, dimensions, logo, sheet numbering, layers/lineweights where visually
represented, scale, symbols and missing callouts/labels.

## 7. Complete-library review

Configure a master library containing DWG, DXF, PDF, nested folders, invalid
files and inaccessible files. Run `CE_DETAILREVIEWLIB`.

Verify:

- only supported extensions are included;
- recursive enumeration honours the maximum-file limit;
- categories are inferred from folders/file names consistently;
- one corrupt file does not stop the complete review;
- progress messages identify the current file;
- the stored register is replaced only after confirmation;
- findings are capped by the per-file limit with a clear truncation row;
- the active DWG stores file path, format, category, modified time, severity,
  area, finding and evidence;
- stored data survives save/reopen;
- the source library remains unchanged.

Test at least 100 files and measure review time/memory use.

## 8. Stored report and traceability

Run `CE_DETAILREVIEWREPORT` after single-file and library reviews.

Verify:

- file-by-file findings match the latest stored register;
- paths remain traceable to the reviewed asset;
- single-file review replaces only the same file's earlier result;
- complete-library review replaces the whole prior register;
- the optional drawing table is readable and does not overwrite unrelated
  content;
- stored rows are safely capped at the global register limit;
- `CE_DETAILREVIEWINFO` reports schema, last review time, file count and finding
  count.

## 9. Heuristic accuracy review

For each review area, document false positives and false negatives:

- title-block keyword detection;
- revision detection;
- notes and legend detection;
- north-arrow and logo detection;
- sheet-number attributes;
- text/dimension style approval lists;
- layer-prefix checks;
- lineweight/default-lineweight checks;
- non-ByLayer overrides;
- scale evidence;
- missing dimensions, notes, callouts and labels.

Adjust office keywords and approved lists before relying on reports for library
standardisation. The tool identifies likely consistency gaps; it does not know
the engineering intent of every detail.

## 10. Safety and regression

Test:

- read-only network folders;
- OneDrive/synchronised folders;
- long paths and special characters;
- missing fonts/xrefs/images;
- password-protected, corrupt and newer-version drawings;
- multiple open drawings;
- Undo/Redo in the active project drawing;
- save/reopen/AUDIT/PURGE;
- all previous Parking, Dynamic Intersection, Surface, Water, Sewer,
  Stormwater, BOQ, Client Book and Phase 1 Typical Details workflows.

## Release boundary

Do not merge until the exact head compiles and passes this plan in Civil 3D
2023 and Civil 3D 2024. Findings are heuristic office/engineering review prompts.
They do not automatically approve, reject, standardise or modify a detail.
Source DWG, DXF and PDF assets must remain unchanged. PDF content requires
manual visual review unless a separately approved inspection pipeline is added
later.
