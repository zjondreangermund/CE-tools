[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)]
    [string]$V154Root
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).ProviderPath
$target = (Resolve-Path $V154Root).ProviderPath
$rel = 'src\CE.Tools.Civil3D'
$source = Join-Path $repo "$rel\ViewportTextUprightCommands.cs"
$dest = Join-Path $target "$rel\ViewportTextUprightCommands.cs"
$entry = Join-Path $target "$rel\PluginEntry.cs"
$project = Join-Path $target "$rel\CE.Tools.Civil3D.csproj"
if (-not (Test-Path -LiteralPath $source)) {
    throw "The GitHub download does not contain the new viewport tool source: $source"
}
foreach ($path in @($entry, $project)) {
    if (-not (Test-Path -LiteralPath $path)) { throw "Required V154 file missing: $path" }
}
$projectContent = Get-Content -LiteralPath $project -Raw
if ($projectContent -notmatch 'CE_CIVIL3D_2024') {
    throw 'V154 does not define CE_CIVIL3D_2024. Confirm this is the converted 2024 test project; no files were changed.'
}
$original = Get-Content -LiteralPath $entry -Raw
$updated = $original

$initialize = '            AnnotationScaleSyncManager.Initialize();'
$terminate = '            AnnotationScaleSyncManager.Terminate();'
if ($updated -notmatch 'ViewportTextUprightManager.Initialize\(') {
    if (-not $updated.Contains($initialize)) {
        throw 'Could not find CE Tools startup manager in V154 PluginEntry.cs; no files were changed.'
    }
    $init = @'
#if CE_CIVIL3D_2024
            ViewportTextUprightManager.Initialize();
#endif
'@
    $updated = $updated.Replace($initialize, $initialize + [Environment]::NewLine + $init)
}
if ($updated -notmatch 'ViewportTextUprightManager.Terminate\(') {
    if (-not $updated.Contains($terminate)) {
        throw 'Could not find CE Tools termination manager in V154 PluginEntry.cs; no files were changed.'
    }
    $finish = @'
#if CE_CIVIL3D_2024
            ViewportTextUprightManager.Terminate();
#endif
'@
    $updated = $updated.Replace($terminate, $finish + [Environment]::NewLine + $terminate)
}

if (Test-Path -LiteralPath $dest) {
    if ((Get-FileHash $source -Algorithm SHA256).Hash -eq
        (Get-FileHash $dest -Algorithm SHA256).Hash -and $updated -eq $original) {
        Write-Host 'V154 already has the current viewport text correction.' -ForegroundColor Green
        return
    }
}
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$backup = "$entry.before-vptext-$stamp.bak"
Copy-Item -LiteralPath $entry -Destination $backup
try {
    [System.IO.File]::WriteAllText($entry, $updated, [System.Text.UTF8Encoding]::new($false))
    Copy-Item -LiteralPath $source -Destination $dest -Force
}
catch {
    Copy-Item -LiteralPath $backup -Destination $entry -Force
    throw
}
Write-Host "Updated only V154 2024 PluginEntry.cs and $dest" -ForegroundColor Green
Write-Host "Original startup file backed up: $backup"
Write-Host "No 2023 source, road sheet logic, popup or installer has been changed."
Write-Host "Now build with BUILD-V154-CIVIL3D-2024.ps1 (without -Install), then test on a copy."
