#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
FIXTURE="$(mktemp -d "${TMPDIR:-/tmp}/tbox-signature-test.XXXXXX")"
trap 'rm -rf "$FIXTURE"' EXIT
APP="$FIXTURE/Test.app"
mkdir -p "$APP/Contents/MacOS"
# This fixture is never installed or run. An ad hoc signature is the negative control.
cp /usr/bin/true "$APP/Contents/MacOS/Test"
cat > "$APP/Contents/Info.plist" <<'PLIST'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
<key>CFBundleIdentifier</key><string>com.xsddpx.tbox</string>
<key>CFBundleExecutable</key><string>Test</string>
<key>CFBundlePackageType</key><string>APPL</string>
</dict></plist>
PLIST
codesign --force --sign - "$APP" 2>/dev/null
codesign --verify --deep --strict "$APP"
if bash "$ROOT/scripts/check-macos-identity.sh" "$APP" > "$FIXTURE/output" 2>&1; then
  echo "测试失败：临时签名被接受。" >&2; exit 1
fi
grep -Fq '缺少持久证书签名身份' "$FIXTURE/output"
/usr/libexec/PlistBuddy -c 'Set CFBundleIdentifier com.example.other' "$APP/Contents/Info.plist"
if bash "$ROOT/scripts/check-macos-identity.sh" "$APP" > "$FIXTURE/output" 2>&1; then
  echo "测试失败：其他应用标识被接受。" >&2; exit 1
fi
grep -Fq '应用标识发生变化' "$FIXTURE/output"
echo "签名策略测试通过：拒绝临时身份和错误应用标识。"
