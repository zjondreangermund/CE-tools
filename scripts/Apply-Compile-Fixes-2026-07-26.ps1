$ErrorActionPreference = "Stop"

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$presentationPath = Join-Path $repositoryRoot "src\CE.Tools.Civil3D\ProjectPresentationCommands.cs"

if (-not (Test-Path $presentationPath)) {
    throw "Project presentation source was not found: $presentationPath"
}

$text = [System.IO.File]::ReadAllText($presentationPath)
$geometryUsing = "using Autodesk.AutoCAD.Geometry;"

if (-not $text.Contains($geometryUsing)) {
    $anchor = "using Autodesk.AutoCAD.EditorInput;"
    if (-not $text.Contains($anchor)) {
        throw "Could not locate the ProjectPresentationCommands using-directive anchor."
    }

    $text = $text.Replace(
        $anchor,
        $anchor + [Environment]::NewLine + $geometryUsing)

    [System.IO.File]::WriteAllText(
        $presentationPath,
        $text,
        (New-Object System.Text.UTF8Encoding($false)))

    Write-Host "Added Autodesk.AutoCAD.Geometry for ProjectPresentationCommands Point3d compilation." -ForegroundColor Green
}
else {
    Write-Host "ProjectPresentationCommands geometry namespace is already present." -ForegroundColor DarkGray
}
