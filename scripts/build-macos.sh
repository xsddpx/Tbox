#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"
RID="${1:-osx-arm64}"
if [[ "$RID" != osx-arm64 || "$(uname -m)" != arm64 ]]; then
  echo "仅支持在 Apple Silicon 上构建 osx-arm64。" >&2; exit 1
fi
SIGN_IDENTITY="${TBOX_SIGN_IDENTITY:-Tbox Local Development}"
if [[ "$SIGN_IDENTITY" == "-" ]]; then echo "禁止使用临时签名，以免升级丢失权限。" >&2; exit 1; fi
if ! security find-certificate -c "$SIGN_IDENTITY" >/dev/null 2>&1; then
  echo "缺少持久签名证书。首次运行 bash scripts/setup-macos-signing.sh；不要每轮重新生成证书。" >&2; exit 1
fi
bash scripts/prepare-macos-media.sh
DOTNET="${TBOX_DOTNET:-$(command -v dotnet || true)}"
if [[ -z "$DOTNET" && -x "$ROOT/TEMP/dotnet/dotnet" ]]; then DOTNET="$ROOT/TEMP/dotnet/dotnet"; fi
if [[ ! -x "$DOTNET" ]]; then echo "请安装 global.json 指定的 .NET SDK，或设置 TBOX_DOTNET。" >&2; exit 1; fi
BUILD="$ROOT/TEMP/build/$RID"
APP="$BUILD/Tbox.app"
NATIVE=src/Tbox.Desktop/Platform/macos
MINIMUM_MACOS="$(/usr/libexec/PlistBuddy -c 'Print LSMinimumSystemVersion' "$NATIVE/Info.plist")"
SWIFT_TARGET="arm64-apple-macosx$MINIMUM_MACOS"
mkdir -p "$BUILD"
python3 - <<'PY'
import plistlib, xml.etree.ElementTree as ET
p=ET.parse('src/Tbox.Desktop/Tbox.Desktop.csproj').getroot()
v={k:p.find('.//'+k).text for k in ['Version','AssemblyVersion','FileVersion']}
with open('src/Tbox.Desktop/Platform/macos/Info.plist','rb') as f: info=plistlib.load(f)
assert v['AssemblyVersion']==v['FileVersion']==v['Version']+'.0'==info['CFBundleVersion'], '程序集/文件/Bundle 版本不一致'
assert v['Version']==info['CFBundleShortVersionString'], '应用版本不一致'
PY
rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
# Keep the macOS executable directory free of portable PDB/managed PE files;
# macOS treats files in this directory as code when sealing the bundle.
"$DOTNET" publish src/Tbox.Desktop/Tbox.Desktop.csproj -c Release -r "$RID" --self-contained true \
  -p:PublishSingleFile=true -p:DebugType=None -p:DebugSymbols=false -o "$APP/Contents/MacOS"
[[ "$(/usr/bin/lipo -archs "$APP/Contents/MacOS/Tbox.Desktop")" == arm64 ]] || {
  echo "Mac 安装包必须使用 arm64 主程序。" >&2; exit 1
}
rm -rf "$APP/Contents/MacOS/ThirdParty"
LID_SERVICE=com.xsddpx.tbox.lid-sleep
# Both ends authenticate the actual code signature, including the stable local signing certificate.
CERT_SHA1="$(security find-certificate -c "$SIGN_IDENTITY" -p | openssl x509 -noout -fingerprint -sha1 | cut -d= -f2 | tr -d ':')"
[[ "$CERT_SHA1" =~ ^[a-fA-F0-9]{40}$ ]] || { echo "无法读取合盖唤醒组件的签名证书。"; exit 1; }
REQUIREMENTS="$BUILD/LidSleepRequirements.swift"
cat > "$REQUIREMENTS" <<EOF
let lidSleepAppRequirement = #"identifier "com.xsddpx.tbox" and certificate leaf = H"$CERT_SHA1""#
let lidSleepDaemonRequirement = #"identifier "$LID_SERVICE" and certificate leaf = H"$CERT_SHA1""#
EOF
swiftc -swift-version 5 -O -emit-library -target "$SWIFT_TARGET" \
  "$NATIVE"/*.swift "$NATIVE/lid/LidSleepClient.swift" "$NATIVE/lid/LidSleepService.swift" "$REQUIREMENTS" -o "$APP/Contents/MacOS/libTboxNative.dylib"
LID_DAEMON="$APP/Contents/MacOS/TboxLidSleep"
swiftc -swift-version 5 -O -target "$SWIFT_TARGET" \
  "$NATIVE/lid/main.swift" "$NATIVE/lid/LidSleepService.swift" "$REQUIREMENTS" -o "$LID_DAEMON"
codesign --force --sign "$SIGN_IDENTITY" --identifier "$LID_SERVICE" --options runtime --timestamp=none "$LID_DAEMON"
mkdir -p "$APP/Contents/Library/LaunchDaemons"
cp "$NATIVE/lid/$LID_SERVICE.plist" "$APP/Contents/Library/LaunchDaemons/"

cp "$NATIVE/Info.plist" "$APP/Contents/Info.plist"
mkdir -p "$APP/Contents/Resources/Licenses"
cp src/Tbox.Desktop/ThirdParty/*.txt "$APP/Contents/Resources/Licenses/"
cp src/Tbox.Desktop/ThirdParty/THIRD-PARTY-NOTICES.md "$APP/Contents/Resources/Licenses/"
cp TEMP/media-runtime/Licenses/*.txt "$APP/Contents/Resources/Licenses/"
mkdir -p "$APP/Contents/MacOS/ThirdParty/FFmpeg/bin"
cp TEMP/media-runtime/FFmpeg/bin/ffmpeg "$APP/Contents/MacOS/ThirdParty/FFmpeg/bin/"
ditto TEMP/media-runtime/VLC "$APP/Contents/MacOS/ThirdParty/VLC"
if [[ -n "${TBOX_REMOTE_MACOS_VIEWER_ZIP:-}" ]]; then
  : "${TBOX_REMOTE_MACOS_VIEWER_SHA256:?Missing the verified remote viewer digest}"
  : "${TBOX_REMOTE_RENDEZVOUS:?Missing the rendezvous server address}"
  : "${TBOX_REMOTE_RENDEZVOUS_KEY:?Missing the rendezvous server key}"
  bash scripts/stage-remote-viewer-macos.sh "$TBOX_REMOTE_MACOS_VIEWER_ZIP" "$TBOX_REMOTE_MACOS_VIEWER_SHA256" \
    "$APP" "$MINIMUM_MACOS" "$SIGN_IDENTITY" "$CERT_SHA1"
fi
python3 scripts/verify-macos-deployment-target.py --deployment-target "$MINIMUM_MACOS" "$APP"
ICONSET="$BUILD/Tbox.iconset"
mkdir -p "$ICONSET"
for SIZE in 16 32 128 256 512; do
  sips -z "$SIZE" "$SIZE" src/Tbox.Desktop/Assets/AppIcon.png --out "$ICONSET/icon_${SIZE}x${SIZE}.png" >/dev/null
  DOUBLE=$((SIZE * 2))
  sips -z "$DOUBLE" "$DOUBLE" src/Tbox.Desktop/Assets/AppIcon.png --out "$ICONSET/icon_${SIZE}x${SIZE}@2x.png" >/dev/null
done
iconutil -c icns "$ICONSET" -o "$APP/Contents/Resources/Tbox.icns"
while IFS= read -r -d '' LIBRARY; do
  codesign --force --sign "$SIGN_IDENTITY" --timestamp=none "$LIBRARY"
done < <(find "$APP/Contents/MacOS" -type f -name '*.dylib' -print0)
codesign --force --sign "$SIGN_IDENTITY" --timestamp=none "$APP/Contents/MacOS/ThirdParty/FFmpeg/bin/ffmpeg"
codesign --force --sign "$SIGN_IDENTITY" --timestamp=none "$APP"
codesign --verify --strict -R "=identifier \"com.xsddpx.tbox\" and certificate leaf = H\"$CERT_SHA1\"" "$APP"
codesign --verify --strict -R "=identifier \"$LID_SERVICE\" and certificate leaf = H\"$CERT_SHA1\"" "$LID_DAEMON"
bash scripts/check-macos-identity.sh "$APP" > "$BUILD/designated-requirement.txt"
echo "$APP"
