# September 29 image comments and band sources

These changes address the common-assembly, junction midpoint, multiple-profile/corridor, pipe reference alignment, and empty band-source results shown in the field screenshots. The commands are available from **Field Completion**.

| Request | Command | Behavior |
| --- | --- | --- |
| Select a complete Common Assembly inside CE Tools | `CE_ASSEMBLYCREATE` | Reads installed Civil 3D ATC catalogs and imports the chosen assembly with its subassemblies. Shows the catalog's units. Also offers an assembly DWG and an empty custom assembly. |
| Preserve the road crown on junction closures | `CE_ROADJUNCTIONBATCH` | Creates both edge controls and a protected midpoint on each straight closure/limit segment. Feature-line output can sample and write vertices to multiple selected editable TIN surfaces. |
| Repair existing junction feature lines | `CE_ROADJUNCTIONFEATURELINESTOP` | Adds a missing midpoint to straight two-PI connectors, samples elevations, and writes the common control elevations to the selected surfaces that cover each point. Curved bellmouths are retained. |
| Assign several design profiles to several corridors | `CE_CORRIDORPROFILESMULTI` | Updates matching alignment baselines or explicitly adds every selected profile as a baseline in every selected corridor. New baselines receive a selected assembly region over the profile's station range. |
| Fill the pipe's Reference Alignment property | `CE_PIPEALIGNMENTMULTI` | Assigns and verifies the selected alignment on selected pipes/structures. Newly generated branches also bind their parts to the new alignment. A shared structure's existing reference is retained during branch generation. |
| Import bands and populate each view's sources | `CE_PROFILEVIEWDATASOURCES` | Defaults to the full band-set import workflow, followed by source matching for each view's alignment. Also offers repair of existing road bands and manual source selection. |

## Band-set repair

The screenshot showed eleven processed views but zero written/verified sources. Merely opening the data-source command did not import any band set, and keeping Profile 1 while choosing Same as Profile 1 for Profile 2 could make the old command do nothing.

The default action now reuses `CE_ROADBANDLABELS`: select the required native band-set style, import/commit each view independently, materialize and verify native band rows, prepare any compatibility profiles, bind each view's own road profiles, and refresh labels. Existing band-set styles must be present in the drawing.

**Match each road view's existing bands** repairs the sources without importing another style. Missing rows, missing road profiles, and unresolved source assignments produce an explicit per-view message.

Manual mode maps a selected profile to the corresponding profile on each destination alignment. Same as Profile 1 uses the row's actual Profile 1 even when that field is kept unchanged. Both profile fields are verified from persisted band collections. Gravity-network sources apply only to gravity Pipe Network rows. A view with no compatible assignments is reported as failed instead of successful with zero writes.

## Civil 3D acceptance checks

Run these checks on a copy of the field drawing after building/installing the updated source. Automated checks do not replace these native checks.

1. **Assembly:** choose Basic Assembly and then Primary Road Full Section from the installed catalogs. Confirm the inserted native assemblies contain subassemblies, retain their classification, and can be selected for a corridor region. Check metric/imperial labels, insertion under a rotated UCS, and a saved DWG containing more than one assembly.
2. **Junction:** create a cross junction with feature-line output and select two road TOP surfaces. Confirm each straight cross-road connector has edge/midpoint/edge controls, the midpoint has the sampled crown elevation, and the destination surfaces use the same control elevations where they overlap. Repair an existing two-PI connector, rerun, and confirm the midpoint is not duplicated. Curved returns must remain curved. An unresolved control must prevent that line from writing vertices to any destination surface.
3. **Corridors:** select multiple FG profiles and corridors. In Update mode, confirm only baselines with the same alignment can be assigned. When two selected profiles share an alignment, the default is to skip until one is chosen. A profile shorter than an existing region must roll back that corridor. In Add mode, verify the selected assembly and station range, configure required corridor targets, and rerun to confirm an existing alignment/profile pair is not duplicated.
4. **Pipe alignment:** assign Branch-1 to P1.1 and confirm Properties > Reference Alignment shows Branch-1. Generate new branch alignments and confirm pipe references are set; a shared structure with an existing reference must retain it. Pipe geometry and surface references should remain unchanged.
5. **Bands:** select the eleven profile views from the screenshot, run `CE_PROFILEVIEWDATASOURCES`, and choose the default import action. Select the required band set and confirm Bands tab rows, Profile 1/Profile 2 sources, and visible labels on every view. Source profiles must belong to each view's alignment. Save/reopen and check persistence. Test the repair action on existing bands, and manual mode with Profile 1 kept and Profile 2 set to Same as Profile 1. A view with no rows or compatible sources must report a problem.

## Verification recorded for this change

- Assembly catalog behavior: 8 checks (real assembly tools only, GUID parsing, names, units, duplicate catalogs/names, malformed XML, and external-entity rejection).
- C# syntax parser across the Civil 3D source tree.
- Existing Civil 3D API contract, plan-junction geometry, and core behavior checks: 18, 4, and 16 checks respectively.
- Related current-source validators: September 16 field comments, September 18 field retest, September 25 road/sewer/profile fixes, September 25 band labels, command registry, and undo-group pollution checks.
- Native Autodesk build, DWG execution, and the Windows staging pipeline were not available in the Linux editing environment. They remain to be checked on the Civil 3D workstation.

The following older source-pattern validators also fail on unchanged base commit `076f078f8b129828adec89dce42e625063d944e9`: `Validate-RoadAssemblyProduction.py`, `Validate-ProductionStyleAssemblyBands.py`, `Validate-September18RoadJunctionCompletion.py`, and `Validate-CECommandWiring.py`. Their failures were compared with an untouched worktree; this change does not weaken those validators.
