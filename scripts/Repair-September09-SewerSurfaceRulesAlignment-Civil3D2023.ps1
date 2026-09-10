[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$RepoRoot)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$dq = [char]34
$root = (Resolve-Path -LiteralPath $RepoRoot.Trim().Trim($dq)).ProviderPath
$src = Join-Path $root 'src\CE.Tools.Civil3D'
$utf8 = New-Object System.Text.UTF8Encoding($false)

function Required([string]$name) {
    $path = Join-Path $src $name
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw ('September 09 sewer surface/rules source missing: {0}' -f $path)
    }
    return $path
}
function ReadText([string]$path) {
    return [System.IO.File]::ReadAllText($path) -replace '\r?\n',"`r`n"
}
function WriteText([string]$path,[string]$text) {
    [System.IO.File]::WriteAllText($path,($text -replace '\r?\n',"`r`n"),$utf8)
}
function MethodBounds([string]$text,[string]$marker) {
    $start = $text.IndexOf($marker,[StringComparison]::Ordinal)
    if ($start -lt 0) { throw ('September 09 sewer method marker missing: {0}' -f $marker) }
    $open = $text.IndexOf('{',$start)
    if ($open -lt 0) { throw ('September 09 sewer opening brace missing: {0}' -f $marker) }
    $depth = 0
    $close = -1
    for ($i=$open; $i -lt $text.Length; $i++) {
        if ($text[$i] -eq '{') { $depth++ }
        elseif ($text[$i] -eq '}') {
            $depth--
            if ($depth -eq 0) { $close = $i; break }
        }
    }
    if ($close -lt 0) { throw ('September 09 sewer closing brace missing: {0}' -f $marker) }
    return [pscustomobject]@{ Open=$open; Close=$close }
}
function ReplaceMethodBody([string]$text,[string]$marker,[string]$body) {
    $bounds = MethodBounds $text $marker
    $body = ($body -replace '\r?\n',"`r`n").Trim("`r","`n")
    return $text.Substring(0,$bounds.Open+1) + "`r`n" + $body + "`r`n        " + $text.Substring($bounds.Close)
}

$runtimePath = Required 'September09SewerSurfaceRulesRuntime.cs'
$networkPath = Required 'August13SewerMultiSourceNetworkCommands.cs'
$alignmentPath = Required 'SewerBranchAlignmentCommands.cs'
$menuPath = Required 'August24FieldCompletionCommands.cs'
$runnerPath = Required 'CeSequentialCommandRunner.cs'
$googleEarthPath = Required 'SurveyGoogleEarthCommands.cs'

$runtime = ReadText $runtimePath

# Earlier staged recovery sources can leave the completion marker call passing a
# Database even though NetworkSourceMarker.Mark requires the active Document.
# Repair that exact legacy call before compilation and guard the corrected form so
# a later staged source rewrite cannot silently reintroduce CS1503.
$legacySourceMarkerCall = 'NetworkSourceMarker.Mark(database, sourceId, "Sewer");'
$documentSourceMarkerCall = 'NetworkSourceMarker.Mark(document, sourceId, "Sewer");'
if ($runtime.Contains($legacySourceMarkerCall)) {
    $runtime = $runtime.Replace($legacySourceMarkerCall, $documentSourceMarkerCall)
    WriteText $runtimePath $runtime
}
if (-not $runtime.Contains($documentSourceMarkerCall)) {
    throw 'September 09 sewer source marker guard missing: NetworkSourceMarker.Mark must receive Document.'
}

# Civil 3D 2023 exposes the WinForms modal-dialog helper on the ApplicationServices
# Application type rather than the Core.Application alias used for DocumentManager.
# Also qualify WinForms FlowDirection so the DatabaseServices enum cannot collide.
$googleEarth = ReadText $googleEarthPath
$legacyModalDialog = 'AcApplication.ShowModalDialog(form);'
$compatibleModalDialog = 'Autodesk.AutoCAD.ApplicationServices.Application.ShowModalDialog(form);'
if ($googleEarth.Contains($legacyModalDialog)) {
    $googleEarth = $googleEarth.Replace($legacyModalDialog, $compatibleModalDialog)
}
$legacyFlowDirection = 'FlowDirection = FlowDirection.LeftToRight'
$compatibleFlowDirection = 'FlowDirection = System.Windows.Forms.FlowDirection.LeftToRight'
if ($googleEarth.Contains($legacyFlowDirection)) {
    $googleEarth = $googleEarth.Replace($legacyFlowDirection, $compatibleFlowDirection)
}
WriteText $googleEarthPath $googleEarth
if (-not $googleEarth.Contains($compatibleModalDialog)) {
    throw 'Survey Google Earth modal-dialog compatibility guard missing.'
}
if (-not $googleEarth.Contains($compatibleFlowDirection)) {
    throw 'Survey Google Earth FlowDirection compatibility guard missing.'
}

$requiredRuntimeTokens = @(
    'CE_SEWLINKSURFACE',
    'RefSurfaceId = surface.Id',
    'pipe.RuleSetStyleId = pipeRuleSetId',
    'structure.RuleSetStyleId = structureRuleSetId',
    'CreateBranchAlignmentsSafe(',
    'CreateOneAlignmentSafely(',
    'Critical Civil 3D safety boundary: commit the source entity first.',
    'Maximum structure spacing'
)
foreach ($token in $requiredRuntimeTokens) {
    if (-not $runtime.Contains($token)) {
        throw ('September 09 sewer runtime guard missing: {0}' -f $token)
    }
}

# Absolute final route for CE_SEWERNETWORKMULTI. Older staged recovery scripts may
# reconstruct August13SewerMultiSourceNetworkCommands.cs earlier in the build, so
# re-route the public command only after every September 09 field finalizer.
$network = ReadText $networkPath
$network = ReplaceMethodBody $network '        public void CreateSewerNetworkFromMultipleSources()' @'
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            CivilDocument civilDocument = CivilApplication.ActiveDocument;
            if (document == null || civilDocument == null) return;
            September09SewerSurfaceRulesRuntime.CreateNetworkFromMultipleSources(
                document,
                civilDocument);
'@
WriteText $networkPath $network

# Crash-safe final route for CE_SEWALIGN. Geometry is preflighted first and each
# temporary branch polyline is committed to the DWG before CivilAlignment.Create.
$alignment = ReadText $alignmentPath
$alignment = ReplaceMethodBody $alignment '        public void CreateBranchAlignments()' @'
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            CivilDocument civilDocument = CivilApplication.ActiveDocument;
            if (document == null || civilDocument == null) return;
            September09SewerSurfaceRulesRuntime.CreateBranchAlignmentsSafe(
                document,
                civilDocument);
'@
WriteText $alignmentPath $alignment

# Expose the existing-part relink command in the Sewer field supplementary popup.
$menu = ReadText $menuPath
$menu = $menu.Replace(
    'A("CE-Create Sewer Network from Multiple Sources", "CE_SEWERNETWORKMULTI", "Create one connected gravity network from lines, polylines or feature lines.", "01 Network preparation"),',
    'A("CE-Create Sewer Network from Multiple Sources", "CE_SEWERNETWORKMULTI", "Create one connected gravity network, select its reference surface, choose pipe/structure rule sets and apply rules after surface linking.", "01 Network preparation"),' + "`r`n" +
    '                    A("CE-Link Sewer Parts to Surface / Rules", "CE_SEWLINKSURFACE", "Link multiple existing sewer pipes/manholes to one selected surface and independently choose/apply pipe and structure rule sets.", "02 Surface / levels"),')
WriteText $menuPath $menu

# The centre-construction report showed a queued CE_SEWLABELS step leaking into a
# later manually-started command. The sequential runner must cancel pending queue
# steps as soon as another manual command begins between queued steps.
$runner = ReadText $runnerPath
foreach ($token in @(
    '_document.CommandWillStart += OnCommandWillStart;',
    'private static void OnCommandWillStart(',
    'remaining queued steps cancelled because',
    '_document.CommandWillStart -= OnCommandWillStart;'
)) {
    if (-not $runner.Contains($token)) {
        throw ('September 09 sequential-runner guard missing: {0}' -f $token)
    }
}

# Final staged-state guards.
$network = ReadText $networkPath
$alignment = ReadText $alignmentPath
$menu = ReadText $menuPath
if (-not $network.Contains('September09SewerSurfaceRulesRuntime.CreateNetworkFromMultipleSources(')) {
    throw 'CE_SEWERNETWORKMULTI was not routed to the September 09 surface/rules runtime.'
}
if (-not $alignment.Contains('September09SewerSurfaceRulesRuntime.CreateBranchAlignmentsSafe(')) {
    throw 'CE_SEWALIGN was not routed to the September 09 crash-safe alignment runtime.'
}
if (-not $menu.Contains('CE_SEWLINKSURFACE')) {
    throw 'CE_SEWLINKSURFACE is missing from the Sewer field supplementary menu.'
}

Write-Host 'September 09 sewer surface/rules + crash-safe alignment finalizer applied.'
