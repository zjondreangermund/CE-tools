[CmdletBinding()]
param(
    [switch]$SkipInstall,
    [switch]$Clean,
    [string]$Configuration = 'Release',
    [string]$SourceCommit = ''
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Find-MSBuild {
    $programFilesX86 = ${env:ProgramFiles(x86)}
    $candidates = @(
        (Join-Path $env:ProgramFiles 'Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe'),
        (Join-Path $env:ProgramFiles 'Microsoft Visual Studio\2022\Professional\MSBuild\Current\Bin\MSBuild.exe'),
        (Join-Path $env:ProgramFiles 'Microsoft Visual Studio\2022\Enterprise\MSBuild\Current\Bin\MSBuild.exe')
    )
    if ($programFilesX86) {
        $candidates += (Join-Path $programFilesX86 'Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe')
        $vswhere = Join-Path $programFilesX86 'Microsoft Visual Studio\Installer\vswhere.exe'
        if (Test-Path $vswhere) {
            $detected = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' 2>$null | Select-Object -First 1
            if ($detected) { $candidates = @($detected) + $candidates }
        }
    }
    foreach ($candidate in $candidates | Where-Object { $_ } | Select-Object -Unique) {
        if (Test-Path $candidate) { return $candidate }
    }
    $cmd = Get-Command msbuild.exe -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    throw 'MSBuild was not found. Install Visual Studio 2022 Build Tools with .NET Framework 4.8 development tools.'
}

function Restore-V60SupportSources {
    param([Parameter(Mandatory=$true)][string]$RepoRoot)
    $archive = Join-Path $RepoRoot 'recovery\v60\missing-v60-sources.b64'
    if (-not (Test-Path -LiteralPath $archive)) { return }
    $targets = @(
        'CommentPresentationCommands.cs',
        'DynamicTypicalDetailEngine.cs',
        'ProductionCommentCommands.cs'
    )
    $sourceRoot = Join-Path $RepoRoot 'src\CE.Tools.Civil3D'
    $needsRestore = $false
    foreach ($name in $targets) {
        if (-not (Test-Path -LiteralPath (Join-Path $sourceRoot $name))) { $needsRestore = $true }
    }
    if (-not $needsRestore) { return }

    $temp = Join-Path $env:TEMP ('CE-Tools-V60-Restore-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Force -Path $temp | Out-Null
    try {
        $zip = Join-Path $temp 'sources.zip'
        $raw = [System.IO.File]::ReadAllText($archive)
        $base64 = [System.Text.RegularExpressions.Regex]::Replace($raw, '[^A-Za-z0-9+/=]', '')
        if ([string]::IsNullOrWhiteSpace($base64)) {
            throw 'The staged V60 recovery archive is empty after Base64 normalization.'
        }
        $firstPadding = $base64.IndexOf('=')
        if ($firstPadding -ge 0 -and $firstPadding -lt ($base64.Length - 2)) {
            $body = $base64.Substring(0, $firstPadding).Replace('=', '')
            $base64 = $body
        }
        $remainder = $base64.Length % 4
        if ($remainder -eq 2) { $base64 += '==' }
        elseif ($remainder -eq 3) { $base64 += '=' }
        elseif ($remainder -eq 1) { throw 'The staged V60 recovery archive has an invalid Base64 length.' }

        try {
            $bytes = [Convert]::FromBase64String($base64)
        }
        catch {
            throw "The staged V60 recovery archive could not be decoded after normalization. $($_.Exception.Message)"
        }
        if ($bytes.Length -lt 4 -or $bytes[0] -ne 0x50 -or $bytes[1] -ne 0x4B) {
            throw 'The decoded V60 recovery archive is not a valid ZIP file.'
        }
        [IO.File]::WriteAllBytes($zip, $bytes)
        Expand-Archive -LiteralPath $zip -DestinationPath $temp -Force
        foreach ($name in $targets) {
            $from = Join-Path $temp $name
            if (-not (Test-Path -LiteralPath $from)) { throw "V60 recovery source missing from archive: $name" }
            Copy-Item -LiteralPath $from -Destination (Join-Path $sourceRoot $name) -Force
        }
        Write-Host 'Restored remaining V60 support sources.' -ForegroundColor Green
    }
    finally {
        Remove-Item $temp -Recurse -Force -ErrorAction SilentlyContinue
    }
}

function Repair-Civil3D2023RibbonSource {
    param([Parameter(Mandatory=$true)][string]$RepoRoot)
    $plugin = Join-Path $RepoRoot 'src\CE.Tools.Civil3D\PluginEntry.cs'
    if (-not (Test-Path $plugin)) { throw "Plugin source not found: $plugin" }
    $text = Get-Content $plugin -Raw
    if ($text -notmatch 'RibbonRow') { return }

    $old = @'
        private static RibbonRow Row(params RibbonItem[] items)
        {
            var row = new RibbonRow();
            foreach (RibbonItem item in items) row.RowItems.Add(item);
            return row;
        }

        private static void AddPanel(
            RibbonTab tab,
            string panelId,
            string title,
            params RibbonRow[] rows)
        {
            var source = new RibbonPanelSource
            {
                Id = panelId,
                Title = title.ToUpperInvariant()
            };
            foreach (RibbonRow row in rows) source.Rows.Add(row);
            tab.Panels.Add(new RibbonPanel { Source = source });
        }
'@
    $new = @'
        private static RibbonItem[] Row(params RibbonItem[] items)
        {
            return items;
        }

        private static void AddPanel(
            RibbonTab tab,
            string panelId,
            string title,
            params RibbonItem[][] rows)
        {
            var source = new RibbonPanelSource
            {
                Id = panelId,
                Title = title.ToUpperInvariant()
            };
            foreach (RibbonItem[] row in rows)
            {
                foreach (RibbonItem item in row)
                    source.Items.Add(item);
            }
            tab.Panels.Add(new RibbonPanel { Source = source });
        }
'@
    if (-not $text.Contains($old)) {
        throw 'The expected Civil 3D 2023 RibbonRow block was not found in PluginEntry.cs.'
    }
    Set-Content -Path $plugin -Value $text.Replace($old, $new) -Encoding UTF8
    Write-Host 'Applied Civil 3D 2023 ribbon compatibility repair.' -ForegroundColor Green
}

$repo = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repo 'src\CE.Tools.Civil3D\CE.Tools.Civil3D.csproj'
$verifiedInstaller = Join-Path $repo 'scripts\Install-VerifiedCivil3D2023Bundle.ps1'
$packager = Join-Path $repo 'scripts\New-CE-ToolsReleasePackage.ps1'
$autoCadRoot = 'C:\Program Files\Autodesk\AutoCAD 2023'
$civil3DRoot = if (Test-Path (Join-Path $autoCadRoot 'AeccDbMgd.dll')) { $autoCadRoot } else { Join-Path $autoCadRoot 'C3D' }
$aecRoot = if (Test-Path (Join-Path $civil3DRoot 'AecBaseMgd.dll')) { $civil3DRoot } else { $autoCadRoot }

$required = @(
    (Join-Path $autoCadRoot 'AcMgd.dll'),
    (Join-Path $autoCadRoot 'AcDbMgd.dll'),
    (Join-Path $autoCadRoot 'AcCoreMgd.dll'),
    (Join-Path $autoCadRoot 'AdWindows.dll'),
    (Join-Path $civil3DRoot 'AeccDbMgd.dll'),
    (Join-Path $aecRoot 'AecBaseMgd.dll')
)
$missing = $required | Where-Object { -not (Test-Path $_) }
if ($missing) {
    throw "Civil 3D 2023 SDK files are missing:`n$($missing -join "`n")"
}
if (-not (Test-Path $project)) { throw "Project not found: $project" }
if (-not (Test-Path -LiteralPath $verifiedInstaller)) { throw "Verified installer not found: $verifiedInstaller" }
if (-not (Test-Path -LiteralPath $packager)) { throw "Release packager not found: $packager" }
if ([string]::IsNullOrWhiteSpace($SourceCommit)) {
    try { $SourceCommit = (& git -C $repo rev-parse HEAD 2>$null).Trim() }
    catch { $SourceCommit = 'UNKNOWN' }
    if ([string]::IsNullOrWhiteSpace($SourceCommit)) { $SourceCommit = 'UNKNOWN' }
}

try {
    Restore-V60SupportSources -RepoRoot $repo
}
catch {
    Write-Warning "Optional V60 source recovery was skipped: $($_.Exception.Message)"
}
Repair-Civil3D2023RibbonSource -RepoRoot $repo

$msbuild = Find-MSBuild
Write-Host "Using MSBuild: $msbuild" -ForegroundColor Cyan
Write-Host 'Using single-process compilation with the Roslyn compiler server disabled.' -ForegroundColor Cyan

$commonBuildArgs = @(
    "/p:Configuration=$Configuration",
    '/p:Platform=x64',
    '/p:AutoCADVersion=2023',
    "/p:AutoCADRoot=$autoCadRoot",
    "/p:Civil3DRoot=$civil3DRoot",
    "/p:AecRoot=$aecRoot",
    '/p:UseSharedCompilation=false',
    '/p:BuildInParallel=false',
    '/p:Deterministic=false',
    '/m:1',
    '/nr:false'
)

if ($Clean) {
    & $msbuild $project /t:Clean @commonBuildArgs
    if ($LASTEXITCODE -ne 0) { throw "Clean failed with exit code $LASTEXITCODE" }
}

& $msbuild $project /t:Restore,Build /p:ContinuousIntegrationBuild=false @commonBuildArgs
if ($LASTEXITCODE -ne 0) { throw "Build failed with exit code $LASTEXITCODE" }

$bundle = Join-Path $repo 'bundle\CE Tools.bundle'
$dll = Join-Path $bundle 'Contents\Windows\2023\CE.Tools.Civil3D.dll'
$coreDll = Join-Path $bundle 'Contents\Windows\2023\CE.Tools.Core.dll'
foreach ($file in @($dll, $coreDll)) {
    if (-not (Test-Path $file)) { throw "Expected build output missing: $file" }
}

$releaseDir = Join-Path $repo 'artifacts\release'
New-Item -ItemType Directory -Force -Path $releaseDir | Out-Null
$releasePackage = & $packager `
    -BundlePath $bundle `
    -OutputDirectory $releaseDir `
    -SourceCommit $SourceCommit
$zip = $releasePackage.ZipPath

if (-not $SkipInstall) {
    $buildLog = Join-Path $releaseDir ('build-install-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.log')
    & $verifiedInstaller `
        -SourceBundle $bundle `
        -SourceCommit $SourceCommit `
        -BuildLogPath $buildLog
}

Write-Host "Build succeeded." -ForegroundColor Green
Write-Host "Package: $zip"
Write-Host "Civil 3D DLL: $dll"
Write-Host "Source commit: $SourceCommit"
