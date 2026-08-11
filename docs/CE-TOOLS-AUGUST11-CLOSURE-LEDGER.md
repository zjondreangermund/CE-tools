# CE Tools — August 11, 2026 Comment Closure Ledger

This ledger closes the August 11 field-test comments at the **source / staged-build implementation level**.

Status meanings:

- **CODE/STAGING COMPLETE** — implementation exists in `main` and/or is deliberately applied by the Civil 3D 2023 staging pipeline before MSBuild.
- **HOST ACCEPTANCE REQUIRED** — Autodesk Civil 3D 2023 must still compile/load/run the feature on the user's workstation. This is not a code TODO; it is the final acceptance gate.
- **EXTERNAL INPUT REQUIRED** — exact output cannot be reproduced from a screenshot without the actual owner-supplied source file/template.

## 1. Platform Production

**CODE/STAGING COMPLETE**

- Platform Slopes out-of-range crash repaired by replacing unsafe `AllPoints` numeric-index elevation writes with point-based feature-line elevation updates in the Civil 3D 2023 compatibility stage.
- Stepped-offset child elevation transfer receives the same point-based repair.
- Existing linked platform refresh remains active after slope/drape/source updates.

**HOST ACCEPTANCE REQUIRED**

- Re-test `CE_PLATFORMSLOPE` on closed platform feature lines containing PI/elevation/arc points.

## 2. Survey / COGO / Setting-Out

**CODE/STAGING COMPLETE**

- Immediate post-setting-out COGO style application from Project Style Centre.
- Immediate Vertex Setting-Out refresh after the generating command ends; no manual refresh should be required for the first correct display.
- Universal dynamic refresh continues to update linked Vertex Setting-Out and feature-line reports after CE commands, MOVE/STRETCH/grip changes.
- Linked COGO outputs update Easting, Northing and Elevation when their source feature line changes.
- Initial COGO label offsets are stored immediately after first setting-out refresh.
- `CE_COGOLABELRESTOREINITIAL` restores All/Selected COGO labels without changing COGO coordinates.
- Generic `CE_ANNOTATIONRESTORE` also includes stored COGO initial positions.
- COGO overlap compatibility repair keeps clear labels fixed, bounds movement, removes the old farthest-candidate fallback and respects selected-point scope.
- Junction setting-out now routes through the general Vertex Setting-Out engine so selected polylines and arcs are accepted.
- `CE_COORDMULTISURFACETABLE` supports multiple selected Civil 3D surfaces before table placement and stores live source handles for refresh.
- Multi-surface coordinate tables are included in universal refresh.
- Survey Location / town coordinate-system assignment synchronizes Project Information metadata.
- Existing Namibia LO/WGS84 coordinate work remains in the project.

**HOST ACCEPTANCE REQUIRED**

- Verify current COGO style, label proximity and initial-position restore in the field drawing.
- Move/change a linked feature line and verify COGO + table change together.

## 3. Tables / Source Navigation / PDF

**CODE/STAGING COMPLETE**

- Legacy `CE_TABLESOURCEZOOM` delegates to robust `CE_TABLECELLZOOM`.
- When Civil 3D resolves the clicked table cell, that row's linked source becomes the default target.
- If Civil 3D 2023 cannot hit-test a transformed/annotative table reliably, a popup still lists all live linked source entities by object type, name, layer and handle.
- Source navigation supports selecting one linked pipe/structure/feature line/alignment/profile/etc. or all linked sources.
- Linked tables remain in universal refresh.
- Existing PDF-to-DWG workflow uses an Open File popup before import/conversion.

**HOST ACCEPTANCE REQUIRED**

- Verify table hit-testing on the user's transformed/annotative production tables.

## 4. Junctions / Bellmouths

**CODE/STAGING COMPLETE**

- Cross/T-junction setting-out groups one complete junction before moving to the next.
- Legacy arc-only `CE_ROADJUNCTIONSETTINGOUT` is staged to the new grouped all-Curve workflow.
- Bellmouth quarter-arc construction no longer uses hard-coded global angle quadrants after the local side-road axis is flipped; start/end angles are derived from the actual local tangent endpoints.
- `CE_JUNCTIONTRIMBOUNDARIES` creates closed non-plot junction trim boundaries for multiple junctions and can hand them directly to `CE_TRIMINSIDEMULTI`.
- `CE_BELLMOUTHTRIMEDGES` projects actual bellmouth arc start/end tangent points onto multiple road-edge / shoulder curves, splits them at tangent stations and removes only the portions through each junction.
- Road Completion and Road Production expose bellmouth tangent trim.

**HOST ACCEPTANCE REQUIRED**

- Confirm corrected arcs overlay the intended 3 m road-edge geometry in the field drawing.
- Confirm multi-junction edge/shoulder trimming retains all outside pieces.

## 5. Route Planner / Preliminary Road Layout

**CODE/STAGING COMPLETE**

- `CE_ROADCONTINUITYFIX` joins small road-centreline gaps and prefers straight-through continuation at crossings.
- Existing Road Layout Production retains road edges, shoulders/sidewalk offsets, bulk T/cross junctions, road names and road dimensions.
- `CE_ROADOUTSIDEOFFSET` automatically chooses the offset farther from the nearest CE road centreline for road-edge / shoulder source geometry.
- `CE_ROUTEHORIZONTALCURVES` applies multiple tangent horizontal curves with requested radius and locally shortens a curve only where adjacent tangent geometry cannot fit it safely.
- `CE_ROUTEANNOTATIONSTYLE` exposes paper text heights 1.8 / 2.0 / 2.5 / 3.5 / 5.0, mask on/off, paper arrow size and metre dimension suffix.
- `CE_ROUTESHIFTANNOTATION` moves selected text/dimensions/leaders together to resolve layout overlap.
- `CE_POLYLINEARCS` creates true Arc entities from curved lightweight-polyline segments.
- `CE_UTILITYROUTEOFFSET` creates discipline-aware Stormwater/Sewer/Water/Bulk-Water routes at selected offsets from erf boundaries, road-reserve edges, road centrelines or other selected curves.

**HOST ACCEPTANCE REQUIRED**

- Confirm centreline joining chooses the intended through-road on unusual skew intersections.
- Confirm requested horizontal curve radii and annotation scales in model/paper space.

## 6. Roads — Alignments / Profiles / Styles / Corridors / Names

**CODE/STAGING COMPLETE**

- Road production resolves selected Profile Style, Profile View Style and Profile View Band Set and applies the resolved IDs to generated profile views.
- Existing Civil 3D 2023 style auto-import stage imports the bundled road profile/band source when the requested/default road band library is missing.
- Default road band-set preference remains `Road-Single-Band Set 1-Full Grid` where no explicit road preset is saved.
- Road-style fallback repair prevents utility styles from being blindly selected for roads.
- Separate discipline style presets are now stored in `PROJECT_STYLE_PRESET_<DISCIPLINE>` records for Roads, Stormwater, Sewer, Water, Platforms, Bulk Water, Parking and Flood.
- Saving Project Style Centre automatically snapshots that discipline's selection.
- Entering a guided Production Centre activates that discipline's stored preset while all presets continue to reference the same Civil 3D style catalogue.
- `CE_ROADPROFILEFULL` now runs EG/profile-view creation, editable final PVI design profile and `CE_ROADVERTICALCURVES`.
- `CE_ROADVERTICALCURVES` adds free symmetric parabolic vertical curves to eligible internal PVIs, shortens them where adjacent tangent spacing requires it and skips end/already-curved PVIs.
- Corridor Completion retains baseline/region/target/surface rebuild logic and now attempts to restore hidden corridor visibility and mark graphics modified.
- Main Corridors ribbon `Create / Rebuild Baselines and Regions` points to corridor production (`CE_ROADCORRIDORCOMPLETE`); the old popup is retained separately as `Baseline / Region Report`.
- `CE_ROADNAMESYNC` propagates ROAD-n naming into nearby Civil road alignment names and linked profile/corridor/section/assembly names and stores a road-name link record.
- Universal refresh calls road-name synchronization so ROAD-4 / RD-02-type mismatches do not remain static after refresh.

**HOST ACCEPTANCE REQUIRED**

- Confirm the user's road profile-view style/band library resolves to the intended Road preset.
- Review generated vertical-curve lengths against project/authority standards before design issue.
- Confirm a corridor previously visible only in Prospector is displayed after complete corridor rebuild.

## 7. Networks — Sewer / Stormwater / Water / Bulk Water

**CODE/STAGING COMPLETE**

- Legacy `CE_NETWORKFROMPOLYLINES` delegates to the multi-source batch manager.
- `CE_NETWORKFROMPOLYLINESBATCH` allows the user to select many source line/polyline/feature-line objects once and feeds them into the native Civil network-from-object command sequentially.
- Successfully completed sources are marked by discipline; same-discipline reruns skip completed CE source geometry by default to prevent accidental duplicate network creation.
- Markers can be intentionally cleared or sources can be processed again when requested.
- Legacy `CE_NETWORKCONNECT` delegates to selected multi-part connection handling.
- `CE_NETWORKCONNECTSELECTED` accepts multiple selected pipe/structure parts and hands the complete set into CE's connection/open-end workflow.
- `CE_CLOSEPIPESONLY` is explicitly separate from `CE_BOQREFRESH`.
- Staging scans visible Close Pipe(s) actions and rewrites any historical Close Pipes → `CE_BOQREFRESH` misrouting to `CE_CLOSEPIPESONLY` while leaving legitimate BOQ refresh buttons unchanged.
- Utilities ribbon exposes multi-source network creation, utility route offset and close/connect selected pipes.

**HOST ACCEPTANCE REQUIRED**

- Civil 3D native network dialogs still need one acceptance per queued source; verify the batch advances after each native command finishes.
- Verify the user's specific pipe/structure connection topology with selected multi-part connection handling.

## 8. Sewer Midblock / Road-Reserve Production

**CODE/STAGING COMPLETE**

- Route Planner Option 2 is staged to `CE_MIDBLOCKSEWERPRODUCTION` instead of the older one-short-line-per-erf layout.
- Adjacent cadastral erfs are grouped into rows.
- One continuous route is created per row.
- Route side can be selected explicitly or chosen as the low side from a selected Civil 3D surface.
- Planning manhole diameter default = 1.2 m.
- Maximum planning spacing = 60 m / 80 m / custom.
- Preferred manhole location = approximately 1.5 m from nearby erf corners while maintaining the maximum selected interval.
- Equivalent configurable utility routing from erf / road-reserve / road-centre geometry is available through `CE_UTILITYROUTEOFFSET`.
- Sewer Production Centre places continuous Midblock / Road-Reserve routing at the Prepare stage before network creation.

**HOST ACCEPTANCE REQUIRED**

- Confirm row clustering and low-side selection on the actual cadastral layout and surveyed surface.
- Verify planning manhole shifts against design standards, servitudes and final hydraulic design.

## 9. CE Production Centre / Workflow / Welcome / Themes

**CODE/STAGING COMPLETE**

- Dedicated `CE PRODUCTION` ribbon tab.
- `CE_WELCOME` two-choice home screen:
  1. CE-PRODUCTION CENTRE — shortest guided production routes.
  2. CE-ENGINEERING INTELLIGENCE CENTRE — full CE Tools workflow/command library.
- Main CE TOOLS ribbon also exposes `CE Tools Home`.
- Saved CE window Dark / Light preference.
- Production disciplines:
  - Project
  - Survey
  - Platform
  - Road
  - Stormwater
  - Sewer
  - Water
  - Bulk Water
  - Parking Area
  - Flood
- Discipline centres place Settings first and organize the important commands into Prepare → Create → Design → Complete → Deliver plus `RUN COMPLETE <DISCIPLINE> PRODUCTION`.
- Full command inventory remains available through Engineering Intelligence / existing workflow centre rather than being duplicated into Production Centre.
- Discipline style preset management is exposed next to Project Style Centre.

**HOST ACCEPTANCE REQUIRED**

- Verify ribbon layout/spacing and welcome-window appearance at the user's Civil 3D DPI/theme settings.

## 10. Build / Staging Safeguards

**CODE/STAGING COMPLETE**

- `Inject-August11FieldCompletion-Civil3D2023.ps1` wires the primary August 11 runtime/legacy-command integrations.
- Completion passes 2–4 wire final route/network/road/profile/style/bellmouth/ribbon mappings.
- `Repair-August11-FieldCompilerCompatibility-Civil3D2023.ps1` runs all final completion passes, prevents the new network hub from colliding with the established `CE_NETWORKMULTI`, normalizes host-sensitive source and invokes the focused final validator.
- `Validate-August11FieldCompletion.ps1` validates the main field-comment closure.
- `Validate-August11FieldCompletion2.ps1` parses every `*August11*.ps1` file and validates the final bellmouth, initial COGO, main-ribbon, style-preset, vertical-curve and corridor-visibility hooks.
- Windows GitHub Actions workflow `.github/workflows/august11-field-completion-validation.yml` runs the disposable staged-source validation without requiring Civil 3D installation.
- The actual one-click Civil 3D 2023 build still remains the authoritative Autodesk compiler/runtime acceptance gate.

## 11. External Template Dependency

**EXTERNAL INPUT REQUIRED**

The earlier request to reproduce the exact internal row/formula structure of the owner-supplied **Annexure A / Asset Register** cost-estimate workbook cannot be completed exactly from a WhatsApp screenshot alone. CE Tools already supports selecting an approved XLSX/XLSM template. The actual workbook file is required to reproduce its exact formulas, macros, hidden sheets, named ranges and row structure safely.

## Final status

**All August 11 screenshot/field comments are now closed at the CE Tools source/staging implementation level.**

They are **not yet marked Autodesk host-accepted**. The next gate is a clean Civil 3D 2023 build/install followed by targeted field acceptance of Platform Slopes, COGO/setting-out, junction/bellmouth geometry, road profiles/corridors, batch networks and Midblock sewer routing.
