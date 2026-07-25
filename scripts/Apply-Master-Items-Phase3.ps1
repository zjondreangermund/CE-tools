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
    if (-not (Test-Path $path)) { throw "Phase 3 source was not found: $RelativePath" }
    $text = [System.IO.File]::ReadAllText($path).Replace("`r`n", "`n")
    $oldNormalised = $OldText.Replace("`r`n", "`n")
    $newNormalised = $NewText.Replace("`r`n", "`n")
    if ($text.Contains($newNormalised) -and -not $text.Contains($oldNormalised)) { return }
    if (-not $text.Contains($oldNormalised)) {
        throw "Could not apply Phase 3 change '$Description' in '$RelativePath'."
    }
    [System.IO.File]::WriteAllText(
        $path,
        $text.Replace($oldNormalised, $newNormalised),
        $utf8NoBom)
    Write-Host "  $Description" -ForegroundColor Green
}

$ribbonFile = "src\CE.Tools.Civil3D\PluginEntry.cs"
$oldHydraulicTail = @'
                    Cmd("Pump Duty-Point Review", "CE_PUMPREVIEW ", "Screen a candidate pump rating against a simplified Hazen-Williams system duty point."),
                    Cmd("Clear Hydraulic Review Graphics", "CE_HYDRAULICCLEAR ", "Erase only CE-generated catchment and hydraulic review graphics.")),
'@
$newHydraulicTail = @'
                    Cmd("Pump Duty-Point Review", "CE_PUMPREVIEW ", "Screen a candidate pump rating against a simplified Hazen-Williams system duty point."),
                    Cmd("Clear Hydraulic Review Graphics", "CE_HYDRAULICCLEAR ", "Erase only CE-generated catchment and hydraulic review graphics."),
                    Cmd("Surface Hydrology Tools", "CE_HYDROLOGYTOOLS ", "Open regular-grid surface-flow, outlet catchment, hydrograph comparison and clear-review workflows."),
                    Cmd("Trace Surface Flow Route", "CE_SURFACEFLOW ", "Sample a selected TIN surface inside a closed boundary and create a preliminary priority-filled D8 flow route."),
                    Cmd("Delineate Outlet Catchment", "CE_CATCHMENTDELINEATE ", "Extract the grid cells contributing to a selected outlet and create removable catchment-perimeter and route graphics."),
                    Cmd("Compare Pre/Post Hydrographs", "CE_HYDROGRAPHCOMPARE ", "Create preliminary modified-rational pre/post development hydrographs with optional Excel export."),
                    Cmd("Clear Surface Hydrology Review", "CE_HYDROLOGYCLEAR ", "Erase only CE-generated surface-flow, catchment, outlet and label graphics.")),
'@
Replace-ExactText `
    -RelativePath $ribbonFile `
    -OldText $oldHydraulicTail `
    -NewText $newHydraulicTail `
    -Description "add tested surface-flow catchment and hydrograph commands"

$surfaceFile = "src\CE.Tools.Civil3D\SurfaceHydrologyCommands.cs"
$oldEdge = @'
    internal readonly struct CatchmentEdge
    {
        public CatchmentEdge(Point3d start, Point3d end)
        {
            Start = start;
            End = end;
        }

        public Point3d Start { get; private set; }
        public Point3d End { get; private set; }
    }
'@
$newEdge = @'
    internal sealed class CatchmentEdge
    {
        public CatchmentEdge(Point3d start, Point3d end)
        {
            Start = start;
            End = end;
        }

        public Point3d Start { get; private set; }
        public Point3d End { get; private set; }
    }
'@
Replace-ExactText `
    -RelativePath $surfaceFile `
    -OldText $oldEdge `
    -NewText $newEdge `
    -Description "use a net48-compatible catchment-edge reference type"

Write-Host "Master Items Phase 3 surface hydrology source is wired." -ForegroundColor Green
