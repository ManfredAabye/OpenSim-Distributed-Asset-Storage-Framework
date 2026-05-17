param(
    [string]$RepoRoot = "D:\OpenSim-Data\opensim",
    [string]$NUnitConsole = "nunit-console",
    [string]$ReportPath = "",
    [switch]$AttemptBuild,
    [ValidateSet("Debug", "Release")]
    [string]$BuildConfiguration = "Release",
    [switch]$WhatIfMode
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Resolve-NUnitConsolePath {
    param(
        [string]$RepoRoot,
        [string]$Requested
    )

    if (-not [string]::IsNullOrWhiteSpace($Requested)) {
        $requestedCommand = Get-Command $Requested -ErrorAction SilentlyContinue
        if ($null -ne $requestedCommand) {
            return $requestedCommand.Source
        }

        if (Test-Path $Requested) {
            return (Resolve-Path $Requested).Path
        }
    }

    $candidateRoots = @(
        (Join-Path $RepoRoot "bin"),
        (Join-Path $RepoRoot "packages"),
        (Join-Path $RepoRoot "bin\lib"),
        (Join-Path $RepoRoot "bin\lib64")
    ) | Select-Object -Unique

    foreach ($root in $candidateRoots) {
        if (-not (Test-Path $root)) {
            continue
        }

        $found = Get-ChildItem -Path $root -Recurse -File -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -in @("nunit-console.exe", "nunit-console2.exe", "nunit3-console.exe") } |
            Select-Object -First 1

        if ($null -ne $found) {
            return $found.FullName
        }
    }

    return ""
}

function Resolve-TestAssemblies {
    param([string]$RepoRoot)

    $roots = @(
        (Join-Path $RepoRoot "bin"),
        (Join-Path $RepoRoot "OpenSim\bin")
    ) | Select-Object -Unique

    $nameAllowList = @(
        "OpenSim.DataS3.Tests.dll",
        "Robust.Tests.dll",
        "OpenSim.Framework.Tests.dll"
    )

    $assemblies = @()
    foreach ($root in $roots) {
        if (-not (Test-Path $root)) {
            continue
        }

        $assemblies += Get-ChildItem -Path $root -Recurse -File -Filter "*.dll" -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -in $nameAllowList } |
            Select-Object -ExpandProperty FullName
    }

    return @($assemblies | Select-Object -Unique)
}

$resolvedNUnit = Resolve-NUnitConsolePath -RepoRoot $RepoRoot -Requested $NUnitConsole
$assemblies = @(Resolve-TestAssemblies -RepoRoot $RepoRoot)
$dataS3TestProject = Join-Path $RepoRoot "OpenSim\DataS3\Tests\OpenSim.DataS3.Tests.csproj"
$dotnetTestProjectFound = Test-Path $dataS3TestProject

if ($AttemptBuild -and $assemblies.Count -eq 0) {
    $buildScript = Join-Path $RepoRoot "OpenSim\DataS3\Tests\Build-DataS3-TestAssemblies.ps1"
    if (-not (Test-Path $buildScript)) {
        throw "Build-Skript nicht gefunden: $buildScript"
    }

    Write-Host "- AttemptBuild aktiv: versuche Test-Assemblies zu erzeugen"
    & $buildScript -RepoRoot $RepoRoot -Configuration $BuildConfiguration -ForceRegenerateSolution -WhatIfMode:$WhatIfMode
    if (-not $WhatIfMode -and $LASTEXITCODE -ne 0) {
        throw "Build-Skript fehlgeschlagen mit ExitCode $LASTEXITCODE"
    }

    $assemblies = @(Resolve-TestAssemblies -RepoRoot $RepoRoot)
}

$report = [ordered]@{
    TimestampUtc = [DateTime]::UtcNow.ToString("o")
    RepoRoot = $RepoRoot
    NUnitConsoleResolved = $resolvedNUnit
    NUnitConsoleFound = -not [string]::IsNullOrWhiteSpace($resolvedNUnit)
    AssembliesFound = $assemblies.Count
    Assemblies = $assemblies
    DotnetTestProject = $dataS3TestProject
    DotnetTestProjectFound = $dotnetTestProjectFound
    ReadyForExecution = (((-not [string]::IsNullOrWhiteSpace($resolvedNUnit)) -and $assemblies.Count -gt 0) -or $dotnetTestProjectFound)
    AttemptBuild = $AttemptBuild
    BuildConfiguration = $BuildConfiguration
    WhatIfMode = $WhatIfMode
}

Write-Host "DataS3 Test Readiness"
Write-Host "- RepoRoot: $($report.RepoRoot)"
Write-Host "- NUnitConsoleFound: $($report.NUnitConsoleFound)"
if ($report.NUnitConsoleFound) {
    Write-Host "- NUnitConsoleResolved: $($report.NUnitConsoleResolved)"
}
Write-Host "- AssembliesFound: $($report.AssembliesFound)"
$assemblies | ForEach-Object { Write-Host "  - $_" }
Write-Host "- ReadyForExecution: $($report.ReadyForExecution)"

if (-not [string]::IsNullOrWhiteSpace($ReportPath)) {
    $reportDir = Split-Path -Path $ReportPath -Parent
    if (-not [string]::IsNullOrWhiteSpace($reportDir) -and -not (Test-Path $reportDir)) {
        New-Item -ItemType Directory -Path $reportDir -Force | Out-Null
    }

    $report | ConvertTo-Json -Depth 4 | Out-File -FilePath $ReportPath -Encoding UTF8
    Write-Host "- Report geschrieben: $ReportPath"
}

if ($report.ReadyForExecution) {
    exit 0
}

if ($WhatIfMode) {
    Write-Host "- WhatIfMode aktiv: nicht bereit, aber Dry-Run wird als erfolgreich gewertet."
    exit 0
}

exit 2
