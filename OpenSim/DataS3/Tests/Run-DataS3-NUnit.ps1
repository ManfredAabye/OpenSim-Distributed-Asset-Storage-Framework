param(
    [string]$RepoRoot = "D:\OpenSim-Data\opensim",
    [string]$NUnitConsole = "nunit-console",
    [ValidateSet("multiuser", "failure", "all")]
    [string]$Mode = "all",
    [string]$OutputDir = "",
    [switch]$GenerateSummary,
    [switch]$WhatIfMode
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($OutputDir)) {
    $OutputDir = Join-Path $RepoRoot "OpenSim\DataS3\Tests\results"
}

if (-not (Test-Path $OutputDir)) {
    New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
}

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
            Where-Object {
                $_.Name -in @("nunit-console.exe", "nunit-console2.exe", "nunit3-console.exe")
            } |
            Select-Object -First 1

        if ($null -ne $found) {
            return $found.FullName
        }
    }

    throw "nunit-console konnte nicht gefunden werden. Bitte -NUnitConsole mit Pfad oder Command angeben."
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

function Get-TotalTestsFromTrx {
    param([string]$TrxPath)

    if (-not (Test-Path $TrxPath)) {
        return -1
    }

    try {
        [xml]$trx = Get-Content -Path $TrxPath -Raw
        $countersNode = $trx.SelectSingleNode("//*[local-name()='ResultSummary']/*[local-name()='Counters']")
        if ($null -eq $countersNode) {
            return -1
        }

        $totalAttr = $countersNode.Attributes["total"]
        if ($null -eq $totalAttr) {
            return -1
        }

        return [int]$totalAttr.Value
    }
    catch {
        return -1
    }
}

$testMap = @{
    "multiuser" = "OpenSim.DataS3.Tests.UploadRateLimiterMultiUserTests"
    "failure"   = "OpenSim.DataS3.Tests.HybridAssetDataProviderFailureTests"
}

$dataS3TestProject = Join-Path $RepoRoot "OpenSim\DataS3\Tests\OpenSim.DataS3.Tests.csproj"

$modesToRun = if ($Mode -eq "all") { @("multiuser", "failure") } else { @($Mode) }

if ($WhatIfMode) {
    Write-Host "WhatIfMode aktiv: Es werden keine Tests ausgefuehrt."

    try {
        $resolvedNUnit = Resolve-NUnitConsolePath -RepoRoot $RepoRoot -Requested $NUnitConsole
        Write-Host "NUnitConsole gefunden: $resolvedNUnit"
    }
    catch {
        Write-Warning "NUnitConsole nicht gefunden: $($_.Exception.Message)"
    }

    $resolvedAssemblies = @(Resolve-TestAssemblies -RepoRoot $RepoRoot)
    if ($resolvedAssemblies.Count -eq 0) {
        Write-Warning "Keine passenden Test-Assemblies gefunden (das ist im Dry-Run erlaubt)."
    }
    else {
        Write-Host "Gefundene Assemblies:"
        $resolvedAssemblies | ForEach-Object { Write-Host " - $_" }
    }

    Write-Host "Geplante Modi: $($modesToRun -join ', ')"
    exit 0
}

$nunitConsolePath = ""
try {
    $nunitConsolePath = Resolve-NUnitConsolePath -RepoRoot $RepoRoot -Requested $NUnitConsole
}
catch {
    $nunitConsolePath = ""
}

$assemblies = @(Resolve-TestAssemblies -RepoRoot $RepoRoot)

$canUseNUnitConsole = (-not [string]::IsNullOrWhiteSpace($nunitConsolePath)) -and ($assemblies.Count -gt 0)
$canUseDotnetTest = Test-Path $dataS3TestProject

if (-not $canUseNUnitConsole -and -not $canUseDotnetTest) {
    throw "Weder NUnit-Console-Pfad mit Test-Assemblies noch OpenSim.DataS3.Tests.csproj verfuegbar."
}

if ($canUseNUnitConsole) {
    Write-Host "NUnitConsole: $nunitConsolePath"
    Write-Host "Assemblies:"
    $assemblies | ForEach-Object { Write-Host " - $_" }
}
else {
    Write-Warning "NUnit-Console nicht verfuegbar. Verwende dotnet test mit: $dataS3TestProject"
}

foreach ($runMode in $modesToRun) {
    $targetClass = $testMap[$runMode]
    if ([string]::IsNullOrWhiteSpace($targetClass)) {
        throw "Unbekannter Testmodus: $runMode"
    }

    if ($canUseNUnitConsole) {
        $resultFile = Join-Path $OutputDir ("datas3-{0}-{1}.xml" -f $runMode, (Get-Date -Format "yyyyMMdd-HHmmss"))
        $executed = $false

        foreach ($assembly in $assemblies) {
            & $nunitConsolePath $assembly "/run:$targetClass" "/result:$resultFile"
            if ($LASTEXITCODE -eq 0) {
                $executed = $true
                break
            }

            Write-Host "NUnit run failed for $assembly / $targetClass with exit code $LASTEXITCODE"
        }

        if (-not $executed) {
            throw "Kein NUnit-Lauf erfolgreich fuer Modus '$runMode'."
        }
    }
    else {
        $trxFile = Join-Path $OutputDir ("datas3-{0}-{1}.trx" -f $runMode, (Get-Date -Format "yyyyMMdd-HHmmss"))
        $filter = "FullyQualifiedName~$targetClass"
        & dotnet test $dataS3TestProject --configuration Release --filter $filter --logger "trx;LogFileName=$trxFile"
        if ($LASTEXITCODE -ne 0) {
            throw "dotnet test fehlgeschlagen fuer Modus '$runMode' mit ExitCode $LASTEXITCODE"
        }

        $totalTests = Get-TotalTestsFromTrx -TrxPath $trxFile
        if ($totalTests -eq 0) {
            throw "dotnet test hat 0 Tests fuer Modus '$runMode' ausgefuehrt (Filter/Discovery pruefen)."
        }

        if ($GenerateSummary) {
            Write-Warning "GenerateSummary ist fuer dotnet test (TRX) aktuell nicht aktiviert."
        }
    }
}

if ($GenerateSummary) {
    $summaryScript = Join-Path $RepoRoot "OpenSim\DataS3\Tests\Summarize-DataS3-NUnitResults.ps1"
    if (Test-Path $summaryScript) {
        $summaryPath = Join-Path $OutputDir ("datas3-summary-{0}.json" -f (Get-Date -Format "yyyyMMdd-HHmmss"))
        & $summaryScript -ResultsRoot $OutputDir -JsonOut $summaryPath
    }
    else {
        Write-Warning "Summary-Skript nicht gefunden: $summaryScript"
    }
}

Write-Host "DataS3 NUnit-Runs abgeschlossen. Ergebnisse unter: $OutputDir"
