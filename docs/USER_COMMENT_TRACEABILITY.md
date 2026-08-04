# CE Tools User Comment Traceability

This register prevents the requirements gathered during Civil 3D 2023 testing from being lost during later development. “Source complete” means an implementation and regression marker exist in `main`; it does not replace a live Civil 3D 2023 drawing test.

## Source-complete comments

| Area | Comment carried into the source | Current implementation |
|---|---|---|
| Survey coordinates | Use `POINT NAME, X, Y, Z`; do not show Northing/Easting wording | Four-column centred linked table in `SurveyCoordinateWorkflowCommands` |
| Polyline vertices | Select one or multiple 2D/3D polylines; preserve stored direction | Multi-selection in `CE_COORDPOLY2`; each source stores its own vertex-index links |
| COGO points | Select COGO output; start names at P1; point name and raw description must agree | Shared annotation-output selection, P1 default sequence and explicit point-name/raw-description assignment |
| Dynamic coordinates | Moving a polyline vertex or linked point must update points, Z, annotations and tables | `DynamicCoordinateLinkStore.Refresh`, optional surface elevation link and linked-table rebuild |
| Coordinate presentation | Larger rows, useful cell spacing and centred text | Scale-safe text height, expanded rows/columns and middle-centred title/header/data cells |
| Direction arrows | Refresh and reverse arrows with the source polyline | `CE_PLDIRREFRESH`, `CE_PLDIRREVERSE` and shared refresh integration |
| Parking | Closed bays, linked boundary layouts, numbering, reports and automatic refresh | Parking row/block workflows, dynamic option/grading/optimizer modules and parking refresh managers |
| Stormwater | Network/polyline alignments, automatic or selected main, branches, styles, profiles and bands | Stormwater production, sequence and profile workflows with linked metadata |
| Sewer | Selected-main or automatic sequencing, Branch-1 numbering, alignments, offset labels, profiles, styles and bands | Sewer production/sequence modules and `SewerBranchLabelPlacement` |
| Water | Linked alignments, profiles, styles, bands and asset review markers | Water production workflow and pressure-part profile-view integration |
| Profile tools | Select profile-view styles and band sets, batch apply, fit and rebuild | WPF `ProfileViewBatchWindow` with style/band catalogues |
| Dynamic profile annotation | Drag a point along an alignment and update station, elevation, grade and text | `ProfileAnnotationLinkStore` |
| Annotation | Paper text sizes, annotative scale synchronisation and overlap cleanup | Shared annotation settings, `AnnotationScaleSyncManager` and `CE_OVERLAPFIX` |
| Workflow window | Auto-open, Ctrl+F, all commands, General/Survey/Roads/Stormwater/Sewer/Water/Bulk Water/Flood | Reflection-backed 397-command catalogue and discipline tabs |
| Settings UX | Use dialogs rather than command-line workflow selection wherever practical | Searchable `CE_SETTINGS` centre plus dedicated Stormwater, Sewer and Water workflow/settings windows with installed-style dropdowns, paper heights and persisted profile layout |
| BOQs and costs | Linked BOQs and water/sewer estimates with automatic refresh | BOQ/cost link stores and deferred refresh managers |
| Drawing production | A4, A3, A1 and A0 layouts, books and indexes | Production and client-book modules |
| Installer | Civil 3D 2023 checks, Release x64, source commit, installation log, SHA-256 comparison and rollback | V61 package with double-click elevated installer, embedded release manifest and source/stage/install verification used by both 2023 build paths |

## Implemented but requiring live Civil 3D 2023 validation

- Polyline grip edits moving linked COGO/DBPoint outputs and follower annotations.
- Surface-linked Z updates for points inside and outside surface boundaries.
- Parking boundary-grip refresh, block geometry, numbering and report regeneration.
- Stormwater, sewer and water alignment/profile creation against real project styles and networks.
- Branch label size, above/below offset, repeated labels and overlap behaviour at plotted scales.
- Profile-view style and band-set enumeration in the installed Civil 3D 2023 style catalogue.
- Undo/redo, drawing switching, save/reopen and automatic refresh event sequencing.
- Verified installer rollback under an intentionally interrupted installation.

## Not represented as complete

- Full solved 2D/3D hydraulic flood simulation is not claimed. Current flood tools provide terrain/hydrology screening, imported result review, property tables and point-sample animation. A certified solver and calibrated model workflow remain a later module.
- Replacing every remaining advanced command-line settings prompt with a dialog remains ongoing. Stormwater, Sewer and Water launchers/settings/profile layout are now window-driven; specialist geometry inputs and several older utility settings still use Civil 3D prompts.
- Public code signing remains release-pipeline work. A safe in-product update check and a Windows Civil 3D GitHub workflow now exist; automatic binary replacement remains intentionally disabled until signed releases and a configured self-hosted Civil3D2023 runner are available.

The source regression gate is `scripts/Validate-UserCommentCoverage.py`.
