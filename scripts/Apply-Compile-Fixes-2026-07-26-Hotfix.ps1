$ErrorActionPreference = "Stop"

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$path = Join-Path $repositoryRoot "src\CE.Tools.Civil3D\ModelDesignAuditCommands.cs"
if (-not (Test-Path $path)) {
    throw "Model design audit source was not found: $path"
}

$utf8 = New-Object System.Text.UTF8Encoding($false)
$text = [System.IO.File]::ReadAllText($path)
$text = $text.Replace("`r`n", "`n").Replace("`r", "`n")

$correctBlock = @'
                snapshot.Layouts.Add(new LayoutAuditItem(
                    layout.LayoutName,
                    layout.ModelType,
                    viewports,
                    layout.TabOrder,
                    layout.CanonicalMediaName,
                    Convert.ToString(
                        ReadProperty(layout, "ConfigName") ??
                        ReadProperty(layout, "PlotConfigurationName"),
                        CultureInfo.CurrentCulture)));
'@

if ($text.Contains($correctBlock)) {
    Write-Host "Civil 3D 2023 layout plot-configuration compatibility block is already correct." -ForegroundColor DarkGray
    exit 0
}

$startMarker = "                snapshot.Layouts.Add(new LayoutAuditItem("
$endMarker = "            }`n        }`n`n        private static void ReadCoordinateSystem"
$startIndex = $text.IndexOf($startMarker, [System.StringComparison]::Ordinal)
if ($startIndex -lt 0) {
    throw "Could not locate the LayoutAuditItem creation block in ModelDesignAuditCommands.cs"
}

$endIndex = $text.IndexOf($endMarker, $startIndex, [System.StringComparison]::Ordinal)
if ($endIndex -lt 0) {
    throw "Could not locate the end of the layout-audit block in ModelDesignAuditCommands.cs"
}

$text = $text.Substring(0, $startIndex) + $correctBlock + "`n" + $text.Substring($endIndex)
[System.IO.File]::WriteAllText($path, $text, $utf8)

Write-Host "Corrected the Civil 3D 2023 layout plot-configuration compatibility block." -ForegroundColor Green
