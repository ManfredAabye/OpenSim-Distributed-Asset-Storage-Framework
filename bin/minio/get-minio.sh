#!/usr/bin/env bash
# get-minio.sh – Lädt MinIO-Binaries für Linux und macOS herunter
# Verwendung: bash bin/minio/get-minio.sh [all|linux-x64|osx-x64|osx-arm64]

set -euo pipefail

RELEASE="RELEASE.2025-09-07T16-13-09Z"
BASE="https://dl.min.io/server/minio/release"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"

PLATFORM="${1:-all}"

download() {
    local rid="$1" url="$2" file="$3"
    local out="$REPO_ROOT/bin/minio/$rid/$file"
    local dir
    dir="$(dirname "$out")"

    if [ -f "$out" ]; then
        echo "[$rid] Bereits vorhanden: $out – übersprungen."
        return
    fi

    mkdir -p "$dir"
    echo "[$rid] Lade herunter: $url"
    curl -fL -o "$out" "$url"
    chmod +x "$out"
    echo "[$rid] OK → $out"
}

case "$PLATFORM" in
    all|linux-x64)
        download "linux-x64" "$BASE/linux-amd64/archive/minio.$RELEASE" "minio" ;;& 
    all|osx-x64)
        download "osx-x64"   "$BASE/darwin-amd64/archive/minio.$RELEASE" "minio" ;;&
    all|osx-arm64)
        download "osx-arm64" "$BASE/darwin-arm64/archive/minio.$RELEASE"  "minio" ;;&
    all) ;;
    *)
        echo "Unbekannte Plattform: $PLATFORM"; exit 1 ;;
esac

echo ""
echo "Fertig. Starte MinIO lokal mit:"
echo "  bin/minio/linux-x64/minio server bin/minio/data --address :9000"
