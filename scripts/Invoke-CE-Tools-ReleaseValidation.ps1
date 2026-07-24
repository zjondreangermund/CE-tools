[CmdletBinding()]
param(
    [ValidateSet("2023", "2024", "All")]
    [string]$Version = "All",

    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",

    [string]$AutoCAD2023Root = "C:\Program Files\Autodesk\AutoCAD 2023",

    [string]$AutoCAD2024Root = "C:\Program Files\Autodesk\AutoCAD 2024",

    [string]$OutputDirectory,

    [switch]$SkipCivilBuild,

    [switch]$SkipInstallSnapshot
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repositoryRoot "artifacts\release-validation\$timestamp"
}

$logDirectory = Join-Path $OutputDirectory "logs"
$snapshotDirectory = Join-Path $OutputDirectory "bundle-snapshot"
New-Item -ItemType Directory -Path $logDirectory -Force | Out-Null

$results = New-Object System.Collections.Generic.List[object]

function Add-Result {
    param(
        [string]$Stage,
        [string]$Target,
        [string]$Status,
        [string]$Details,
        [string]$LogPath = ""
    )

    $results.Add([pscustomobject]@{
        Stage = $Stage
        Target = $Target
        Status = $Status
        Details = $Details
        LogPath = $LogPath
    })
}

function Invoke-LoggedCommand {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Stage,

        [Parameter(Mandatory = $true)]
        [string]$Target,

        [Parameter(Mandatory = $true)]
        [string]$LogName,

        [Parameter(Mandatory = $true)]
        [scriptblock]$Command
    )

    $logPath = Join-Path $logDirectory $LogName
    Write-Host "[$Stage] $Target" -ForegroundColor Cyan
    try {
        & $Command *>&1 | Tee-Object -FilePath $logPath
        if ($LASTEXITCODE -ne 0) {
            throw "Command returned exit code $LASTEXITCODE."
        }
        Add-Result -Stage $Stage -Target $Target -Status "PASS" -Details "Completed successfully." -LogPath $logPath
    }
    catch {
        Add-Result -Stage $Stage -Target $Target -Status "FAIL" -Details $_.Exception.Message -LogPath $logPath
        throw
    }
}

function Assert-CommandAvailable {
    param([string]$Name)
    if ($null -eq (Get-Command $Name -ErrorAction SilentlyContinue)) {
        throw "Required command '$Name' is not available in PATH."
    }
}

function Get-GitValue {
    param([string[]]$Arguments)
    try {
        $value = & git -C $repositoryRoot @Arguments 2>$null
        if ($LASTEXITCODE -eq 0) {
            return ($value -join "`n").Trim()
        }
    }
    catch {
        return ""
    }
    return ""
}

function Test-CivilRoot {
    param(
        [string]$Year,
        [string]$Root
    )

    if (-not (Test-Path $Root)) {
        Add-Result -Stage "Prerequisite" -Target "Civil 3D $Year" -Status "FAIL" -Details "Installation root was not found: $Root"
        return $false
    }

    foreach ($assembly in @("AcMgd.dll", "AcDbMgd.dll", "AcCoreMgd.dll")) {
        if (-not (Test-Path (Join-Path $Root $assembly))) {
            Add-Result -Stage "Prerequisite" -Target "Civil 3D $Year" -Status "FAIL" -Details "$assembly was not found below the expected AutoCAD root: $Root"
            return $false
        }
    }

    $civilAssembly = Get-ChildItem -Path $Root -Filter "AeccDbMgd.dll" -File -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
    $aecAssembly = Get-ChildItem -Path $Root -Filter "AecBaseMgd.dll" -File -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($null -eq $civilAssembly -or $null -eq $aecAssembly) {
        Add-Result -Stage "Prerequisite" -Target "Civil 3D $Year" -Status "FAIL" -Details "Civil 3D or AEC managed assemblies were not found below: $Root"
        return $false
    }

    Add-Result -Stage "Prerequisite" -Target "Civil 3D $Year" -Status "PASS" -Details "AutoCAD, Civil 3D and AEC managed assemblies were found."
    return $true
}

function Invoke-CivilBuild {
    param(
        [string]$Year,
        [string]$Root
    )

    $script = Join-Path $PSScriptRoot "Build-CE-Tools.ps1"
    Invoke-LoggedCommand `
        -Stage "Civil build" `
        -Target "Civil 3D $Year" `
        -LogName "build-$Year.log" `
        -Command {
            & $script -Version $Year -Configuration $Configuration -AutoCADRoot $Root
        }

    $bundleFolder = Join-Path $repositoryRoot "bundle\CE Tools.bundle\Contents\Windows\$Year"
    $assembly = Join-Path $bundleFolder "CE.Tools.Civil3D.dll"
    if (-not (Test-Path $assembly)) {
        Add-Result -Stage "Bundle verification" -Target "Civil 3D $Year" -Status "FAIL" -Details "Expected plugin assembly is missing: $assembly"
        throw "Civil 3D $Year bundle verification failed."
    }

    $hash = (Get-FileHash -Path $assembly -Algorithm SHA256).Hash
    Add-Result -Stage "Bundle verification" -Target "Civil 3D $Year" -Status "PASS" -Details "CE.Tools.Civil3D.dll SHA256=$hash"
}

function Write-Report {
    param(
        [string]$Commit,
        [string]$Branch,
        [string]$WorkingTree
    )

    $reportPath = Join-Path $OutputDirectory "RELEASE_VALIDATION_REPORT.md"
    $lines = New-Object System.Collections.Generic.List[string]
    $lines.Add("# CE Tools Release Validation Report")
    $lines.Add("")
    $lines.Add("- Generated: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss zzz')")
    $lines.Add("- Branch: $Branch")
    $lines.Add("- Commit: $Commit")
    $lines.Add("- Configuration: $Configuration")
    $lines.Add("- Requested Civil 3D version: $Version")
    $lines.Add("- Working tree: $WorkingTree")
    $lines.Add("")
    $lines.Add("## Automated results")
    $lines.Add("")
    $lines.Add("| Stage | Target | Status | Details |")
    $lines.Add("|---|---|---:|---|")
    foreach ($result in $results) {
        $details = ($result.Details -replace "\|", "\\|")
        $lines.Add("| $($result.Stage) | $($result.Target) | $($result.Status) | $details |")
    }
    $lines.Add("")
    $lines.Add("## Mandatory Civil 3D runtime checks")
    $lines.Add("")
    foreach ($item in @(
        "Load the exact built DLL in Civil 3D 2023 and confirm the CE Tools ribbon loads without duplicate-command errors.",
        "Repeat in Civil 3D 2024.",
        "Run the Batch 1 through Batch 7 manual test plans on copies of representative drawings.",
        "Validate automatic cross-section refresh after source-line grip edits and after surface/design-object edits.",
        "Validate linked BOQ and coordinate-table refresh, including repeated refresh and Undo.",
        "Open generated XLSX files in Microsoft Excel and confirm no repair warning.",
        "Publish A4, A3, A1 and A0 layouts using office-approved PC3 and CTB/STB settings.",
        "Record all defects before any stacked PR is merged."
    )) {
        $lines.Add("- [ ] $item")
    }
    $lines.Add("")
    $lines.Add("## Release decision")
    $lines.Add("")
    $lines.Add("- [ ] Civil 3D 2023 compile passed")
    $lines.Add("- [ ] Civil 3D 2024 compile passed")
    $lines.Add("- [ ] Civil 3D 2023 runtime passed")
    $lines.Add("- [ ] Civil 3D 2024 runtime passed")
    $lines.Add("- [ ] Excel validation passed")
    $lines.Add("- [ ] PDF publishing validation passed")
    $lines.Add("- [ ] Exact-head approval recorded for PRs #18, #19, #20, #21, #22, #23 and #25")
    $lines.Add("- [ ] Approved to merge in dependency order")

    Set-Content -Path $reportPath -Value $lines -Encoding UTF8
    return $reportPath
}

Push-Location $repositoryRoot
try {
    Assert-CommandAvailable -Name "dotnet"
    Assert-CommandAvailable -Name "python"

    $commit = Get-GitValue -Arguments @("rev-parse", "HEAD")
    $branch = Get-GitValue -Arguments @("rev-parse", "--abbrev-ref", "HEAD")
    $status = Get-GitValue -Arguments @("status", "--porcelain")
    $workingTree = if ([string]::IsNullOrWhiteSpace($status)) { "clean" } else { "DIRTY" }
    Add-Result -Stage "Source" -Target "Git" -Status $(if ($workingTree -eq "clean") { "PASS" } else { "FAIL" }) -Details "Branch=$branch; Commit=$commit; WorkingTree=$workingTree"
    if ($workingTree -ne "clean") {
        throw "Release validation requires a clean working tree."
    }

    $validators = @(
        "Validate-ReportPresentation.py",
        "Validate-AnnotationCommands.py",
        "Validate-WorkflowRepairs.py",
        "Validate-SurveyCoordinateWorkflows.py",
        "Validate-BillOfQuantities.py",
        "Validate-DynamicProduction.py",
        "Validate-CommandRegistry.py"
    )
    foreach ($validator in $validators) {
        $path = Join-Path $PSScriptRoot $validator
        Invoke-LoggedCommand -Stage "Source validation" -Target $validator -LogName ($validator + ".log") -Command {
            & python $path
        }
    }

    $testProject = Join-Path $repositoryRoot "tests\CE.Tools.Core.Tests\CE.Tools.Core.Tests.csproj"
    Invoke-LoggedCommand -Stage "Core tests" -Target "CE.Tools.Core.Tests" -LogName "core-tests.log" -Command {
        & dotnet run --project $testProject -c Release
    }

    if (-not $SkipCivilBuild) {
        if ($Version -in @("2023", "All")) {
            if (-not (Test-CivilRoot -Year "2023" -Root $AutoCAD2023Root)) {
                throw "Civil 3D 2023 prerequisites failed."
            }
            Invoke-CivilBuild -Year "2023" -Root $AutoCAD2023Root
        }

        if ($Version -in @("2024", "All")) {
            if (-not (Test-CivilRoot -Year "2024" -Root $AutoCAD2024Root)) {
                throw "Civil 3D 2024 prerequisites failed."
            }
            Invoke-CivilBuild -Year "2024" -Root $AutoCAD2024Root
        }
    }
    else {
        Add-Result -Stage "Civil build" -Target $Version -Status "SKIPPED" -Details "-SkipCivilBuild was supplied."
    }

    if (-not $SkipInstallSnapshot) {
        $bundle = Join-Path $repositoryRoot "bundle\CE Tools.bundle"
        if (-not (Test-Path $bundle)) {
            throw "Application bundle was not found: $bundle"
        }
        Copy-Item -Path $bundle -Destination $snapshotDirectory -Recurse -Force
        $hashFile = Join-Path $OutputDirectory "SHA256SUMS.txt"
        Get-ChildItem -Path $snapshotDirectory -File -Recurse |
            Sort-Object FullName |
            ForEach-Object {
                $relative = $_.FullName.Substring($snapshotDirectory.Length).TrimStart('\')
                $hash = (Get-FileHash -Path $_.FullName -Algorithm SHA256).Hash
                "$hash  $relative"
            } | Set-Content -Path $hashFile -Encoding ASCII
        Add-Result -Stage "Snapshot" -Target "CE Tools.bundle" -Status "PASS" -Details "Bundle snapshot and SHA256SUMS.txt created."
    }

    $report = Write-Report -Commit $commit -Branch $branch -WorkingTree $workingTree
    Write-Host "Release validation automation completed." -ForegroundColor Green
    Write-Host "Report: $report" -ForegroundColor Green
}
catch {
    $commit = Get-GitValue -Arguments @("rev-parse", "HEAD")
    $branch = Get-GitValue -Arguments @("rev-parse", "--abbrev-ref", "HEAD")
    $status = Get-GitValue -Arguments @("status", "--porcelain")
    $workingTree = if ([string]::IsNullOrWhiteSpace($status)) { "clean" } else { "DIRTY" }
    $report = Write-Report -Commit $commit -Branch $branch -WorkingTree $workingTree
    Write-Error "Release validation failed. Review $report and the logs in $logDirectory. $($_.Exception.Message)"
    exit 1
}
finally {
    Pop-Location
}
