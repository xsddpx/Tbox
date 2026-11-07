#!/bin/bash
set -euo pipefail
DESTINATION="$1"
STAGE="$2"
PARENT_PID="$3"
VERSION="$4"
OLD_VERSION="$5"
exec >> "$STAGE/install.log" 2>&1
BACKUP="$STAGE/Previous.app"
REQUIREMENT="$(cat "$STAGE/requirement.txt")"
for ((attempt=0; attempt<60; attempt++)); do
  if ! kill -0 "$PARENT_PID" 2>/dev/null; then break; fi
  sleep 1
done
if kill -0 "$PARENT_PID" 2>/dev/null; then echo 'Tbox 未退出，取消安装。'; exit 1; fi
/usr/bin/codesign --verify --deep --strict -R "=$REQUIREMENT" "$DESTINATION"
[[ "$(/usr/libexec/PlistBuddy -c 'Print CFBundleVersion' "$DESTINATION/Contents/Info.plist")" == "$OLD_VERSION" ]]
/usr/bin/codesign --verify --deep --strict -R "=$REQUIREMENT" "$STAGE/Tbox.app"
[[ "$(/usr/libexec/PlistBuddy -c 'Print CFBundleIdentifier' "$STAGE/Tbox.app/Contents/Info.plist")" == com.xsddpx.tbox ]]
[[ "$(/usr/libexec/PlistBuddy -c 'Print CFBundleVersion' "$STAGE/Tbox.app/Contents/Info.plist")" == "$VERSION" ]]
mv "$DESTINATION" "$BACKUP"
if ! mv "$STAGE/Tbox.app" "$DESTINATION"; then
  mv "$BACKUP" "$DESTINATION"
  /usr/bin/open "$DESTINATION"
  exit 1
fi
if ! /usr/bin/codesign --verify --deep --strict "$DESTINATION" || ! /usr/bin/open "$DESTINATION"; then
  mv "$DESTINATION" "$STAGE/Failed.app"
  mv "$BACKUP" "$DESTINATION"
  /usr/bin/open "$DESTINATION"
  exit 1
fi
echo '升级完成；Previous.app 保留为恢复副本。'
