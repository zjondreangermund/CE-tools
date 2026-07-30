# Typical Details Phase 3 — Civil 3D 2023/2024 Validation Plan

Validate the exact draft PR #36 head. GitHub checks source shape and host-independent geometry only; they do not compile or execute Autodesk assemblies.

## 1. Exact branch and source checks

```powershell
git fetch origin
git checkout followup/typical-details-dynamic
git pull --ff-only origin followup/typical-details-dynamic
$Head = git rev-parse HEAD
$Head
python scripts/Validate-CommandRegistry.py
python scripts/Validate-TypicalDetailsReview.py
python scripts/Validate-DynamicTypicalDetails.py
dotnet run --project tests/CE.Tools.Core.Tests/CE.Tools.Core.Tests.csproj -c Release
```

Confirm `$Head` exactly matches the current PR #36 head and PR #36 still targets `followup/typical-details-standards-review` as a draft.

## 2. Civil 3D 2023 build

Open **Windows PowerShell 5.1** in the repository root:

```powershell
Set-ExecutionPolicy -Scope Process Bypass -Force
.\scripts\Build-CE-Tools.ps1 -Version 2023 -Configuration Release
$Dll2023 = Resolve-Path '.\bundle\CE Tools.bundle\Contents\Windows\2023\CE.Tools.Civil3D.dll'
Get-FileHash $Dll2023 -Algorithm SHA256
```

Acceptance:

- Release x64 build completes without compiler errors;
- `DynamicTypicalDetailCommands.cs`, `DynamicTypicalDetailEngine.cs`, `DynamicTypicalDetailStorage.cs`, `RibbonIconCommands.cs`, `TypicalDetailsRibbonExtension.cs` and `RibbonVisuals.cs` compile against the installed 2023 assemblies;
- no duplicate AutoCAD command warning appears;
- record the PR head, DLL SHA-256, Civil 3D build, tester and date.

## 3. Install the exact 2023 bundle

Close Civil 3D, then run PowerShell as a user permitted to write to the target folder:

```powershell
$SourceBundle = Resolve-Path '.\bundle\CE Tools.bundle'
$TargetBundle = Join-Path $env:ProgramData 'Autodesk\ApplicationPlugins\CE Tools.bundle'
$BackupBundle = $TargetBundle + '.backup-' + (Get-Date -Format 'yyyyMMdd-HHmmss')
if (Test-Path $TargetBundle) { Move-Item $TargetBundle $BackupBundle }
Copy-Item $SourceBundle $TargetBundle -Recurse -Force
Get-FileHash (Join-Path $TargetBundle 'Contents\Windows\2023\CE.Tools.Civil3D.dll') -Algorithm SHA256
```

The installed hash must match the built DLL hash. Start Civil 3D 2023 and verify CE Tools loads from this bundle.

## 4. Ribbon compatibility and icon performance

Verify:

- one `CE TOOLS` tab appears;
- the existing panel layout and commands remain available;
- Standards & Details contains `Details Standards Review` and `Dynamic Typical Details` flyouts;
- flyout entries are `RibbonMenuItem` commands and execute correctly;
- no `RibbonRow` compatibility error occurs;
- icon failure cannot blank the tab.

Test each mode and restart timing:

```text
CE_RIBBONICONS
TextOnly
CE_RIBBONICONS
Cached
CE_RIBBONICONS
Full
CE_RIBBONICONS
Cached
```

Record cold-start and ribbon-display times for three starts in Cached mode and compare with Full. Cached is expected to render unique top-level icons once per session while sharing one small command icon. TextOnly must remain fully usable.

## 5. Create representative linked details

Run `CE_DETAILPARAMCREATE` for:

- TrenchDrain;
- PipeTrench;
- ValveChamber;
- Kerb;
- Headwall.

Use both millimetre drawings (`Drawing units per metre = 1000`) and metre drawings (`= 1`). Vary width, depth/height, length or plan thickness, wall/slab thickness, concrete specification, reinforcement, grating/cover and pipe diameter. Verify geometry, labels, dimensions, parameter table, quantity schedule, anchor link and source-template path/hash.

## 6. Parameter edit and refresh

For every detail type:

1. run `CE_DETAILPARAMEDIT` from the anchor, generated geometry and BOQ table;
2. change measurable and specification parameters;
3. confirm old CE-generated objects are replaced once;
4. confirm the review status resets to Draft;
5. run `CE_DETAILPARAMREFRESH`;
6. move to another layout/model-space context and verify regeneration stays in the anchor's owner space;
7. test Undo and Redo.

Unsupported or impossible dimensions must be rejected without partial output.

## 7. Source-template preservation and drift

Create a test DWG template and record its timestamp and SHA-256. Reference it during creation.

- confirm CE Tools never activates, writes, saves or normalises the template;
- edit or replace the external template and run refresh;
- confirm a hash-drift warning is shown and the detail returns to Draft;
- delete the template and confirm missing-source status is traceable;
- compare source timestamps and hashes before and after every CE command.

## 8. Quantity schedule and BOQ-ready linkage

For each type, independently hand-check:

- excavation volume;
- concrete volume;
- pipe/grating/kerb lengths or counts;
- bedding/backfill where applicable;
- headwall pipe-opening deduction;
- formwork where included.

Enter rates in the generated table, run `CE_DETAILPARAMBOQ`, and verify matching item-key rates are retained while quantities and amounts recalculate. Inspect the table extension dictionary and confirm `CE_DYNAMIC_DETAIL_BOQ_LINK` stores owner, detail identity, source identity, review status and item data. Treat reinforcement as a specification item, not a certified bar-bending schedule.

Run `CE_DETAILPARAMBOQEXPORT`. Microsoft Excel must open the `.xlsx` without a repair warning and values must match the drawing schedule.

## 9. Review, information, detach and clear

Test:

- `CE_DETAILPARAMREVIEW` with Draft, For Review, Reviewed and Approved (recorded);
- reviewer/reference and UTC timestamp persistence;
- `CE_DETAILPARAMINFO` from anchor, geometry and schedule;
- detach Keep: generated objects remain ordinary content and link records are removed;
- detach Delete: only the selected linked set is deleted;
- `CE_DETAILPARAMCLEAR` Selected and AllCurrentSpace;
- cancellation at every confirmation;
- stale/missing generated handles;
- locked output layers;
- save, close, reopen, AUDIT and PURGE.

No operation may erase unrelated drawing content or modify source templates.

## 10. Regression

Retest Phase 1 and Phase 2 Typical Details commands plus Parking, Dynamic Intersections, Surface, Water, Sewer, Stormwater, BOQ, Client Book, Bellmouth, Total Length and Total Area. Confirm the Civil 3D 2023 ribbon fallback still reports the first error rather than displaying a blank tab.

## 11. Civil 3D 2024 build and runtime

After 2023 passes, close Civil 3D and run:

```powershell
.\scripts\Build-CE-Tools.ps1 -Version 2024 -Configuration Release
$Dll2024 = Resolve-Path '.\bundle\CE Tools.bundle\Contents\Windows\2024\CE.Tools.Civil3D.dll'
Get-FileHash $Dll2024 -Algorithm SHA256
```

Install the exact updated bundle with the same backup/copy commands and repeat Sections 4–10 in Civil 3D 2024.

## Release boundary

Keep PR #36 draft, stacked above PR #35 and unmerged until the exact PR head compiles and passes this plan in Civil 3D 2023 and 2024. Generated variants and quantities are review aids, not automatic engineering approval, authority approval, certified reinforcement design or a certified payment BOQ.
