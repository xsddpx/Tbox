#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
cd "$ROOT"
mkdir -p TEMP/native-folder-tests
swiftc -swift-version 5 src/Tbox.Desktop/Platform/macos/*.swift \
  tests/macos/FolderPanelPolicyTests.swift -o TEMP/native-folder-tests/folder-panel-tests
TEMP/native-folder-tests/folder-panel-tests
