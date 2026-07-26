[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)

function Replace-ExactText {
    param(
        [Parameter(Mandatory = $true)][string]$RelativePath,
        [Parameter(Mandatory = $true)][string]$OldText,
        [Parameter(Mandatory = $true)][string]$NewText,
        [Parameter(Mandatory = $true)][string]$Description
    )
    $path = Join-Path $repositoryRoot $RelativePath
    if (-not (Test-Path $path)) { throw "Review-fix source was not found: $RelativePath" }
    $text = [System.IO.File]::ReadAllText($path).Replace("`r`n", "`n")
    $oldNormalised = $OldText.Replace("`r`n", "`n")
    $newNormalised = $NewText.Replace("`r`n", "`n")
    if ($text.Contains($newNormalised) -and -not $text.Contains($oldNormalised)) { return }
    if (-not $text.Contains($oldNormalised)) {
        throw "Could not apply review-fix extension '$Description' in '$RelativePath'."
    }
    [System.IO.File]::WriteAllText(
        $path,
        $text.Replace($oldNormalised, $newNormalised),
        $utf8NoBom)
    Write-Host "  $Description" -ForegroundColor Green
}

$alignmentFile = "src\CE.Tools.Civil3D\SewerBranchAlignmentCommands.cs"
Replace-ExactText `
    -RelativePath $alignmentFile `
    -OldText @'
            ObjectId[] ids = sourceIds
                .Where(id => !id.IsNull && !id.IsErased)
                .Distinct()
                .ToArray();
'@ `
    -NewText @'
            ObjectId[] ids = sourceIds
                .Where(id => !id.IsNull)
                .Distinct()
                .ToArray();
'@ `
    -Description "avoid version-specific ObjectId erased-state filtering"

Replace-ExactText `
    -RelativePath $alignmentFile `
    -OldText @'
            Match match = Regex.Match(
                structure.Name ?? string.Empty,
                "^MH" + branchNumber.ToString(CultureInfo.InvariantCulture) +
                @"\.(?<sequence>\d+)$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            int sequence;
            return match.Success &&
                   int.TryParse(
                       match.Groups["sequence"].Value,
                       NumberStyles.None,
                       CultureInfo.InvariantCulture,
                       out sequence)
                ? sequence
                : int.MaxValue;
'@ `
    -NewText @'
            string name = structure.Name ?? string.Empty;
            Match match = Regex.Match(
                name,
                "^MH" + branchNumber.ToString(CultureInfo.InvariantCulture) +
                @"\.(?<sequence>\d+)$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (!match.Success)
            {
                match = Regex.Match(
                    name,
                    @"^MH(?<sequence>\d+)$",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            }

            int sequence;
            return match.Success &&
                   int.TryParse(
                       match.Groups["sequence"].Value,
                       NumberStyles.None,
                       CultureInfo.InvariantCulture,
                       out sequence)
                ? sequence
                : int.MaxValue;
'@ `
    -Description "support dotted whole-network and simple selected-path manhole sequences"

$ribbonFile = "src\CE.Tools.Civil3D\PluginEntry.cs"
Replace-ExactText `
    -RelativePath $ribbonFile `
    -OldText @'
                Menu("CE_TOOLS_FAST_BLOCK_MENU", "Fast Block\nEditing", "Avoid slow in-place architectural block editing.",
                    Cmd("Open Normal Block Editor", "CE_BLOCKEDITFAST ", "Open ordinary blocks with BEDIT and XREFs with XOPEN; REFEDIT is not used."))));
'@ `
    -NewText @'
                Menu("CE_TOOLS_SURFACE_REPAIR_FIX_MENU", "Surface Spike\n& Hole Repair", "Create a separate corrected TIN copy that replaces local spikes/lows and fills detected internal open-edge components.",
                    Cmd("Repair Surface Spikes and Holes", "CE_SURFSPIKEHOLEFIX ", "Create a reversible spike/hole repaired surface copy and show the repair popup/table; the original remains unchanged.")),
                Menu("CE_TOOLS_FAST_BLOCK_MENU", "Fast Block\nEditing", "Avoid slow in-place architectural block editing.",
                    Cmd("Open Normal Block Editor", "CE_BLOCKEDITFAST ", "Open ordinary blocks with BEDIT and XREFs with XOPEN; REFEDIT is not used."))));
'@ `
    -Description "add reversible surface spike and hole repair to the review-fixes panel"

Write-Host "26 July 2026 runtime review-fix extension was applied." -ForegroundColor Green
