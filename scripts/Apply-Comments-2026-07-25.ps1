[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
$changedFiles = New-Object System.Collections.Generic.List[string]

function Replace-ExactText {
    param(
        [Parameter(Mandatory = $true)]
        [string]$RelativePath,

        [Parameter(Mandatory = $true)]
        [string]$OldText,

        [Parameter(Mandatory = $true)]
        [string]$NewText,

        [Parameter(Mandatory = $true)]
        [string]$Description
    )

    $path = Join-Path $repositoryRoot $RelativePath
    if (-not (Test-Path $path)) {
        throw "Comment source file was not found: $RelativePath"
    }

    $text = [System.IO.File]::ReadAllText($path).Replace("`r`n", "`n")
    $oldNormalised = $OldText.Replace("`r`n", "`n")
    $newNormalised = $NewText.Replace("`r`n", "`n")

    if ($text.Contains($newNormalised) -and -not $text.Contains($oldNormalised)) {
        return
    }

    if (-not $text.Contains($oldNormalised)) {
        throw "Could not apply comment change '$Description' in '$RelativePath'. The expected source text was not found."
    }

    $updated = $text.Replace($oldNormalised, $newNormalised)
    [System.IO.File]::WriteAllText($path, $updated, $utf8NoBom)
    if (-not $changedFiles.Contains($RelativePath)) {
        $changedFiles.Add($RelativePath)
    }
}

$projectFile = "src\CE.Tools.Civil3D\ProjectSetupCommands.cs"
$oldProjectInput = @'
            ProjectMetadata existing = ReadProjectMetadata(
                document.Database,
                ProjectRecordName);
            var proposed = new ProjectMetadata();

            for (int index = 0; index < FieldOrder.Length; index++)
            {
                string field = FieldOrder[index];
                string currentValue = existing.Get(field);
                if (string.IsNullOrWhiteSpace(currentValue) &&
                    string.Equals(field, "Units", StringComparison.OrdinalIgnoreCase))
                {
                    currentValue = "Metric";
                }

                PromptResult result = PromptForValue(editor, field, currentValue);
                if (result.Status != PromptStatus.OK)
                {
                    editor.WriteMessage(
                        "\nCE_PROJECTSETUP cancelled. Existing project metadata was not changed.");
                    return;
                }

                proposed.Set(field, result.StringResult == null
                    ? string.Empty
                    : result.StringResult.Trim());
            }
'@
$newProjectInput = @'
            ProjectMetadata existing = ReadProjectMetadata(
                document.Database,
                ProjectRecordName);
            var initialValues = new Dictionary<string, string>(
                StringComparer.OrdinalIgnoreCase);
            for (int index = 0; index < FieldOrder.Length; index++)
            {
                string field = FieldOrder[index];
                string currentValue = existing.Get(field);
                if (string.IsNullOrWhiteSpace(currentValue) &&
                    string.Equals(field, "Units", StringComparison.OrdinalIgnoreCase))
                {
                    currentValue = "Metric";
                }

                initialValues[field] = currentValue ?? string.Empty;
            }

            var setupWindow = new ProjectSetupPopupWindow(
                FieldOrder,
                initialValues);
            AcApplication.ShowModalWindow(setupWindow);
            if (!setupWindow.Accepted)
            {
                editor.WriteMessage(
                    "\nCE_PROJECTSETUP cancelled. Existing project metadata was not changed.");
                return;
            }

            var proposed = new ProjectMetadata();
            for (int index = 0; index < FieldOrder.Length; index++)
            {
                string field = FieldOrder[index];
                proposed.Set(field, setupWindow.GetValue(field));
            }
'@
Replace-ExactText `
    -RelativePath $projectFile `
    -OldText $oldProjectInput `
    -NewText $newProjectInput `
    -Description "replace separate project prompts with one project setup popup"

$parkingFile = "src\CE.Tools.Civil3D\ParkingCommands.cs"
Replace-ExactText `
    -RelativePath $parkingFile `
    -OldText 'CreateSingleRow(document);' `
    -NewText 'ClosedParkingBayWorkflow.CreateSingleRow(document);' `
    -Description "route single parking rows to closed bay polyline generation"
Replace-ExactText `
    -RelativePath $parkingFile `
    -OldText 'CreateDoubleRow(document);' `
    -NewText 'ClosedParkingBayWorkflow.CreateDoubleRow(document);' `
    -Description "route double parking rows to closed bay polyline generation"

if ($changedFiles.Count -eq 0) {
    Write-Host "25 July 2026 comment corrections are already applied." -ForegroundColor DarkGray
}
else {
    Write-Host "Applied 25 July 2026 comment corrections:" -ForegroundColor Green
    foreach ($file in $changedFiles) {
        Write-Host "  $file"
    }
}
