#!/usr/bin/env bash
set -euo pipefail
ARCHIVE="$1"
EXPECTED_HASH="$2"
PARENT="$3"
MINIMUM_MACOS="$4"
SIGN_IDENTITY="$5"
CERT_SHA1="$6"
[[ "$EXPECTED_HASH" =~ ^[0-9A-Fa-f]{64}$ ]]
ACTUAL_HASH="$(shasum -a 256 "$ARCHIVE" | cut -d ' ' -f 1)"
[[ "$ACTUAL_HASH" == "$(printf '%s' "$EXPECTED_HASH" | tr 'A-F' 'a-f')" ]] || {
  echo '远程桌面引擎 ZIP 的 SHA-256 不匹配。' >&2; exit 1
}

WORK="$(mktemp -d "$(dirname "$PARENT")/tbox-remote-viewer.XXXXXX")"
trap 'rm -rf "$WORK"' EXIT
ditto -x -k "$ARCHIVE" "$WORK"
SOURCE="$WORK/RustDesk.app"
[[ -d "$SOURCE" && ! -L "$SOURCE" && -x "$SOURCE/Contents/MacOS/RustDesk" ]]
[[ "$(lipo -archs "$SOURCE/Contents/MacOS/RustDesk")" == arm64 ]]
[[ -f "$SOURCE/Contents/Resources/RustDesk-LICENCE.txt" ]]
[[ -f "$SOURCE/Contents/Resources/RustDesk-source.txt" ]]

mkdir -p "$PARENT/Contents/Applications"
VIEWER="$PARENT/Contents/Applications/TboxRemote.app"
[[ ! -e "$VIEWER" ]]
mv "$SOURCE" "$VIEWER"
mv "$VIEWER/Contents/MacOS/RustDesk" "$VIEWER/Contents/MacOS/TboxRemote"
python3 - "$VIEWER/Contents/Info.plist" "$MINIMUM_MACOS" <<'PY'
import pathlib
import plistlib
import sys

path = pathlib.Path(sys.argv[1])
info = plistlib.loads(path.read_bytes())
info.update(CFBundleIdentifier="com.xsddpx.tbox.remote", CFBundleExecutable="TboxRemote",
            CFBundleDisplayName="Tbox 远程桌面", CFBundleName="TboxRemote", LSMinimumSystemVersion=sys.argv[2])
info.pop("CFBundleURLTypes", None)
path.write_bytes(plistlib.dumps(info))
PY

while IFS= read -r -d '' payload; do
  case "$(file -b "$payload")" in
    *Mach-O*) codesign --force --sign "$SIGN_IDENTITY" --timestamp=none "$payload" ;;
  esac
done < <(find "$VIEWER/Contents" -type f ! -path "$VIEWER/Contents/MacOS/TboxRemote" -print0)
while IFS= read -r -d '' bundle; do
  codesign --force --sign "$SIGN_IDENTITY" --timestamp=none "$bundle"
done < <(find "$VIEWER/Contents" -depth -type d \( -name '*.framework' -o -name '*.app' -o -name '*.xpc' \) -print0)
codesign --force --sign "$SIGN_IDENTITY" --timestamp=none "$VIEWER"
codesign --verify --deep --strict "$VIEWER"
codesign --verify --strict -R "=identifier \"com.xsddpx.tbox.remote\" and certificate leaf = H\"$CERT_SHA1\"" "$VIEWER"
swift "$(dirname "$0")/../tests/macos/RemoteHostLaunchTests.swift" "$VIEWER/Contents/MacOS/TboxRemote"
