param(
    [string]$RepoRoot = "D:\OpenSim-Data\opensim",
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [switch]$ForceRegenerateSolution,
    [switch]$WhatIfMode
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$solutionPath = Join-Path $RepoRoot "OpenSim.sln"
$prebuildBatch = Join-Path $RepoRoot "runprebuild.bat"
$prebuildXml = Join-Path $RepoRoot "prebuild.xml"
$dataS3Project = Join-Path $RepoRoot "OpenSim\DataS3\OpenSim.DataS3.csproj"
$dataS3TestProject = Join-Path $RepoRoot "OpenSim\DataS3\Tests\OpenSim.DataS3.Tests.csproj"

if (-not (Test-Path $prebuildBatch)) {
    throw "runprebuild.bat nicht gefunden: $prebuildBatch"
}

$needPrebuild = $ForceRegenerateSolution -or -not (Test-Path $solutionPath)

if (-not $needPrebuild -and (Test-Path $prebuildXml)) {
    $slnTime = (Get-Item $solutionPath).LastWriteTimeUtc
    $prebuildTime = (Get-Item $prebuildXml).LastWriteTimeUtc
    if ($prebuildTime -gt $slnTime) {
        $needPrebuild = $true
    }
}

if (-not $needPrebuild -and (Test-Path $solutionPath)) {
    $slnHasDataS3Tests = Select-String -Path $solutionPath -Pattern "OpenSim.DataS3.Tests" -SimpleMatch -Quiet
    if (-not $slnHasDataS3Tests) {
        $needPrebuild = $true
    }
}

if ($needPrebuild) {
    if ($WhatIfMode) {
        Write-Host "WhatIfMode: wuerde runprebuild ausfuehren: $prebuildBatch"
    }
    else {
        Write-Host "Generiere/Openisiere OpenSim.sln via runprebuild.bat"
        & $prebuildBatch
        if ($LASTEXITCODE -ne 0) {
            throw "runprebuild.bat fehlgeschlagen mit ExitCode $LASTEXITCODE"
        }
    }
}

if (-not (Test-Path $solutionPath) -and -not $WhatIfMode) {
    throw "OpenSim.sln konnte nicht erzeugt werden."
}

if (-not $WhatIfMode -and (Test-Path $solutionPath)) {
    $slnHasDataS3Tests = Select-String -Path $solutionPath -Pattern "OpenSim.DataS3.Tests" -SimpleMatch -Quiet
    if (-not $slnHasDataS3Tests) {
        Write-Warning "OpenSim.sln enthaelt OpenSim.DataS3.Tests nicht. Verwende dediziertes DataS3-Testprojekt fuer Build."
    }
}

$buildTargets = @()
if (Test-Path $dataS3Project) {
    $buildTargets += $dataS3Project
}

if (Test-Path $dataS3TestProject) {
    $buildTargets += $dataS3TestProject
}

if ($buildTargets.Count -eq 0) {
    if (Test-Path $solutionPath) {
        $buildTargets += $solutionPath
    }
}

if ($buildTargets.Count -eq 0 -and -not $WhatIfMode) {
    throw "Kein gueltiges Build-Target gefunden."
}

foreach ($target in $buildTargets) {
    $buildCmd = "dotnet build `"$target`" --configuration $Configuration"
    if ($WhatIfMode) {
        Write-Host "WhatIfMode: wuerde ausfuehren: $buildCmd"
    }
    else {
        Write-Host "Baue via dotnet build: $target"
        & dotnet build $target --configuration $Configuration
        if ($LASTEXITCODE -ne 0) {
            throw "dotnet build fehlgeschlagen fuer $target mit ExitCode $LASTEXITCODE"
        }
    }
}

# Remove stale multi-target output folders from earlier net8/net9/net10 builds.
$staleRootPaths = @(
    (Join-Path $RepoRoot "OpenSim\DataS3\Tests\bin"),
    (Join-Path $RepoRoot "OpenSim\DataS3\Tests\obj")
)

foreach ($rootPath in $staleRootPaths) {
    if (-not (Test-Path $rootPath)) {
        continue
    }

    Get-ChildItem -Path $rootPath -Recurse -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match '^net(8\.0|9\.0|10\.0)$' } |
        Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
}

$expected = @(
    (Join-Path $RepoRoot "bin\OpenSim.DataS3.Tests.dll"),
    (Join-Path $RepoRoot "OpenSim\DataS3\Tests\bin\Release\OpenSim.DataS3.Tests.dll"),
    (Join-Path $RepoRoot "bin\OpenSim.Framework.Tests.dll"),
    (Join-Path $RepoRoot "bin\Robust.Tests.dll")
)

$found = @($expected | Where-Object { Test-Path $_ })

Write-Host "Build-Check:"
$expected | ForEach-Object {
    $present = Test-Path $_
    Write-Host "- $($_): $present"
}

if ($WhatIfMode) {
    exit 0
}

if ($found.Count -eq 0) {
    throw "Keine erwartete Test-Assembly gefunden."
}

Write-Host "Build erfolgreich. Gefundene Test-Assemblies: $($found.Count)"
