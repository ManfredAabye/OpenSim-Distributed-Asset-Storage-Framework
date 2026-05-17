<#
.SYNOPSIS
    Lädt die MinIO-Server-Binaries für alle Plattformen herunter.

.DESCRIPTION
    Dieses Skript lädt die MinIO-Binaries in die korrekten Unterverzeichnisse:
      bin/minio/win-x64/minio.exe
      bin/minio/linux-x64/minio
      bin/minio/osx-x64/minio
      bin/minio/osx-arm64/minio

    Ausführen vom Repository-Root aus:
      pwsh bin/minio/Get-MinIO.ps1

.PARAMETER Release
    MinIO Release-Tag, z.B. RELEASE.2025-09-07T16-13-09Z

.PARAMETER Platform
    Plattform: All | win-x64 | linux-x64 | osx-x64 | osx-arm64 (Standard: All)
#>
param(
    [string]$Release  = "RELEASE.2025-09-07T16-13-09Z",
    [ValidateSet("All","win-x64","linux-x64","osx-x64","osx-arm64")]
    [string]$Platform = "All"
)

$base = "https://dl.min.io/server/minio/release"

$targets = @{
    "win-x64"   = @{ Url = "$base/windows-amd64/archive/minio.$Release.exe"; File = "minio.exe" }
    "linux-x64" = @{ Url = "$base/linux-amd64/archive/minio.$Release";       File = "minio"     }
    "osx-x64"   = @{ Url = "$base/darwin-amd64/archive/minio.$Release";       File = "minio"     }
    "osx-arm64" = @{ Url = "$base/darwin-arm64/archive/minio.$Release";       File = "minio"     }
}

$scriptDir = Split-Path $MyInvocation.MyCommand.Path
$repoRoot  = Split-Path (Split-Path $scriptDir)

$selected = if ($Platform -eq "All") { $targets.Keys } else { @($Platform) }

foreach ($rid in $selected) {
    $entry   = $targets[$rid]
    $outDir  = Join-Path $repoRoot "bin\minio\$rid"
    $outFile = Join-Path $outDir $entry.File

    if (Test-Path $outFile) {
        Write-Host "[$rid] Bereits vorhanden: $outFile – übersprungen." -ForegroundColor Cyan
        continue
    }

    Write-Host "[$rid] Lade herunter: $($entry.Url)" -ForegroundColor Yellow
    New-Item -ItemType Directory -Force -Path $outDir | Out-Null

    try {
        Invoke-WebRequest -Uri $entry.Url -OutFile $outFile -UseBasicParsing
        Write-Host "[$rid] OK → $outFile" -ForegroundColor Green

        # Auf Linux/macOS ausführbar machen (ignoriert auf Windows)
        if ($IsLinux -or $IsMacOS) {
            chmod +x $outFile 2>$null
        }
    }
    catch {
        Write-Warning "[$rid] Fehler beim Download: $_"
    }
}

Write-Host ""
Write-Host "Fertig. Starte MinIO lokal mit:" -ForegroundColor White
Write-Host "  bin/minio/win-x64/minio.exe server bin/minio/data --address :9000" -ForegroundColor Gray
