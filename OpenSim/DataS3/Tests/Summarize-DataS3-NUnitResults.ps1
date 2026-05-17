param(
    [string]$ResultsRoot = "",
    [string]$JsonOut = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($ResultsRoot)) {
    throw "ResultsRoot muss gesetzt sein."
}

if (-not (Test-Path $ResultsRoot)) {
    throw "ResultsRoot nicht gefunden: $ResultsRoot"
}

$xmlFiles = @(Get-ChildItem -Path $ResultsRoot -Recurse -File -Filter "*.xml" -ErrorAction SilentlyContinue)

$summaryRows = @()
$totalTest = 0
$totalPassed = 0
$totalFailed = 0
$totalInconclusive = 0

foreach ($file in $xmlFiles) {
    try {
        [xml]$doc = Get-Content -Path $file.FullName -Raw
    }
    catch {
        Write-Warning "Ungueltige XML uebersprungen: $($file.FullName)"
        continue
    }

    $counterNode = $doc.SelectSingleNode("//test-results")
    if ($null -eq $counterNode) {
        $counterNode = $doc.SelectSingleNode("//test-run")
    }

    if ($null -eq $counterNode) {
        Write-Warning "Kein erkennbarer NUnit-Wurzelknoten in: $($file.FullName)"
        continue
    }

    $total = [int]($counterNode.total ?? 0)
    $passed = [int]($counterNode.passed ?? 0)
    $failed = [int]($counterNode.failed ?? 0)
    $inconclusive = [int]($counterNode.inconclusive ?? 0)

    $totalTest += $total
    $totalPassed += $passed
    $totalFailed += $failed
    $totalInconclusive += $inconclusive

    $summaryRows += [pscustomobject]@{
        File = $file.FullName
        Total = $total
        Passed = $passed
        Failed = $failed
        Inconclusive = $inconclusive
    }
}

$result = [ordered]@{
    TimestampUtc = [DateTime]::UtcNow.ToString("o")
    ResultsRoot = $ResultsRoot
    FilesParsed = $summaryRows.Count
    Total = $totalTest
    Passed = $totalPassed
    Failed = $totalFailed
    Inconclusive = $totalInconclusive
    Success = ($totalFailed -eq 0)
    Files = $summaryRows
}

Write-Host "DataS3 NUnit Result Summary"
Write-Host "- ResultsRoot: $ResultsRoot"
Write-Host "- FilesParsed: $($result.FilesParsed)"
Write-Host "- Total: $($result.Total), Passed: $($result.Passed), Failed: $($result.Failed), Inconclusive: $($result.Inconclusive)"
Write-Host "- Success: $($result.Success)"

if (-not [string]::IsNullOrWhiteSpace($JsonOut)) {
    $jsonDir = Split-Path -Path $JsonOut -Parent
    if (-not [string]::IsNullOrWhiteSpace($jsonDir) -and -not (Test-Path $jsonDir)) {
        New-Item -ItemType Directory -Path $jsonDir -Force | Out-Null
    }

    $result | ConvertTo-Json -Depth 6 | Out-File -FilePath $JsonOut -Encoding UTF8
    Write-Host "- JSON geschrieben: $JsonOut"
}

if ($result.Success) {
    exit 0
}

exit 1
