#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
SOURCE="$ROOT/TEMP/build/osx-arm64/Tbox.app"
DESTINATION="$HOME/Applications/Tbox.app"
ALLOW_MIGRATION="${1:-}"
[[ -d "$SOURCE" ]] || { echo "先执行 Mac 构建。" >&2; exit 1; }
bash "$ROOT/scripts/check-macos-identity.sh" "$SOURCE" >/dev/null
IDENTIFIER="$(/usr/libexec/PlistBuddy -c 'Print CFBundleIdentifier' "$SOURCE/Contents/Info.plist")"
[[ "$IDENTIFIER" == "com.xsddpx.tbox" ]] || { echo "应用标识发生变化，停止安装。" >&2; exit 1; }
if [[ -d "$DESTINATION" ]]; then
  EXISTING_ID="$(/usr/libexec/PlistBuddy -c 'Print CFBundleIdentifier' "$DESTINATION/Contents/Info.plist")"
  [[ "$EXISTING_ID" == "$IDENTIFIER" ]] || { echo "目标路径属于其他应用，停止安装。" >&2; exit 1; }
  REQUIREMENT="$(codesign -d -r- "$DESTINATION" 2>&1 | sed -n 's/^#\{0,1\} *designated => //p')"
  # A leading '=' marks literal requirement source; without it codesign treats
  # the expression as a filename and rejects even an identical signing identity.
  if [[ -z "$REQUIREMENT" ]] || ! codesign --verify --strict -R "=$REQUIREMENT" "$SOURCE"; then
    [[ "$ALLOW_MIGRATION" == "--migrate-signature" ]] || {
      echo "新包不满足已安装应用的签名身份，会影响权限。停止覆盖安装；首次迁移需显式指定 --migrate-signature。" >&2; exit 1;
    }
  fi
  "$DESTINATION/Contents/MacOS/Tbox.Desktop" --quit
fi
mkdir -p "$HOME/Applications" "$ROOT/TEMP/install-backups"
STAGE="$(mktemp -d "$HOME/Applications/.tbox-install.XXXXXX")"
trap 'rm -rf "$STAGE"' EXIT
ditto "$SOURCE" "$STAGE/Tbox.app"
codesign --verify --deep --strict "$STAGE/Tbox.app"
if [[ -d "$DESTINATION" ]]; then
  BACKUP="$ROOT/TEMP/install-backups/Tbox-$(date +%Y%m%d-%H%M%S)-$$.app"
  mv "$DESTINATION" "$BACKUP"
fi
if ! mv "$STAGE/Tbox.app" "$DESTINATION"; then
  if [[ -n "${BACKUP:-}" ]]; then mv "$BACKUP" "$DESTINATION"; fi
  exit 1
fi
codesign --verify --deep --strict "$DESTINATION"
echo "$DESTINATION"
