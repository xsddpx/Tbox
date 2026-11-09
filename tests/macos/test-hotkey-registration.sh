#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
cd "$ROOT"
mkdir -p TEMP/native-hotkey-tests
swiftc -swift-version 5 src/Tbox.Desktop/Platform/macos/*.swift \
  tests/macos/HotkeyRegistrationTests.swift -o TEMP/native-hotkey-tests/hotkey-registration-tests
"$ROOT/TEMP/native-hotkey-tests/hotkey-registration-tests"
