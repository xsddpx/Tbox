#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
cd "$ROOT"
OUTPUT="${1:-TEMP/native-ocr-tests}"
mkdir -p "$OUTPUT"
swiftc -swift-version 5 src/Tbox.Desktop/Platform/macos/*.swift \
  tests/macos/OcrMetadataTests.swift -o "$OUTPUT/ocr-metadata-tests"
"$OUTPUT/ocr-metadata-tests" "$OUTPUT"
