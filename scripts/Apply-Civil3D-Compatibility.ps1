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
        throw "Compatibility source file was not found: $RelativePath"
    }

    $text = [System.IO.File]::ReadAllText($path).Replace("`r`n", "`n")
    $oldNormalised = $OldText.Replace("`r`n", "`n")
    $newNormalised = $NewText.Replace("`r`n", "`n")

    if ($text.Contains($newNormalised)) {
        return
    }

    if (-not $text.Contains($oldNormalised)) {
        throw "Could not apply compatibility change '$Description' in '$RelativePath'. The expected source text was not found."
    }

    $updated = $text.Replace($oldNormalised, $newNormalised)
    [System.IO.File]::WriteAllText($path, $updated, $utf8NoBom)
    if (-not $changedFiles.Contains($RelativePath)) {
        $changedFiles.Add($RelativePath)
    }
}

$projectFile = "src\CE.Tools.Civil3D\CE.Tools.Civil3D.csproj"
Replace-ExactText `
    -RelativePath $projectFile `
    -OldText '    <Reference Include="System.IO.Compression" />' `
    -NewText "    <Reference Include=`"System.Windows.Forms`" />`n    <Reference Include=`"System.IO.Compression`" />" `
    -Description "add the System.Windows.Forms framework reference"

$sewerFile = "src\CE.Tools.Civil3D\SewerProductionCommands.cs"
$oldSewerSettings = @'
            if (!PromptText(editor, "Alignment style", settings.AlignmentStyle, out settings.AlignmentStyle))
                return;
            if (!PromptText(editor, "Profile style", settings.ProfileStyle, out settings.ProfileStyle))
                return;
            if (!PromptText(editor, "Profile label-set style", settings.ProfileLabelSetStyle, out settings.ProfileLabelSetStyle))
                return;
            if (!PromptText(editor, "Profile-view style", settings.ProfileViewStyle, out settings.ProfileViewStyle))
                return;
            if (!PromptText(editor, "Profile-view band-set style", settings.ProfileViewBandSetStyle, out settings.ProfileViewBandSetStyle))
                return;
            if (!PromptText(editor, "Profile output layer", settings.ProfileLayer, out settings.ProfileLayer))
                return;
'@
$newSewerSettings = @'
            string promptValue;
            if (!PromptText(editor, "Alignment style", settings.AlignmentStyle, out promptValue))
                return;
            settings.AlignmentStyle = promptValue;
            if (!PromptText(editor, "Profile style", settings.ProfileStyle, out promptValue))
                return;
            settings.ProfileStyle = promptValue;
            if (!PromptText(editor, "Profile label-set style", settings.ProfileLabelSetStyle, out promptValue))
                return;
            settings.ProfileLabelSetStyle = promptValue;
            if (!PromptText(editor, "Profile-view style", settings.ProfileViewStyle, out promptValue))
                return;
            settings.ProfileViewStyle = promptValue;
            if (!PromptText(editor, "Profile-view band-set style", settings.ProfileViewBandSetStyle, out promptValue))
                return;
            settings.ProfileViewBandSetStyle = promptValue;
            if (!PromptText(editor, "Profile output layer", settings.ProfileLayer, out promptValue))
                return;
            settings.ProfileLayer = promptValue;
'@
Replace-ExactText -RelativePath $sewerFile -OldText $oldSewerSettings -NewText $newSewerSettings -Description "avoid passing sewer settings properties as out parameters"
Replace-ExactText -RelativePath $sewerFile -OldText 'network.GetPipeIds().FirstOrDefault()' -NewText 'network.GetPipeIds().Cast<ObjectId>().FirstOrDefault()' -Description "adapt sewer pipe ObjectIdCollection for LINQ"
Replace-ExactText -RelativePath $sewerFile -OldText 'network.GetStructureIds().FirstOrDefault()' -NewText 'network.GetStructureIds().Cast<ObjectId>().FirstOrDefault()' -Description "adapt sewer structure ObjectIdCollection for LINQ"
Replace-ExactText -RelativePath $sewerFile -OldText 'civilDocument.Styles.BandSetStyles.ProfileViewBandSetStyles' -NewText 'civilDocument.Styles.ProfileViewBandSetStyles' -Description "use the Civil 3D 2023 profile-view band-set collection"
Replace-ExactText -RelativePath $sewerFile -OldText '" SEWER PROFILE\PProfile: "' -NewText '" SEWER PROFILE\\PProfile: "' -Description "escape the sewer MText paragraph code"

$stormwaterFile = "src\CE.Tools.Civil3D\StormwaterProductionCommands.cs"
$oldStormwaterSettings = @'
            if (!PromptText(editor, "Alignment style", settings.AlignmentStyle, out settings.AlignmentStyle))
                return;
            if (!PromptText(editor, "Alignment label-set style", settings.AlignmentLabelSetStyle, out settings.AlignmentLabelSetStyle))
                return;
            if (!PromptText(editor, "Profile style", settings.ProfileStyle, out settings.ProfileStyle))
                return;
            if (!PromptText(editor, "Profile label-set style", settings.ProfileLabelSetStyle, out settings.ProfileLabelSetStyle))
                return;
            if (!PromptText(editor, "Profile-view style", settings.ProfileViewStyle, out settings.ProfileViewStyle))
                return;
            if (!PromptText(editor, "Profile-view band-set style", settings.ProfileViewBandSetStyle, out settings.ProfileViewBandSetStyle))
                return;
            if (!PromptText(editor, "Alignment layer", settings.AlignmentLayer, out settings.AlignmentLayer))
                return;
            if (!PromptText(editor, "Profile layer", settings.ProfileLayer, out settings.ProfileLayer))
                return;
'@
$newStormwaterSettings = @'
            string promptValue;
            if (!PromptText(editor, "Alignment style", settings.AlignmentStyle, out promptValue))
                return;
            settings.AlignmentStyle = promptValue;
            if (!PromptText(editor, "Alignment label-set style", settings.AlignmentLabelSetStyle, out promptValue))
                return;
            settings.AlignmentLabelSetStyle = promptValue;
            if (!PromptText(editor, "Profile style", settings.ProfileStyle, out promptValue))
                return;
            settings.ProfileStyle = promptValue;
            if (!PromptText(editor, "Profile label-set style", settings.ProfileLabelSetStyle, out promptValue))
                return;
            settings.ProfileLabelSetStyle = promptValue;
            if (!PromptText(editor, "Profile-view style", settings.ProfileViewStyle, out promptValue))
                return;
            settings.ProfileViewStyle = promptValue;
            if (!PromptText(editor, "Profile-view band-set style", settings.ProfileViewBandSetStyle, out promptValue))
                return;
            settings.ProfileViewBandSetStyle = promptValue;
            if (!PromptText(editor, "Alignment layer", settings.AlignmentLayer, out promptValue))
                return;
            settings.AlignmentLayer = promptValue;
            if (!PromptText(editor, "Profile layer", settings.ProfileLayer, out promptValue))
                return;
            settings.ProfileLayer = promptValue;
'@
Replace-ExactText -RelativePath $stormwaterFile -OldText $oldStormwaterSettings -NewText $newStormwaterSettings -Description "avoid passing stormwater settings properties as out parameters"
Replace-ExactText -RelativePath $stormwaterFile -OldText 'civilDocument.Styles.BandSetStyles.ProfileViewBandSetStyles' -NewText 'civilDocument.Styles.ProfileViewBandSetStyles' -Description "use the Civil 3D 2023 profile-view band-set collection"
Replace-ExactText -RelativePath $stormwaterFile -OldText 'civilDocument.GetAlignmentIds().ToList()' -NewText 'civilDocument.GetAlignmentIds().Cast<ObjectId>().ToList()' -Description "adapt stormwater alignment ObjectIdCollection for LINQ"
Replace-ExactText -RelativePath $stormwaterFile -OldText '"\PProfile style: "' -NewText '"\\PProfile style: "' -Description "escape the stormwater MText paragraph code"

$waterFile = "src\CE.Tools.Civil3D\WaterProductionCommands.cs"
$oldWaterProfileName = @'
                        string profileName = UniqueName(
                            record.RouteName + " - EG",
                            ReadCivilNames(civilDocument.GetProfileIds(), transaction));
'@
$newWaterProfileName = @'
                        CivilAlignment alignment = transaction.GetObject(
                            record.AlignmentId,
                            OpenMode.ForRead,
                            false) as CivilAlignment;
                        if (alignment == null)
                            throw new InvalidOperationException(
                                "The linked water alignment is no longer available.");

                        string profileName = UniqueName(
                            record.RouteName + " - EG",
                            ReadCivilNames(alignment.GetProfileIds(), transaction));
'@
Replace-ExactText -RelativePath $waterFile -OldText $oldWaterProfileName -NewText $newWaterProfileName -Description "read water profile IDs from the owning alignment"
Replace-ExactText -RelativePath $waterFile -OldText 'ReadCivilNames(civilDocument.GetProfileViewIds(), transaction)' -NewText 'ReadCivilNames(alignment.GetProfileViewIds(), transaction)' -Description "read water profile-view IDs from the owning alignment"

$surfaceFile = "src\CE.Tools.Civil3D\SurfaceCorrectionCommands.cs"
$oldSurfaceLinq = @'
                    civilDocument.GetSurfaceIds()
                        .Select
'@
$newSurfaceLinq = @'
                    civilDocument.GetSurfaceIds()
                        .Cast<ObjectId>()
                        .Select
'@
Replace-ExactText -RelativePath $surfaceFile -OldText $oldSurfaceLinq -NewText $newSurfaceLinq -Description "adapt the surface ObjectIdCollection for LINQ"

if ($changedFiles.Count -eq 0) {
    Write-Host "Civil 3D compatibility source is already normalised." -ForegroundColor DarkGray
}
else {
    Write-Host "Applied Civil 3D compatibility corrections:" -ForegroundColor Green
    foreach ($file in $changedFiles) {
        Write-Host "  $file"
    }
}
