#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"
RID="${1:-osx-arm64}"
if [[ "$RID" != "osx-arm64" || "$(uname -m)" != "arm64" ]]; then
  echo "发布必须在对应架构本机完成运行验证；当前脚本仅支持 Apple Silicon。" >&2; exit 1
fi
VERSION="$(/usr/libexec/PlistBuddy -c 'Print CFBundleShortVersionString' src/Tbox.Desktop/Platform/macos/Info.plist)"
PACKAGE="$ROOT/artifacts/Tbox_${VERSION}_${RID}.zip"
if [[ -e "$PACKAGE" ]] || grep -Eq "[|] ${VERSION//./\.} [|] macOS $RID [|]" docs/release-history.md; then
  echo "此版本已经发布，禁止覆盖。请同步提升项目和 Info.plist 版本。" >&2; exit 1
fi
DOTNET="${TBOX_DOTNET:-$(command -v dotnet || true)}"
if [[ -z "$DOTNET" ]]; then DOTNET="$ROOT/TEMP/dotnet/dotnet"; fi
bash tests/macos/test-folder-panel-policy.sh
"$DOTNET" test tests/Tbox.Core.Tests/Tbox.Core.Tests.csproj -c Release
"$DOTNET" test tests/Tbox.Desktop.Tests/Tbox.Desktop.Tests.csproj -c Release
bash scripts/build-macos.sh "$RID"
APP="$ROOT/TEMP/build/$RID/Tbox.app"
ACTUAL_VERSION="$("$APP/Contents/MacOS/Tbox.Desktop" --version)"
[[ "$ACTUAL_VERSION" == "$VERSION.0" ]] || { echo "Release 二进制版本过期" >&2; exit 1; }
SMOKE="$ROOT/TEMP/package-smoke/$VERSION"
"$APP/Contents/MacOS/Tbox.Desktop" --smoke-test "$SMOKE"
python3 - "$SMOKE/result.json" <<'PY'
import json,sys
with open(sys.argv[1]) as f: result=json.load(f)
assert result.get('success') is True, result
PY
"$DOTNET" run --project tests/Tbox.MediaSmoke/Tbox.MediaSmoke.csproj -c Release -- \
  assets/media-test/fixture.mp4 "$SMOKE/media" \
  "$APP/Contents/MacOS/ThirdParty/FFmpeg/bin/ffmpeg" "$APP/Contents/MacOS/ThirdParty/VLC"
mkdir -p artifacts
TEMP_PACKAGE="$(mktemp "$ROOT/artifacts/.tbox-${VERSION}-XXXXXX.zip")"
trap 'rm -f "$TEMP_PACKAGE"' EXIT
ditto -c -k --sequesterRsrc --keepParent "$APP" "$TEMP_PACKAGE"
# Atomic no-overwrite publication; a concurrent invocation cannot overwrite this version.
ln "$TEMP_PACKAGE" "$PACKAGE"
HASH="$(shasum -a 256 "$PACKAGE" | cut -d ' ' -f 1)"
STAMP="$(TZ=Asia/Shanghai date '+%Y-%m-%d %H:%M:%S %z')"
if ! grep -q '^## macOS 开发包' docs/release-history.md; then
  cat >> docs/release-history.md <<'EOF'

## macOS 开发包

持久本地开发证书签名，未经过 Apple Developer ID 签名或公证。不可视为公开分发版本。

| 版本 | 平台 | 打包时间（Asia/Shanghai） | SHA-256 |
| --- | --- | --- | --- |
EOF
fi
python3 - "$VERSION" "$RID" "$STAMP" "$HASH" <<'PY'
import pathlib, sys
path = pathlib.Path('docs/release-history.md')
text = path.read_text()
start = text.index('\n## macOS 开发包\n')
end = text.find('\n## ', start + 1)
if end < 0: end = len(text)
version, rid, stamp, digest = sys.argv[1:]
row = f'| {version} | macOS {rid} | {stamp} | `{digest}` |\n'
path.write_text(text[:end].rstrip() + '\n' + row + text[end:])
PY
echo "$PACKAGE"
