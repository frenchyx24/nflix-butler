#!/usr/bin/env bash
# Builds the Nflix Butler plugin inside the .NET SDK image.
# The docker daemon runs on the TrueNAS host, so nothing is bind-mounted: the source is streamed
# in on stdin and the artifacts are streamed back out on stdout.
set -euo pipefail
SRC="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
OUT="${BUTLER_OUT:-$SRC/out}"
mkdir -p "$OUT"
LOG="${BUTLER_LOG:-/tmp/butler-build.log}"

tar -C "$SRC" --exclude='./out' --exclude='./obj' --exclude='./bin' -cf - . |
  docker run --rm -i -v butler-nuget:/root/.nuget/packages mcr.microsoft.com/dotnet/sdk:10.0 sh -c '
    set -e
    mkdir -p /src && tar -xf - -C /src
    cd /src/Jellyfin.Plugin.NflixButler
    dotnet build -c Release -o /src/out >&2
    tar -C /src/out -cf - .
  ' > "$OUT/butler-artifacts.tar" 2> "$LOG" || {
    echo "BUILD FAILED — log: $LOG" >&2
    grep -E ': error|MSBUILD : error' "$LOG" | sort -u | head -40 >&2
    exit 1
  }

rm -rf "$OUT/files"
mkdir -p "$OUT/files"
tar -C "$OUT/files" -xf "$OUT/butler-artifacts.tar"
cp "$SRC/pkg/meta.json" "$OUT/files/meta.json"
echo "BUILD OK: $OUT/files"
ls -1 "$OUT/files" | head -20
