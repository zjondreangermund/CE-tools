[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$RepoRoot)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = (Resolve-Path -LiteralPath $RepoRoot.Trim().Trim('"')).ProviderPath
$path = Join-Path $root 'src\CE.Tools.Civil3D\StormwaterSequenceCommands.cs'
if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Stormwater sequence source missing: $path" }
$text = [System.IO.File]::ReadAllText($path)

# The old implementation returned 1.0 whenever GetPointAtParam could not expose
# usable geometry. That placeholder contaminates longest-main and branch ordering.
# Use Civil 3D's centre-to-centre 3D length first, then pipe curve geometry, then
# the already-read connected structure positions. Never invent a one-metre pipe.
$oldCall = '                double length = ReadPipeLength(pipe);'
$newCall = '                double length = ReadPipeLength(pipe, start.Position, end.Position);'
if ($text.Contains($oldCall)) {
    $text = $text.Replace($oldCall,$newCall)
}
elseif (-not $text.Contains($newCall)) {
    throw 'Stormwater ReadPipeLength call marker was not found.'
}

$pattern = '(?s)        private static double ReadPipeLength\(CivilPipe pipe\)\s*        \{.*?\n        \}\s*(?=\n        private static void ValidateConnected)'
$replacement = @'
        private static double ReadPipeLength(CivilPipe pipe, Point3d startStructure, Point3d endStructure)
        {
            if (pipe == null)
                throw new InvalidOperationException("A stormwater pipe could not be opened for length calculation.");

            double length = double.NaN;
            try { length = pipe.Length3DCenterToCenter; }
            catch { }
            if (!double.IsNaN(length) && !double.IsInfinity(length) && length > LengthTolerance)
                return length;

            try
            {
                double startParam = pipe.StartParam;
                double endParam = pipe.EndParam;
                Point3d start = pipe.GetPointAtParam(startParam);
                Point3d end = pipe.GetPointAtParam(endParam);
                length = start.DistanceTo(end);
            }
            catch
            {
                try
                {
                    Point3d start = pipe.GetPointAtParam(0.0);
                    Point3d end = pipe.GetPointAtParam(1.0);
                    length = start.DistanceTo(end);
                }
                catch { length = double.NaN; }
            }
            if (!double.IsNaN(length) && !double.IsInfinity(length) && length > LengthTolerance)
                return length;

            length = startStructure.DistanceTo(endStructure);
            if (!double.IsNaN(length) && !double.IsInfinity(length) && length > LengthTolerance)
                return length;

            throw new InvalidOperationException(
                "A stormwater pipe has no readable Civil 3D or geometric length. Rebuild/connect the pipe before sequencing.");
        }
'@
if (-not $text.Contains('pipe.Length3DCenterToCenter')) {
    $regex = [regex]::new($pattern,[System.Text.RegularExpressions.RegexOptions]::Singleline)
    if (-not $regex.IsMatch($text)) { throw 'Stormwater ReadPipeLength method could not be isolated.' }
    $text = $regex.Replace($text,$replacement.TrimEnd("`r","`n"),1)
}

if ($text.Contains('return 1.0;')) {
    throw 'Stormwater one-metre fallback remains after repair.'
}
if (-not $text.Contains('pipe.Length3DCenterToCenter') -or
    -not $text.Contains('startStructure.DistanceTo(endStructure)')) {
    throw 'Stormwater pipe-length repair verification failed.'
}

[System.IO.File]::WriteAllText($path,$text,[System.Text.UTF8Encoding]::new($false))
Write-Host 'Stormwater pipe lengths now use Civil/geometry values with no 1 m placeholder.' -ForegroundColor Green
