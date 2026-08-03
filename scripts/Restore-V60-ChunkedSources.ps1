[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$RepoRoot
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = [System.IO.Path]::GetFullPath($RepoRoot.Trim().Trim('"'))
$chunkRoot = Join-Path $root 'recovery\v60\chunks'
$sourceRoot = Join-Path $root 'src\CE.Tools.Civil3D'
$targets = @(
    'CommentPresentationCommands.cs',
    'DynamicTypicalDetailEngine.cs',
    'ProductionCommentCommands.cs'
)

if (-not (Test-Path -LiteralPath $chunkRoot -PathType Container)) {
    throw "V60 recovery chunk directory was not found: $chunkRoot"
}

$chunks = Get-ChildItem -LiteralPath $chunkRoot -File -Filter '*.b64part' | Sort-Object Name
if ($chunks.Count -ne 3) {
    throw "Expected exactly 3 V60 recovery chunks but found $($chunks.Count)."
}

$base64Builder = New-Object System.Text.StringBuilder
foreach ($chunk in $chunks) {
    [void]$base64Builder.Append([System.IO.File]::ReadAllText($chunk.FullName).Trim())
}
$base64 = $base64Builder.ToString()
if (($base64.Length % 4) -ne 0) {
    throw "The combined V60 recovery Base64 length is invalid: $($base64.Length)."
}

try {
    $bytes = [Convert]::FromBase64String($base64)
}
catch {
    throw "The combined V60 recovery archive could not be decoded. $($_.Exception.Message)"
}
if ($bytes.Length -lt 4 -or $bytes[0] -ne 0x50 -or $bytes[1] -ne 0x4B) {
    throw 'The combined V60 recovery archive is not a ZIP file.'
}

$temp = Join-Path $env:TEMP ('CE-Tools-V60-Chunked-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $temp | Out-Null
try {
    $zip = Join-Path $temp 'v60-support.zip'
    [System.IO.File]::WriteAllBytes($zip, $bytes)
    Expand-Archive -LiteralPath $zip -DestinationPath $temp -Force

    New-Item -ItemType Directory -Force -Path $sourceRoot | Out-Null
    foreach ($name in $targets) {
        $from = Join-Path $temp $name
        if (-not (Test-Path -LiteralPath $from -PathType Leaf)) {
            throw "Recovered V60 source is missing from the archive: $name"
        }
        Copy-Item -LiteralPath $from -Destination (Join-Path $sourceRoot $name) -Force
        Write-Host "Restored V60 source: $name" -ForegroundColor Green
    }
}
finally {
    Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host 'Verified V60 support-source restoration completed.' -ForegroundColor Cyan
