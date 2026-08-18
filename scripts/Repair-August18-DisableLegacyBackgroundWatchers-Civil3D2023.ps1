[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$RepoRoot)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = (Resolve-Path -LiteralPath $RepoRoot.Trim().Trim('"')).ProviderPath
$src = Join-Path $root 'src\CE.Tools.Civil3D'
$utf8 = New-Object System.Text.UTF8Encoding($false)

function ReadText([string]$name) {
    $path = Join-Path $src $name
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Legacy watcher source missing: $path"
    }
    return @($path,([System.IO.File]::ReadAllText($path) -replace "`r?`n","`r`n"))
}
function WriteText([string]$path,[string]$text) {
    [System.IO.File]::WriteAllText($path,($text -replace "`r?`n","`r`n"),$utf8)
}

# Platform's legacy manager must not retain database/Idle subscriptions. The
# explicit platform commands and CE_PLATFORMREFRESH remain unaffected.
$item = ReadText 'PlatformProductionCommands.cs'
$platformPath = $item[0]
$platform = $item[1]
foreach ($line in @(
    '            AcApplication.DocumentManager.DocumentActivated += Activated;',
    '            AcApplication.DocumentManager.DocumentCreated += Activated;',
    '            AcApplication.DocumentManager.DocumentToBeDestroyed += Destroyed;',
    '            AcApplication.Idle += Idle;',
    '            _database.ObjectModified += Changed;',
    '            _database.ObjectErased += Erased;',
    '            _database.ObjectModified -= Changed;',
    '            _database.ObjectErased -= Erased;')) {
    $platform = $platform.Replace($line + "`r`n",'')
}
WriteText $platformPath $platform

# The legacy COGO style monitor previously started pending and forwarded COGO /
# Xrecord changes into Universal Refresh. Manual COGO style commands still work;
# the background monitor must never subscribe to Idle/database changes.
$item = ReadText 'CogoPointProjectStyleCommands.cs'
$cogoPath = $item[0]
$cogo = $item[1]
foreach ($line in @(
    '            AcApplication.Idle += OnIdle;',
    '            AcApplication.Idle -= OnIdle;',
    '            _database.ObjectModified += OnObjectChanged;',
    '            _database.ObjectAppended += OnObjectChanged;',
    '                _database.ObjectModified -= OnObjectChanged;',
    '                _database.ObjectAppended -= OnObjectChanged;')) {
    $cogo = $cogo.Replace($line + "`r`n",'')
}
WriteText $cogoPath $cogo

$platform = [System.IO.File]::ReadAllText($platformPath)
foreach ($forbidden in @(
    'AcApplication.Idle += Idle;',
    '_database.ObjectModified += Changed;',
    '_database.ObjectErased += Erased;')) {
    if ($platform.Contains($forbidden)) {
        throw "Platform legacy watcher remains: $forbidden"
    }
}
$cogo = [System.IO.File]::ReadAllText($cogoPath)
foreach ($forbidden in @(
    'AcApplication.Idle += OnIdle;',
    '_database.ObjectModified += OnObjectChanged;',
    '_database.ObjectAppended += OnObjectChanged;')) {
    if ($cogo.Contains($forbidden)) {
        throw "COGO legacy watcher remains: $forbidden"
    }
}

Write-Host 'Dormant Platform and COGO background watcher subscriptions were removed before final compilation.' -ForegroundColor Green
