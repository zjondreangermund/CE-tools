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

    $cleanEngineUrl = 'https://raw.githubusercontent.com/zjondreangermund/CE-tools/90dc8cde323c253c60f3bd4a3f9e343a7dd210a2/src/CE.Tools.Civil3D/DynamicTypicalDetailEngine.cs'
    $cleanEngineTemp = Join-Path $temp 'DynamicTypicalDetailEngine.clean.cs'
    Write-Host 'Downloading verified clean DynamicTypicalDetailEngine.cs...' -ForegroundColor Cyan
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    Invoke-WebRequest -UseBasicParsing -Uri $cleanEngineUrl -OutFile $cleanEngineTemp

    if (-not (Test-Path -LiteralPath $cleanEngineTemp -PathType Leaf)) {
        throw 'The verified clean dynamic-detail engine download was not created.'
    }
    $cleanText = [System.IO.File]::ReadAllText($cleanEngineTemp)
    if ($cleanText.Length -lt 10000 -or
        -not $cleanText.Contains('public sealed partial class DynamicTypicalDetailCommands') -or
        -not $cleanText.Contains('private static ObjectId CreateLinkedDetail') -or
        -not $cleanText.Contains('private static List<QuantityItem> CalculateQuantities')) {
        throw 'The downloaded dynamic-detail engine did not pass source integrity checks.'
    }

    $cleanDestination = Join-Path $sourceRoot 'DynamicTypicalDetailEngine.cs'
    [System.IO.File]::WriteAllText(
        $cleanDestination,
        ($cleanText -replace "`r?`n", "`r`n"),
        (New-Object System.Text.UTF8Encoding($false)))
    Write-Host 'Replaced corrupted dynamic-detail engine with verified clean source.' -ForegroundColor Green

    $v54Commit = '94e193dd425b8156dd40d1251da34b4bb0fc1b36'
    $v54SupportSources = @(
        'AnnotationCommands.cs',
        'ProfileStationInputWindow.cs',
        'AlignmentAnnotationLinkStore.cs',
        'ProfileAnnotationLinkStore.cs',
        'CorridorAnnotationLinkStore.cs',
        'NetworkAssetScheduleCommands.cs',
        'RoadCrossSectionScheduleCommands.cs',
        'StandardQuantityTemplateCommands.cs',
        'SewerExcavationCommentCommands.cs',
        'ParkingNumberLinkStore.cs',
        'ParkingReportLinkStore.cs',
        'PolylineDirectionCommands.cs',
        'FeatureLineRelativeCommands.cs',
        'DynamicCoordinateLinkStore.cs',
        'FeatureProfileSurfaceCommentCommands.cs'
    )

    Write-Host 'Restoring matching V54 comment-presentation and annotation support sources...' -ForegroundColor Cyan
    foreach ($name in $v54SupportSources) {
        $url = "https://raw.githubusercontent.com/zjondreangermund/CE-tools/$v54Commit/src/CE.Tools.Civil3D/$name"
        $download = Join-Path $temp ("v54-" + $name)
        Invoke-WebRequest -UseBasicParsing -Uri $url -OutFile $download

        if (-not (Test-Path -LiteralPath $download -PathType Leaf)) {
            throw "The V54 support-source download was not created: $name"
        }

        $text = [System.IO.File]::ReadAllText($download)
        $expectedType = [System.IO.Path]::GetFileNameWithoutExtension($name)
        $hasExpectedNamespace =
            $text.Contains('namespace CETools.Civil3D') -or
            $text.Contains('namespace CE.Tools.Civil3D')
        if ($text.Length -lt 200 -or
            -not $hasExpectedNamespace -or
            -not $text.Contains($expectedType)) {
            throw "The downloaded V54 support source failed integrity checks: $name"
        }

        [System.IO.File]::WriteAllText(
            (Join-Path $sourceRoot $name),
            ($text -replace "`r?`n", "`r`n"),
            (New-Object System.Text.UTF8Encoding($false)))
        Write-Host "Restored V54 support source: $name" -ForegroundColor Green
    }
}
finally {
    Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host 'Verified V60 and matching V54 support-source restoration completed.' -ForegroundColor Cyan
