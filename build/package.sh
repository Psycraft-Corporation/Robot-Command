#!/usr/bin/env bash
set -euo pipefail

RID="${1:-linux-x64}"
VERSION="${2:-0.1.0-dev}"
ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
OUTPUT_DIR="$ROOT_DIR/artifacts/publish/$RID"
PACKAGE_DIR="$ROOT_DIR/artifacts/packages"
PROJECT="$ROOT_DIR/src/app/RobotCommand/RobotCommand.csproj"

rm -rf "$OUTPUT_DIR"
mkdir -p "$OUTPUT_DIR" "$PACKAGE_DIR"

dotnet publish "$PROJECT" \
  -c Release \
  -r "$RID" \
  --self-contained true \
  -p:PublishSingleFile=true \
  -p:PublishTrimmed=false \
  -p:Version="$VERSION" \
  -p:InformationalVersion="$VERSION" \
  -o "$OUTPUT_DIR"

ARCHIVE="$PACKAGE_DIR/RobotCommand-$VERSION-$RID.tar.gz"
tar -czf "$ARCHIVE" -C "$OUTPUT_DIR" .

echo "$ARCHIVE"
