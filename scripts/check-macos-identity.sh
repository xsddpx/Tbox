#!/usr/bin/env bash
set -euo pipefail
APP="${1:?用法: check-macos-identity.sh 应用路径}"
IDENTIFIER="$(/usr/libexec/PlistBuddy -c 'Print CFBundleIdentifier' "$APP/Contents/Info.plist")"
[[ "$IDENTIFIER" == "com.xsddpx.tbox" ]] || { echo "应用标识发生变化，拒绝交付。" >&2; exit 1; }
codesign --verify --deep --strict "$APP"
# codesign writes the designated requirement to stdout, and the executable path to stderr.
REQUIREMENT="$(codesign -d -r- "$APP" 2>/dev/null)"
if [[ "$REQUIREMENT" != *'identifier "com.xsddpx.tbox"'* || "$REQUIREMENT" != *'certificate '* || "$REQUIREMENT" == *'cdhash '* ]]; then
  echo "缺少持久证书签名身份，拒绝交付；不能使用临时代码哈希。" >&2; exit 1
fi
printf '%s\n' "$REQUIREMENT"
