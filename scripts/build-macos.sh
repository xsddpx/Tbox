#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"
RID="${1:-osx-arm64}"
SIGN_IDENTITY="${TBOX_SIGN_IDENTITY:-Tbox Local Development}"
if [[ "$SIGN_IDENTITY" == "-" ]]; then echo "禁止使用临时签名，以免升级丢失权限。" >&2; exit 1; fi
if ! security find-certificate -c "$SIGN_IDENTITY" >/dev/null 2>&1; then
  echo "缺少持久签名证书。首次运行 bash scripts/setup-macos-signing.sh；不要每轮重新生成证书。" >&2; exit 1
fi
case "$RID" in
  osx-arm64) ARCH=arm64 ;;
  osx-x64) ARCH=x86_64 ;;
  *) echo "仅支持 osx-arm64 / osx-x64" >&2; exit 1 ;;
esac
if [[ "$RID" != osx-arm64 || "$(uname -m)" != arm64 ]]; then
  echo "录屏媒体依赖当前仅验证 Apple Silicon；其他架构必须先补齐对应媒体构建。" >&2; exit 1
fi
bash scripts/prepare-macos-media.sh
DOTNET="${TBOX_DOTNET:-$(command -v dotnet || true)}"
if [[ -z "$DOTNET" && -x "$ROOT/TEMP/dotnet/dotnet" ]]; then DOTNET="$ROOT/TEMP/dotnet/dotnet"; fi
if [[ ! -x "$DOTNET" ]]; then echo "请安装 global.json 指定的 .NET SDK，或设置 TBOX_DOTNET。" >&2; exit 1; fi
BUILD="$ROOT/TEMP/build/$RID"
APP="$BUILD/Tbox.app"
mkdir -p "$BUILD" "$APP/Contents/MacOS" "$APP/Contents/Resources"
python3 - <<'PY'
import plistlib, xml.etree.ElementTree as ET
p=ET.parse('src/Tbox.Desktop/Tbox.Desktop.csproj').getroot()
v={k:p.find('.//'+k).text for k in ['Version','AssemblyVersion','FileVersion']}
with open('src/Tbox.Desktop/Platform/macos/Info.plist','rb') as f: info=plistlib.load(f)
assert v['AssemblyVersion']==v['FileVersion']==v['Version']+'.0'==info['CFBundleVersion'], '程序集/文件/Bundle 版本不一致'
assert v['Version']==info['CFBundleShortVersionString'], '应用版本不一致'
PY
"$DOTNET" publish src/Tbox.Desktop/Tbox.Desktop.csproj -c Release -r "$RID" --self-contained true -o "$APP/Contents/MacOS"
swiftc -swift-version 5 -O -emit-library -target "$ARCH-apple-macosx14.0" \
  src/Tbox.Desktop/Platform/macos/*.swift -o "$APP/Contents/MacOS/libTboxNative.dylib"
cp src/Tbox.Desktop/Platform/macos/Info.plist "$APP/Contents/Info.plist"
mkdir -p "$APP/Contents/Resources/Licenses"
cp src/Tbox.Desktop/ThirdParty/*.txt "$APP/Contents/Resources/Licenses/"
cp src/Tbox.Desktop/ThirdParty/THIRD-PARTY-NOTICES.md "$APP/Contents/Resources/Licenses/"
cp TEMP/media-runtime/Licenses/*.txt "$APP/Contents/Resources/Licenses/"
mkdir -p "$APP/Contents/MacOS/ThirdParty/FFmpeg/bin"
cp TEMP/media-runtime/FFmpeg/bin/ffmpeg "$APP/Contents/MacOS/ThirdParty/FFmpeg/bin/"
ditto TEMP/media-runtime/VLC "$APP/Contents/MacOS/ThirdParty/VLC"
ICONSET="$BUILD/Tbox.iconset"
mkdir -p "$ICONSET"
for SIZE in 16 32 128 256 512; do
  sips -z "$SIZE" "$SIZE" src/Tbox.App/Assets/AppIcon.png --out "$ICONSET/icon_${SIZE}x${SIZE}.png" >/dev/null
  DOUBLE=$((SIZE * 2))
  sips -z "$DOUBLE" "$DOUBLE" src/Tbox.App/Assets/AppIcon.png --out "$ICONSET/icon_${SIZE}x${SIZE}@2x.png" >/dev/null
done
iconutil -c icns "$ICONSET" -o "$APP/Contents/Resources/Tbox.icns"
while IFS= read -r -d '' LIBRARY; do
  codesign --force --sign "$SIGN_IDENTITY" --timestamp=none "$LIBRARY"
done < <(find "$APP/Contents/MacOS" -type f -name '*.dylib' -print0)
codesign --force --sign "$SIGN_IDENTITY" --timestamp=none "$APP/Contents/MacOS/ThirdParty/FFmpeg/bin/ffmpeg"
codesign --force --sign "$SIGN_IDENTITY" --timestamp=none "$APP"
bash scripts/check-macos-identity.sh "$APP" > "$BUILD/designated-requirement.txt"
echo "$APP"
