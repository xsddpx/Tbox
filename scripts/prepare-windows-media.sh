#!/usr/bin/env bash
# Cross-build the Windows exporter from the same FFmpeg/libwebp sources as macOS.
set -euo pipefail
SCRIPT="$(cd "$(dirname "$0")" && pwd)/$(basename "$0")"
ROOT="${TBOX_MEDIA_ROOT:-$(cd "$(dirname "$0")/.." && pwd)}"
[[ "$(uname -s)" == Linux ]] || { echo 'Build on Linux with the MinGW x64 toolchain.' >&2; exit 1; }
BUILD="$ROOT/TEMP/windows-media-build"
SOURCE="$BUILD/sources"
RUNTIME="$ROOT/TEMP/media-runtime/windows"
PREFIX="$BUILD/prefix"
mkdir -p "$SOURCE" "$RUNTIME/bin"
download() {
  local url="$1" name="$2" digest="$3"
  if [[ -f "$(dirname "$SCRIPT")/$name" ]]; then cp "$(dirname "$SCRIPT")/$name" "$SOURCE/$name"; fi
  if [[ -f "$SOURCE/$name" ]] && [[ "$(sha256sum "$SOURCE/$name" | cut -d ' ' -f1)" == "$digest" ]]; then return; fi
  curl -fL --retry 3 --connect-timeout 20 --max-time 600 "$url" -o "$SOURCE/$name.partial"
  [[ "$(sha256sum "$SOURCE/$name.partial" | cut -d ' ' -f1)" == "$digest" ]] || { echo "Source hash mismatch: $name" >&2; exit 1; }
  mv "$SOURCE/$name.partial" "$SOURCE/$name"
}
download https://ffmpeg.org/releases/ffmpeg-8.1.2.tar.xz ffmpeg-8.1.2.tar.xz 464beb5e7bf0c311e68b45ae2f04e9cc2af88851abb4082231742a74d97b524c
download https://storage.googleapis.com/downloads.webmproject.org/releases/webp/libwebp-1.6.0.tar.gz libwebp-1.6.0.tar.gz e4ab7009bf0629fd11982d4c2aa83964cf244cffba7347ecd39019a9e38c4564
tar -xf "$SOURCE/ffmpeg-8.1.2.tar.xz" -C "$BUILD"
tar -xf "$SOURCE/libwebp-1.6.0.tar.gz" -C "$BUILD"
(
  cd "$BUILD/libwebp-1.6.0"
  ./configure --host=x86_64-w64-mingw32 --prefix="$PREFIX" --disable-shared --enable-static \
    --enable-libwebpmux --disable-threading --disable-sdl --disable-png --disable-jpeg --disable-tiff --disable-gif
  make -j"$(nproc)"
  make install
) > "$BUILD/webp-build.log" 2>&1 || { tail -60 "$BUILD/webp-build.log"; exit 1; }
(
  cd "$BUILD/ffmpeg-8.1.2"
  PKG_CONFIG_LIBDIR="$PREFIX/lib/pkgconfig" ./configure --prefix="$BUILD/ffmpeg-output" \
    --enable-cross-compile --cross-prefix=x86_64-w64-mingw32- --arch=x86_64 --target-os=mingw32 \
    --pkg-config=pkg-config --pkg-config-flags=--static --extra-ldflags=-static \
    --disable-autodetect --disable-everything --disable-doc --disable-debug --disable-ffplay --disable-ffprobe --disable-network \
    --enable-static --disable-shared --enable-libwebp --enable-protocol=file,pipe \
    --enable-demuxer=mov --enable-muxer=gif,webp --enable-decoder=h264,aac \
    --enable-parser=h264,aac --enable-encoder=gif,libwebp,libwebp_anim \
    --enable-filter=fps,scale,split,palettegen,paletteuse,format,null,anull
  make -j"$(nproc)"
  make install
) > "$BUILD/ffmpeg-build.log" 2>&1 || { tail -60 "$BUILD/ffmpeg-build.log"; exit 1; }
cp "$BUILD/ffmpeg-output/bin/ffmpeg.exe" "$RUNTIME/bin/ffmpeg.exe"
cp "$BUILD/ffmpeg-8.1.2/COPYING.LGPLv2.1" "$RUNTIME/LICENSE.txt"
cp "$BUILD/libwebp-1.6.0/COPYING" "$RUNTIME/libwebp-LICENSE.txt"
cp "$BUILD/libwebp-1.6.0/PATENTS" "$RUNTIME/libwebp-PATENTS.txt"
x86_64-w64-mingw32-objdump -p "$RUNTIME/bin/ffmpeg.exe" > "$BUILD/imports.txt"
python3 - "$ROOT" "$SCRIPT" <<'PY'
import hashlib, json, pathlib, re, sys
root = pathlib.Path(sys.argv[1]); build = root / 'TEMP/windows-media-build'; runtime = root / 'TEMP/media-runtime/windows'
imports = re.findall(r'DLL Name:\s*(\S+)', (build / 'imports.txt').read_text())
allowed = {'advapi32.dll', 'bcrypt.dll', 'kernel32.dll', 'msvcrt.dll', 'ole32.dll', 'shell32.dll', 'user32.dll', 'ws2_32.dll'}
if not imports or any(name.lower() not in allowed for name in imports):
    raise SystemExit('Unexpected non-system dependency: ' + str(imports))
def digest(path): return hashlib.sha256(path.read_bytes()).hexdigest()
sources = [{'name':p.name, 'sha256':digest(p)} for p in sorted((build / 'sources').iterdir()) if not p.name.endswith('.partial')]
manifest = {'ffmpeg':'8.1.2', 'libwebp':'1.6.0', 'target':'win-x64', 'license':'LGPL-2.1-or-later',
            'binarySha256':digest(runtime / 'bin/ffmpeg.exe'), 'buildScriptSha256':digest(pathlib.Path(sys.argv[2])),
            'systemImports':imports, 'sources':sources}
(runtime / 'BUILD-INFO.json').write_text(json.dumps(manifest, indent=2) + '\n')
print(json.dumps(manifest))
PY
mkdir -p "$BUILD/corresponding-source"
cp "$SOURCE/ffmpeg-8.1.2.tar.xz" "$SOURCE/libwebp-1.6.0.tar.gz" "$BUILD/corresponding-source/"
cp "$0" "$BUILD/corresponding-source/build-windows.sh"
cp "$RUNTIME/BUILD-INFO.json" "$RUNTIME/LICENSE.txt" "$RUNTIME/libwebp-LICENSE.txt" "$RUNTIME/libwebp-PATENTS.txt" "$BUILD/corresponding-source/"
cat > "$BUILD/corresponding-source/BUILD.md" <<'EOF'
# Windows FFmpeg 对应源码

FFmpeg 8.1.2 使用 LGPL-2.1-or-later；libwebp 1.6.0 的许可及专利授权见附带文件。
源码未修改。`BUILD-INFO.json` 记录输入归档、构建脚本及所分发二进制的 SHA-256。

在 Linux x64 上安装 make、gcc-mingw-w64-x86-64、g++-mingw-w64-x86-64、nasm、pkg-config、Python 3、curl、xz-utils，
解压本归档后执行 `TBOX_MEDIA_ROOT="$PWD" bash build-windows.sh`。
脚本优先读取本目录的源码归档并验证固定哈希；输出位于 `TEMP/media-runtime/windows/`。
仅导出组件使用该独立进程，应用自身不链接 FFmpeg。没有启用 GPL、nonfree 或网络组件。
构建产物可替换安装目录内 `ThirdParty/FFmpeg/bin/ffmpeg.exe`；修改 MSIX 内容时需重新打包签名。
本归档不包含 Tbox 应用源码或任何签名私钥。
EOF
tar -cJf "$ROOT/TEMP/windows-ffmpeg-sources.tar.xz" -C "$BUILD/corresponding-source" .
echo "Windows media runtime: $RUNTIME"
