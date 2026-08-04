# CE Tools V61 Civil 3D 2023 Release Validation Runbook

This runbook begins only after all implementation batches are drafted and their
GitHub checks are green. It does not replace the command-specific Civil 3D test
plans in `docs`.

The repository `main` branch is the release source. Do not install a working-tree
build that differs from the committed source SHA recorded in the package manifest.

## Prepare the Windows validation workstation

Required:

- Windows 10 or Windows 11.
- Civil 3D 2023 installed.
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
git checkout main
git pull --ff-only
git status --short
git rev-parse HEAD
```

The working tree must be clean. Record the displayed commit SHA and compare it
with `RELEASE-MANIFEST.json` in the generated V61 package.

## Run the complete validation harness

```powershell
Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass -Force
.\scripts\Invoke-CE-Tools-ReleaseValidation.ps1 `
  -Version 2023 `
  -Configuration Release
```

The harness:

1. requires a clean Git working tree;
2. records the branch and exact commit SHA;
3. runs all source validators;
4. audits AutoCAD command names for duplicate declarations;
5. runs the host-independent geometry tests;
6. verifies Civil 3D 2023 managed assemblies;
7. builds the Civil 3D 2023 plugin;
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

Use an explicit installation root when Civil 3D 2023 is not installed in the
default location:

```powershell
.\scripts\Invoke-CE-Tools-ReleaseValidation.ps1 `
  -Version 2023 `
  -AutoCAD2023Root "D:\Autodesk\AutoCAD 2023"
```

Build the supported release host when diagnosing a compiler issue:

```powershell
.\scripts\Invoke-CE-Tools-ReleaseValidation.ps1 `
  -Version 2023 `
  -AutoCAD2023Root "C:\Program Files\Autodesk\AutoCAD 2023"
```

Run source and core checks without Autodesk compilation only for diagnosing the
validation harness itself:

```powershell
.\scripts\Invoke-CE-Tools-ReleaseValidation.ps1 `
  -Version 2023 `
  -SkipCivilBuild `
  -SkipInstallSnapshot
```

A `-SkipCivilBuild` result is not release approval.

## Install the exact bundle snapshot

After the build passes, install from the generated `bundle-snapshot` rather
than rebuilding or copying a different working tree.

Keep the SHA-256 file with the tested snapshot. Any DLL change invalidates the
runtime results and requires a new validation run.

## Civil 3D runtime sequence

Use copies of representative production drawings and complete the sequence in
Civil 3D 2023.

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
- Civil 3D 2023 runtime plans passed.
- Excel validation passed.
- PDF publishing validation passed.
- The package manifest source commit matches the validated `main` commit.
- `CE_INSTALLVERIFY` passes in the loaded Civil 3D 2023 session.
- No later commit invalidated the tested package.
