# CE Tools active-comments runtime test plan

Review source: `CE Tools - Comments - 25-07-2026.docx`  
Branch: `followup/comments-2026-07-25`  
Draft PR: `#37`

## Test boundary

- Use disposable Civil 3D drawings and copies of real project templates.
- Test Civil 3D 2023 first, then Civil 3D 2024.
- Build Release x64 from the exact final commit.
- Back up any installed `CE Tools.bundle` before copying the new bundle.
- Do not merge PR #37 until applicable tests pass.
- A GitHub source-validation pass is not an Autodesk compilation or runtime pass.

## 1. Load and ribbon

1. Start Civil 3D and open a disposable DWG.
2. Confirm the `CE TOOLS` ribbon tab appears and is not blank.
3. Confirm panel, flyout and command labels begin with `CE -` where applicable.
4. Run `CE_RIBBONICONS` and test `TextOnly`, `Cached`, `Full`, then leave `Cached` selected.
5. Run `CE_TOOLSPALETTE`.
6. Move the window to a second monitor, search for several commands and execute one command from the window.
7. Close and reopen the window and confirm commands remain available.

Pass criteria:

- no ribbon-loading exception;
- no badly clipped command names at normal ribbon width;
- the floating window remains modeless and the normal ribbon remains available;
- search, icon fallback and command execution work.

## 2. Project Setup, styles and undo

Commands:

- `CE_PROJECTSETUP`
- `CE_PROJECTINFO`
- `CE_PROJECTCLEAR`
- `CE_PROJECTRESTORE`
- `CE_PROJECTSTYLES`
- `CE_PROJECTSTYLEINFO`
- `CE_PROJECTSTYLECLEAR`
- `CE_UNDOSETTINGS`
- `CE_UNDO`
- `CE_REDO`

Tests:

1. Enter all project fields in the single Project Setup popup.
2. Review and save, then place the optional project table.
3. Clear project information, run Undo, then test the dedicated restore command.
4. Open Project Style Centre and select Road, Stormwater, Sewer, Water and Platform style schedules.
5. Confirm drawing styles populate in the dropdowns.
6. Save and reopen the DWG; confirm selections remain.
7. Clear stored style selections and confirm Civil 3D styles themselves are not deleted.

## 3. Shared annotation and presentation

Commands:

- `CE_ANNOTSETTINGS`
- `CE_PRESENTATIONTOOLS`
- `CE_MAKEANNOTATIVE`
- `CE_TABLESCALE`
- `CE_OVERLAPFIX`
- `CE_PLREVERSE`
- `CE_COLOR250`
- `CE_CLEANUPUI`
- `CE_HATCHUI`

Tests:

1. Test text heights 1.8, 2.0 and 5.0.
2. Test MLeader, MText and COGO output.
3. Confirm marker circles remain small relative to text height.
4. Convert test text/dimensions/leaders to annotative objects.
5. Resize selected tables at multiple annotation scales.
6. Resolve an intentionally overlapping group of labels and tables.
7. Reverse several polylines and confirm linked direction arrows update.
8. Test Colour 250 for geometry only and geometry plus annotation.
9. Review Cleanup and Hatch popup options before applying actions.

## 4. Coordinates and survey

Commands:

- `CE_COORDPICK2`
- `CE_COORDCROSS2`
- `CE_COORDTABLE2`
- `CE_COORDREFRESH`
- `CE_COORDPOLY2`
- `CE_PLDIR`
- `CE_REFRESHALL`
- `CE_AUTOREFRESH`
- `CE_REFRESHSTATUS`

Tests:

1. Create MLeader, MText and COGO coordinate outputs.
2. Confirm wording is Point Name, X-Coordinate, Y-Coordinate and Z-Coordinate.
3. Confirm the old separate point-number column is absent.
4. Create a new linked table and append another point to an existing table.
5. Move COGO/AutoCAD points and refresh the register.
6. Create sequential COGO points from a polyline.
7. Move and grip-edit the source polyline and run `CE_REFRESHALL`.
8. Enable auto-refresh, modify a linked source and allow Civil 3D to return to idle.
9. Confirm source points, annotations and tables update without a recursive command loop.

## 5. Feature lines

Commands:

- `CE_FLREPORT2`
- `CE_FLAPPEARANCE`
- `CE_FLVERTEXLABELS`
- `CE_FLRAISEX`
- `CE_FLSURFACEUI`
- `CE_FLCONSTGRADE`
- `CE_FLREL`
- `CE_FLRELUPDATE`
- `CE_FLRELINFO`
- `CE_FLRELDETACH`

Tests:

1. Create the popup report and optional drawing table.
2. Change colour and assign selected feature lines to a Civil 3D site.
3. Annotate all vertices and confirm Point Name/X/Y/Z values.
4. Raise/lower multiple feature lines and confirm geometry changes—not merely a report.
5. Select a surface from the popup and assign elevations.
6. Apply constant grades between endpoints to multiple feature lines.
7. Create linked stepped offsets; edit/move the source and refresh relationships.
8. Inspect and detach relationships without deleting retained geometry.

## 6. Alignment, profile and surface

Commands:

- `CE_ALREPORTUI`
- `CE_ALSTOFF`
- `CE_ALLABELX`
- `CE_PROFILEREPORT2`
- `CE_PROFILEELEVATION2`
- `CE_PRLABELX`
- `CE_SURFACEREPORT2`
- `CE_SURFACEELEVATION2`
- `CE_SURFACECOMPARE2`
- `CE_SFLABELX`
- `CE_REBUILDALL`

Tests:

1. Create alignment reports, station/offset values and shared annotations.
2. Confirm 1.8/2.0/5.0 heights and marker-circle behaviour.
3. Show all profiles in a popup and optional table.
4. Select a profile in the popup and report/annotate station elevation and grade.
5. Show all surfaces, their styles and elevation ranges.
6. Select a surface in the popup and report/annotate X/Y/Z.
7. Compare two surfaces at a point and verify signed cut/fill difference.
8. Raise one comparison surface, rerun/refresh and confirm updated results.
9. Rebuild accessible surfaces/corridors and confirm failures are reported without crashing.

## 7. Corridors and parking

Commands:

- `CE_CORREPORTUI`
- `CE_CORBASEUI`
- `CE_CORLABELX`
- `CE_CORREBUILDX`
- `CE_PKROW`
- `CE_PKDOUBLE`
- `CE_PKREPORTUI`
- `CE_PKCOUNTX`
- `CE_PKNUMBER2`
- `CE_PKSKVALIDATE`
- `CE_PKSKCORRECT`
- `CE_PKSKCLEAR`

Tests:

1. Display corridor and baseline/region popup reports and optional tables.
2. Create corridor annotations with all shared heights and marker options.
3. Rebuild editable corridors and confirm `Corridor.Rebuild()` is called.
4. Create single and double parking rows.
5. Confirm every bay is its own closed four-sided polyline.
6. Count generated bays immediately.
7. Number bays with 1.8, 2.0 and 5.0 height.
8. Validate skew bays and confirm true perpendicular width.
9. Confirm 2500 mm bays pass and incorrect widths fail.
10. Create and clear reversible correction outlines.

## 8. Network data and service production

Commands:

- `CE_NETWORKREPORT2`
- `CE_NETWORKPARTREPORT2`
- `CE_SERVICEPROFILES`
- `CE_NETWORKDATA`
- `CE_SWSEQ`
- `CE_SWALIGN`
- `CE_SWPROFILE`
- `CE_SEWSEQ`
- `CE_SEWSEQMAIN`
- `CE_SEWALIGN`
- `CE_SEWFORMAT`
- `CE_SEWPROFILE`
- `CE_WATERSEQ`
- `CE_WATERALIGN`
- `CE_WATERPROFILE`
- `CE_WATERPLACE`
- `CE_WATERPLACEREFRESH`

Tests:

1. Show gravity and pressure network totals in a popup/table.
2. Select pipes, structures, fittings and appurtenances and verify part data.
3. Confirm actual service lengths are reported rather than default 1 m values.
4. Test automatic and selected-main stormwater sequencing.
5. Create/refresh stormwater alignments and profile views with parts displayed where supported.
6. Test sewer automatic and selected-main sequencing, styles, label spacing and profiles.
7. Test water sequencing, alignments and profiles.
8. Create water asset review markers and refresh them after alignment changes.
9. Confirm production information reports generated and missing objects clearly.

## 9. Road production

Commands:

- `CE_ROADPRODUCTION`
- `CE_ROADALIGN`
- `CE_ROADPROFILES`
- `CE_ROADCORRIDORS`
- `CE_ROADPRODUCTIONINFO`

Tests:

1. Select several open road polylines and create sequential road names.
2. Confirm original polylines remain and generated alignments receive project styles.
3. Test duplicate-name handling.
4. Select an EG surface and create profiles/profile views for all CE road alignments.
5. Verify view spacing, profile style, view style and band-set style.
6. Select an assembly and create corridors for every alignment/profile pair.
7. Rebuild created corridors.
8. Open production information and optional table.
9. Save/reopen and confirm CE tags remain discoverable.

## 10. BOQs, sewer excavation and reports

Commands:

- `CE_BOQCENTER`
- `CE_BOQBUILD`
- `CE_BOQREFRESH`
- `CE_BOQEXPORT`
- discipline BOQ exports
- `CE_SEWEREXCAVATION`
- `CE_SEWEREXCAVATIONREFRESH`
- `CE_SEWEREXCAVATIONINFO`
- `CE_SEWEREXCAVATIONEXPORT`
- `CE_REPORTCENTER`
- full and discipline report commands

Tests:

1. Build and refresh linked BOQs for every discipline.
2. Enter rates, modify source geometry and confirm matching rates remain after refresh.
3. Confirm Road and Platform quantities remain separate.
4. Export every discipline to Excel and open files without repair warnings.
5. Select sewer pipes and enter drawing-unit/trench assumptions.
6. Verify length, diameter, cover, width, depth, excavation, bedding and backfill values.
7. Change pipe length/size/cover and refresh the linked excavation table.
8. Review stored handles and assumptions.
9. Export sewer excavation to Excel.
10. Generate full and discipline reports with optional tables and Excel output.

## 11. Dynamic sections and intersections

Commands:

- `CE_XSCREATE`
- `CE_XSREFRESH`
- `CE_XSINFO`
- `CE_XSDETACH`
- `CE_XSMONITOR`
- `CE_INTCREATE`
- `CE_INTREFRESH`
- `CE_INTINFO`
- `CE_INTDETACH`
- `CE_INTMONITOR`

Tests:

1. Create a dynamic cross section through multiple design elements.
2. Move/grip-edit the section source and confirm refresh behaviour.
3. Check labels, dimensions, services and source status.
4. Create intersections from feature lines, corridors and curves.
5. Modify source geometry and refresh.
6. Test detach with keep/delete generated geometry choices.
7. Confirm save/reopen persistence and monitor status.

## 12. Production books, printing and output locations

Commands:

- `CE_PRODUCTIONCENTER`
- `CE_PRINTCENTER`
- `CE_PROJECTCLOSEOUT`
- `CE_CLIENTBOOK`
- `CE_CLIENTBOOKREFRESH`
- `CE_DRAWINGBOOK`
- `CE_BOOKINDEX`
- `CE_CLIENTBOOKINDEX`
- `CE_BATCHPUBLISH`
- `CE_OUTPUTLOCATION`

Tests:

1. Refresh model data before production.
2. Create project summary pages.
3. Create A4/A3 client books and A1/A0 construction layouts.
4. Modify project/design information and refresh the books.
5. Export both registers to Excel.
6. Open AutoCAD Publish and batch-publish selected generated layouts to PDF.
7. Open AutoCAD Plot for one current sheet.
8. Confirm `CE_OUTPUTLOCATION` accurately explains DWG layouts, Excel and PDF paths.

## 13. Typical details

Commands:

- `CE_DETAILTOOLS`
- `CE_DETAILSETROOT`
- `CE_DETAILSEARCH`
- `CE_DETAILINSERT`
- `CE_DETAILPARAMTOOLS`
- `CE_DETAILPARAMCREATE`
- `CE_DETAILPARAMEDIT`
- `CE_DETAILPARAMREFRESH`
- `CE_DETAILPARAMBOQ`
- `CE_DETAILPARAMBOQEXPORT`
- `CE_DETAILPARAMREVIEW`
- `CE_DETAILPARAMDETACH`
- `CE_DETAILPARAMCLEAR`

Tests:

1. Configure a disposable master-library folder.
2. Search DWG, DXF and PDF assets by category/keyword.
3. Insert an approved DWG detail and verify source file hash/time remain unchanged.
4. Create each generated dynamic type: Trench Drain, Pipe Trench, Valve Chamber, Kerb and Headwall.
5. Edit parameters and confirm geometry, tables and BOQ update.
6. Enter rates and confirm refresh preserves them.
7. Export BOQ to Excel.
8. Test Draft/For Review/Reviewed/Approved status and reset-to-Draft after edit.
9. Test detach/clear and Undo/Redo.
10. Save/reopen and confirm links remain.
11. Repeat visual/engineering checks when the remaining office source assets are uploaded.

## Evidence to save

For every failure record:

- exact command;
- Civil 3D version;
- complete command-line error;
- screenshot;
- whether failure occurred before or after selection;
- selected object type;
- DWG units and annotation scale;
- source and installed DLL SHA-256 values;
- whether Undo restored the previous state.
