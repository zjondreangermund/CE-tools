# CE Tools Exact-Head Release Validation Runbook

This runbook begins only after all implementation batches are drafted and their
GitHub checks are green. It does not replace the command-specific Civil 3D test
plans in `docs`.

## Current stacked dependency order

Validate and later merge only in this order:

1. PR #18 — Batch 1
2. PR #19 — Batch 2
3. PR #20 — Batch 3
4. PR #21 — Batch 4
5. PR #22 — Batch 5
6. PR #23 — Batch 6
7. PR #25 — Batch 7

Do not merge a dependent PR before its base PR. Do not approve a PR after its
head commit changes until the exact new head has been rebuilt and retested.

## Prepare the Windows validation workstation

Required:

- Windows 10 or Windows 11.
- Civil 3D 2023 and Civil 3D 2024 installed.
- .NET 8 SDK.
- Python available as `python`.
- PowerShell.
- Git.
- Microsoft Excel for workbook validation.
- Office-approved PDF plot configuration, including PC3 and CTB/STB files.

Close Civil 3D before replacing an installed CE Tools bundle.

## Check out the exact release-candidate head

From the repository root:

```powershell
git fetch --all --prune
git checkout comments-2026-07-23-batch-7
git pull --ff-only
git status --short
git rev-parse HEAD
```

The working tree must be clean. Record the displayed commit SHA in the release
validation report and compare it to PR #25 before testing.

## Run the complete validation harness

```powershell
Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass -Force
.\scripts\Invoke-CE-Tools-ReleaseValidation.ps1 `
  -Version All `
  -Configuration Release
```

The harness:

1. requires a clean Git working tree;
2. records the branch and exact commit SHA;
3. runs all source validators;
4. audits AutoCAD command names for duplicate declarations;
5. runs the host-independent geometry tests;
6. verifies Civil 3D 2023 and 2024 managed assemblies;
7. builds the plugin separately for 2023 and 2024;
8. verifies the expected bundle DLLs;
9. creates a timestamped application-bundle snapshot;
10. calculates SHA-256 hashes; and
11. writes `RELEASE_VALIDATION_REPORT.md` with the remaining manual checks.

Default output:

```text
artifacts\release-validation\yyyyMMdd-HHmmss\
```

Important generated files:

```text
RELEASE_VALIDATION_REPORT.md
SHA256SUMS.txt
logs\
bundle-snapshot\
```

## Workstation path overrides

Use explicit installation roots when Civil 3D is not installed in the default
locations:

```powershell
.\scripts\Invoke-CE-Tools-ReleaseValidation.ps1 `
  -Version All `
  -AutoCAD2023Root "D:\Autodesk\AutoCAD 2023" `
  -AutoCAD2024Root "D:\Autodesk\AutoCAD 2024"
```

Build only one host version when diagnosing a compiler issue:

```powershell
.\scripts\Invoke-CE-Tools-ReleaseValidation.ps1 `
  -Version 2024 `
  -AutoCAD2024Root "C:\Program Files\Autodesk\AutoCAD 2024"
```

Run source and core checks without Autodesk compilation only for diagnosing the
validation harness itself:

```powershell
.\scripts\Invoke-CE-Tools-ReleaseValidation.ps1 `
  -Version All `
  -SkipCivilBuild `
  -SkipInstallSnapshot
```

A `-SkipCivilBuild` result is not release approval.

## Install the exact bundle snapshot

After both builds pass, install from the generated `bundle-snapshot` rather
than rebuilding or copying a different working tree.

Keep the SHA-256 file with the tested snapshot. Any DLL change invalidates the
runtime results and requires a new validation run.

## Civil 3D runtime sequence

Use copies of representative production drawings. Complete the full sequence
in Civil 3D 2023 and then repeat it in Civil 3D 2024.

### Load and ribbon

- Start Civil 3D with no CE Tools DLL manually NETLOADed from another folder.
- Confirm the application bundle loads once.
- Confirm no duplicate command-name errors appear.
- Confirm the CE Tools ribbon is neat and contains all Batch 1–7 flyouts.
- Confirm Bellmouth Densifier, Total Length and Total Area remain present.

### Batch plans

Complete every applicable test in:

- `REVIEW_COMMENTS_BATCH_1_TEST_PLAN.md`
- `REVIEW_COMMENTS_BATCH_2_TEST_PLAN.md`
- `REVIEW_COMMENTS_BATCH_3_TEST_PLAN.md`
- `REVIEW_COMMENTS_BATCH_4_TEST_PLAN.md`
- `REVIEW_COMMENTS_BATCH_5_TEST_PLAN.md`
- `REVIEW_COMMENTS_BATCH_6_TEST_PLAN.md`
- `REVIEW_COMMENTS_BATCH_7_TEST_PLAN.md`

Record the DWG file, Civil 3D version, exact plugin SHA, tester, date and result.

### High-risk exact-head checks

Pay special attention to:

- repeated linked coordinate-table refresh;
- repeated linked BOQ refresh and rate preservation;
- AutoCAD Table merge/unmerge behaviour;
- COGO point creation and one-step Undo;
- feature-line editing and surface assignment;
- corridor rebuild API behaviour;
- Civil 3D pipe and structure quantity reflection;
- dynamic-section event queuing and idle refresh;
- multiple open drawings and document switching;
- section-line grip edits;
- source-surface edits;
- deleted or stale linked objects;
- drawing close while refresh is pending;
- Excel `.xlsx` files opening without repair warnings; and
- A4, A3, A1 and A0 publishing with approved plot configurations.

## Failure handling

When any compiler or runtime test fails:

1. mark the exact step as failed in the generated report;
2. capture the full command line, exception, Civil 3D version and DWG;
3. do not merge any affected PR;
4. fix the defect on the appropriate batch branch;
5. allow dependent branches to be restacked only after the fix is stable;
6. rerun GitHub checks; and
7. rerun exact-head Civil 3D validation.

Do not approve based on a previous SHA.

## Release approval gate

A merge is allowed only after all are true:

- Civil 3D 2023 Release compilation passed.
- Civil 3D 2024 Release compilation passed.
- Runtime plans passed in both versions.
- Excel validation passed.
- PDF publishing validation passed.
- Exact PR heads match the validated dependency stack.
- All PRs are mergeable.
- No later commit invalidated an earlier result.
- Approval is recorded before merging in dependency order.
