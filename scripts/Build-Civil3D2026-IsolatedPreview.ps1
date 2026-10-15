[CmdletBinding()]
param(
    [string]$AutodeskRoot = 'C:\Program Files\Autodesk\AutoCAD 2026',
    [string]$Civil3DRoot = '',
    [string]$AecRoot = '',
    [ValidateSet('Debug','Release')][string]$Configuration = 'Release',
    [string]$PreviewOutput = '',
    [switch]$KeepTemporaryWorktree
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# This command deliberately never builds in the active CE Tools 2023 checkout.
# Historical 2023 build finalizers mutate source files as part of compilation.
# Port and test Civil 3D 2026 in an independent Git worktree instead.
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).ProviderPath
if (-not (Get-Command git -ErrorAction SilentlyContinue)) { throw 'Git is required for isolated builds.' }
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw '.NET 8 SDK is required.' }
if (-not (Test-Path (Join-Path $AutodeskRoot 'AcMgd.dll'))) {
    throw "AutoCAD 2026 managed references were not found: $AutodeskRoot"
}
function Find-ReferenceDirectory([string]$basePath, [string]$name) {
    if ((Test-Path (Join-Path $basePath $name))) { return $basePath }
    $item = Get-ChildItem -Path $basePath -Recurse -File -Filter $name -ErrorAction SilentlyContinue |
        Select-Object -First 1
    if (-not $item) { throw "Missing $name under $basePath. Check the Civil 3D 2026 installation." }
    return $item.DirectoryName
}
if (-not $Civil3DRoot) { $Civil3DRoot = Find-ReferenceDirectory $AutodeskRoot 'AeccDbMgd.dll' }
if (-not $AecRoot) { $AecRoot = Find-ReferenceDirectory $AutodeskRoot 'AecBaseMgd.dll' }
if (-not $PreviewOutput) { $PreviewOutput = Join-Path $root 'artifacts\2026-isolated-preview' }
$gitRoot = (& git -C $root rev-parse --show-toplevel).Trim()
if ($LASTEXITCODE -ne 0) { throw 'Repository must be a Git checkout.' }
$worktree = Join-Path ([System.IO.Path]::GetTempPath()) ("ce-tools-2026-" + [Guid]::NewGuid().ToString('N'))
$created = $false
try {
    & git -C $gitRoot worktree add --detach $worktree HEAD
    if ($LASTEXITCODE -ne 0) { throw 'Unable to create temporary 2026 worktree.' }
    $created = $true
    $project = Join-Path $worktree 'src\CE.Tools.Civil3D\CE.Tools.Civil3D.csproj'
    & dotnet build $project -c $Configuration "-p:AutoCADVersion=2026" `
        "-p:AutoCADRoot=$AutodeskRoot" "-p:Civil3DRoot=$Civil3DRoot" "-p:AecRoot=$AecRoot" `
        --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Civil 3D 2026 build failed. Nothing was installed or changed in 2023.' }
    $compiled = Join-Path $worktree 'bundle\CE Tools.bundle\Contents\Windows\2026'
    if (-not (Test-Path (Join-Path $compiled 'CE.Tools.Civil3D.dll'))) {
        throw '2026 DLL was not produced. Nothing has been installed.'
    }
    New-Item -Path $PreviewOutput -ItemType Directory -Force | Out-Null
    Copy-Item (Join-Path $compiled '*') $PreviewOutput -Recurse -Force
    Write-Host "Isolated Civil 3D 2026 preview built at: $PreviewOutput" -ForegroundColor Green
    Write-Warning 'Preview ONLY: no installer run, no 2023 DLL or bundle modified. Test in Civil 3D 2026 before deployment.'
}
finally {
    if ($created -and -not $KeepTemporaryWorktree) {
        & git -C $gitRoot worktree remove --force $worktree
        if ($LASTEXITCODE -ne 0) { Write-Warning "Temporary worktree cleanup required: $worktree" }
    }
}
