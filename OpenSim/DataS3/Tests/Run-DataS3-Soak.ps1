param(
    [string]$RepoRoot = "D:\OpenSim-Data\opensim",
    [string]$NUnitConsole = "nunit-console",
    [int]$DurationHours = 24,
    [int]$DurationMinutes = 0,
    [int]$IterationPauseSeconds = 60,
    [int]$MaxIterations = 0,
    [string]$ProfilePath = "",
    [switch]$SkipReadinessCheck,
    [switch]$GenerateSummary,
    [switch]$WhatIfMode
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if ($DurationHours -le 0 -and $DurationMinutes -le 0) {
    throw "DurationHours oder DurationMinutes muss groesser als 0 sein."
}

if ([string]::IsNullOrWhiteSpace($ProfilePath)) {
    $ProfilePath = Join-Path $RepoRoot "OpenSim\DataS3\Tests\soak-profile.default.psd1"
}

if (-not (Test-Path $ProfilePath)) {
    throw "Soak-Profil nicht gefunden: $ProfilePath"
}

$profile = Import-PowerShellDataFile -Path $ProfilePath
$modes = @($profile.TestModes)
if ($modes.Count -eq 0) {
    throw "Soak-Profil enthaelt keine TestModes."
}

$resultsRoot = Join-Path $RepoRoot "OpenSim\DataS3\Tests\results\soak"
if (-not (Test-Path $resultsRoot)) {
    New-Item -ItemType Directory -Path $resultsRoot -Force | Out-Null
}

$runner = Join-Path $RepoRoot "OpenSim\DataS3\Tests\Run-DataS3-NUnit.ps1"
if (-not (Test-Path $runner)) {
    throw "Runner nicht gefunden: $runner"
}

if (-not $SkipReadinessCheck) {
    $readinessScript = Join-Path $RepoRoot "OpenSim\DataS3\Tests\Test-DataS3-TestReadiness.ps1"
    if (Test-Path $readinessScript) {
        $readinessReport = Join-Path $resultsRoot ("readiness-{0}.json" -f (Get-Date -Format "yyyyMMdd-HHmmss"))
        & $readinessScript -RepoRoot $RepoRoot -NUnitConsole $NUnitConsole -ReportPath $readinessReport

        if (-not $WhatIfMode -and $LASTEXITCODE -ne 0) {
            throw "Readiness-Check fehlgeschlagen. Details: $readinessReport"
        }
    }
    else {
        Write-Warning "Readiness-Skript nicht gefunden: $readinessScript"
    }
}

$started = Get-Date
$until = $started.AddHours($DurationHours)
if ($DurationMinutes -gt 0) {
    $until = $until.AddMinutes($DurationMinutes)
}
$iteration = 0

while ((Get-Date) -lt $until) {
    if ($MaxIterations -gt 0 -and $iteration -ge $MaxIterations) {
        break
    }

    $iteration++
    $iterDir = Join-Path $resultsRoot ("iter-{0:0000}" -f $iteration)
    New-Item -ItemType Directory -Path $iterDir -Force | Out-Null

    Write-Host "Soak iteration $iteration gestartet. Output: $iterDir"

    foreach ($mode in $modes) {
        & $runner -RepoRoot $RepoRoot -NUnitConsole $NUnitConsole -Mode $mode -OutputDir $iterDir -WhatIfMode:$WhatIfMode -GenerateSummary:$GenerateSummary
    }

    $now = Get-Date
    if ($now -lt $until -and $IterationPauseSeconds -gt 0) {
        Start-Sleep -Seconds $IterationPauseSeconds
    }
}

if ($GenerateSummary) {
    $summaryScript = Join-Path $RepoRoot "OpenSim\DataS3\Tests\Summarize-DataS3-NUnitResults.ps1"
    if (Test-Path $summaryScript) {
        $summaryPath = Join-Path $resultsRoot ("soak-summary-{0}.json" -f (Get-Date -Format "yyyyMMdd-HHmmss"))
        & $summaryScript -ResultsRoot $resultsRoot -JsonOut $summaryPath
    }
    else {
        Write-Warning "Summary-Skript nicht gefunden: $summaryScript"
    }
}

Write-Host "DataS3 Soak-Lauf abgeschlossen. Iterationen: $iteration, Start: $started, Ende: $(Get-Date)"
