[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)]
    [string]$RepoRoot
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repo = [System.IO.Path]::GetFullPath($RepoRoot.Trim('"'))
$sourceRoot = Join-Path $repo 'src\CE.Tools.Civil3D'
if (-not (Test-Path -LiteralPath $sourceRoot)) {
    throw "Civil 3D source folder was not found: $sourceRoot"
}

$dotnet = (Get-Command dotnet.exe -ErrorAction Stop).Source
$sdkLine = (& $dotnet --list-sdks | Where-Object { $_ -match '^8\.0\.423\s' } | Select-Object -First 1)
if (-not $sdkLine) {
    throw 'The required .NET SDK 8.0.423 was not found.'
}
$sdkRoot = ($sdkLine -replace '^8\.0\.423\s+\[', '') -replace '\]$', ''
$csc = Join-Path $sdkRoot '8.0.423\Roslyn\bincore\csc.dll'
if (-not (Test-Path -LiteralPath $csc)) {
    throw "Roslyn compiler was not found: $csc"
}

$temp = Join-Path $env:TEMP ('CE-Tools-Roslyn-Diagnosis-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temp -Force | Out-Null

try {
    $files = Get-ChildItem -LiteralPath $sourceRoot -Filter '*.cs' -File | Sort-Object Name
    Write-Host "Checking $($files.Count) Civil 3D source files individually for a Roslyn parser crash..." -ForegroundColor Cyan

    $crashFiles = New-Object System.Collections.Generic.List[string]
    foreach ($file in $files) {
        $text = [System.IO.File]::ReadAllText($file.FullName, [System.Text.UTF8Encoding]::new($false, $true))
        $copy = Join-Path $temp $file.Name
        [System.IO.File]::WriteAllText($copy, $text, [System.Text.UTF8Encoding]::new($false))
        $out = Join-Path $temp ($file.BaseName + '.dll')

        # Native compiler crashes write to stderr and return non-zero. PowerShell 7
        # can promote that stderr into a terminating NativeCommandError when the
        # script preference is Stop, so temporarily allow the output to be captured.
        $previousPreference = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        try {
            $result = (& $dotnet $csc '/noconfig' '/nostdlib+' '/target:library' '/langversion:latest' "/out:$out" $copy 2>&1 | Out-String)
            $compilerExitCode = $LASTEXITCODE
        }
        finally {
            $ErrorActionPreference = $previousPreference
        }

        if ($result -match 'ArgumentOutOfRangeException' -or
            $result -match 'TextSpan\.\.ctor' -or
            $result -match 'Process terminated') {
            $crashFiles.Add($file.FullName)
            Write-Host "Roslyn parser crash reproduced in: $($file.Name)" -ForegroundColor Red
        }
        elseif ($compilerExitCode -ne 0) {
            # Ordinary missing-reference or syntax diagnostics are expected when a
            # source file is compiled by itself. They are not parser crashes.
            Write-Host "Checked: $($file.Name)" -ForegroundColor DarkGray
        }
    }

    if ($crashFiles.Count -eq 0) {
        Write-Host 'No individual source file reproduced the Roslyn parser crash.' -ForegroundColor Yellow
        Write-Host 'The full build will now continue so the next diagnostic can isolate a file combination if needed.' -ForegroundColor Yellow
        return
    }

    $report = Join-Path $repo 'artifacts\roslyn-crash-files.txt'
    New-Item -ItemType Directory -Path (Split-Path -Parent $report) -Force | Out-Null
    $crashFiles | Set-Content -LiteralPath $report -Encoding UTF8
    throw "Roslyn parser crash source file(s) identified. Report: $report`n$($crashFiles -join "`n")"
}
finally {
    Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue
}
