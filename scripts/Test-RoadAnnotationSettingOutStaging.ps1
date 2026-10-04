[CmdletBinding()]
param([string]$RepoRoot = (Split-Path -Parent $PSScriptRoot))
$ErrorActionPreference = 'Stop'
$temp = Join-Path ([IO.Path]::GetTempPath()) ('ce-road-settingout-' + [Guid]::NewGuid().ToString('N'))
try {
    $folder = Join-Path $temp 'src/CE.Tools.Civil3D'
    New-Item -ItemType Directory -Path $folder -Force | Out-Null
    $path = Join-Path $folder 'VertexSettingOutCommands.cs'
    $source = [IO.File]::ReadAllText((Join-Path $RepoRoot 'src/CE.Tools.Civil3D/VertexSettingOutCommands.cs'))
    # Reproduce the lost hooks from the historical RefreshTable/UpdateOutput replacements.
    $source = $source.Replace('RestoreRoadNumbers(document.Database, transaction, sources, link);', '')
    $source = $source.Replace('link.StartRecordKey, link.RoadSeeds);', 'link.StartRecordKey);')
    $source = $source.Replace('cogo.LayerId = RoadAnnotationPlacement.Layer(cogo.Database, transaction, link.PointLayer, "CE-ROAD-JUNCTION-POINTS");', '')
    $source = $source.Replace('mtext.LayerId = RoadAnnotationPlacement.Layer(mtext.Database, transaction, link.LeaderLayer, "CE-ROAD-JUNCTION-LEADERS");', '')
    [IO.File]::WriteAllText($path, $source)
    $repair = Join-Path $RepoRoot 'scripts/Repair-October04-RoadAnnotationSettingOut-Civil3D2023.ps1'
    & $repair -RepoRoot $temp
    $repaired = [IO.File]::ReadAllText($path)
    foreach ($token in @('RestoreRoadNumbers(document.Database, transaction, sources, link);',
        'link.StartRecordKey, link.RoadSeeds);', 'cogo.LayerId = RoadAnnotationPlacement.Layer',
        'mtext.LayerId = RoadAnnotationPlacement.Layer')) {
        if (-not $repaired.Contains($token)) { throw "Lost setting-out refresh hook: $token" }
    }
    & $repair -RepoRoot $temp
    if ($repaired -cne [IO.File]::ReadAllText($path)) { throw 'Road setting-out finalizer is not idempotent.' }
    $targets = [IO.File]::ReadAllText((Join-Path $RepoRoot 'Directory.Build.targets'))
    if ($targets -notmatch '(?s)Name="CEApplyOctober04RoadAnnotationSettingOut"\s+AfterTargets="CEApplySeptember10SewerAuditSequenceCompileFix"') {
        throw 'Road finalizer must run after the historical compile/refresh repairs.'
    }
    Write-Host 'Road setting-out staged refresh regression passed.'
}
finally { if (Test-Path $temp) { Remove-Item $temp -Recurse -Force } }
