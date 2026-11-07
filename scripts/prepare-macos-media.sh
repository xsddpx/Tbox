#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
[[ "$(uname -m)" == arm64 ]] || { echo "当前媒体依赖构建仅验证 Apple Silicon。" >&2; exit 1; }
# Match the declared app minimum regardless of the developer/runner's macOS.
export MACOSX_DEPLOYMENT_TARGET=14.0
SOURCE="$ROOT/TEMP/media-sources"
RUNTIME="$ROOT/TEMP/media-runtime"
DOWNLOAD="$ROOT/TEMP/media-downloads"
mkdir -p "$SOURCE" "$RUNTIME" "$DOWNLOAD"
PKG_CONFIG="${TBOX_PKG_CONFIG:-$(command -v pkg-config || true)}"
[[ -x "$PKG_CONFIG" ]] || { echo "请安装 pkg-config（brew install pkgconf）。" >&2; exit 1; }
download() {
  local url="$1" file="$2" hash="$3"
  if [[ -f "$file" ]] && [[ "$(shasum -a 256 "$file" | cut -d ' ' -f 1)" == "$hash" ]]; then return; fi
  curl -fL --retry 3 --connect-timeout 20 "$url" -o "$file.partial"
  [[ "$(shasum -a 256 "$file.partial" | cut -d ' ' -f 1)" == "$hash" ]] || { echo "下载校验失败：$file" >&2; exit 1; }
  mv "$file.partial" "$file"
}
download https://ffmpeg.org/releases/ffmpeg-8.1.2.tar.xz "$SOURCE/ffmpeg-8.1.2.tar.xz" 464beb5e7bf0c311e68b45ae2f04e9cc2af88851abb4082231742a74d97b524c
download https://storage.googleapis.com/downloads.webmproject.org/releases/webp/libwebp-1.6.0.tar.gz "$SOURCE/libwebp-1.6.0.tar.gz" e4ab7009bf0629fd11982d4c2aa83964cf244cffba7347ecd39019a9e38c4564
download https://download.videolan.org/pub/vlc/3.0.24/vlc-3.0.24.tar.xz "$SOURCE/vlc-3.0.24.tar.xz" e7cab503d1d7d5849b89d2cf0e1ee60d0ef6d012407791b644b9cfc0cc225fdf
download https://download.videolan.org/pub/vlc/3.0.24/macosx/vlc-3.0.24-arm64.dmg "$DOWNLOAD/vlc-3.0.24-arm64.dmg" 64a89d93cdd30b0e97131743e246373db82d6826ea882d265d46d74b136da2b7
for archive in "$SOURCE/ffmpeg-8.1.2.tar.xz" "$SOURCE/libwebp-1.6.0.tar.gz" "$SOURCE/vlc-3.0.24.tar.xz"; do tar -xf "$archive" -C "$SOURCE"; done
STAMP="$(shasum -a 256 "$0" | cut -d ' ' -f 1)"
if [[ ! -f "$RUNTIME/build-id" ]] || [[ "$(cat "$RUNTIME/build-id")" != "$STAMP" ]]; then
  (
    cd "$SOURCE/libwebp-1.6.0"
    ./configure --prefix="$RUNTIME/webp" --disable-shared --enable-static --enable-libwebpmux --disable-sdl --disable-png --disable-jpeg --disable-tiff --disable-gif
    make clean
    make -j"$(sysctl -n hw.ncpu)"
    make install
  ) > "$ROOT/TEMP/webp-build.log" 2>&1
  (
    cd "$SOURCE/ffmpeg-8.1.2"
    PKG_CONFIG_PATH="$RUNTIME/webp/lib/pkgconfig" ./configure --prefix="$RUNTIME/FFmpeg" --pkg-config="$PKG_CONFIG" --pkg-config-flags=--static \
      --disable-autodetect --disable-everything --disable-doc --disable-debug --disable-ffplay --disable-network \
      --enable-libwebp --enable-protocol=file,pipe --enable-demuxer=mov,lavfi --enable-muxer=gif,webp,mp4 \
      --enable-decoder=h264,aac,rawvideo,wrapped_avframe --enable-parser=h264,aac \
      --enable-encoder=gif,libwebp,libwebp_anim,h264_videotoolbox \
      --enable-filter=fps,scale,split,palettegen,paletteuse,testsrc2,format,null,anull --enable-indev=lavfi --enable-videotoolbox
    make clean
    make -j"$(sysctl -n hw.ncpu)"
    make install
  ) > "$ROOT/TEMP/ffmpeg-build.log" 2>&1
  MOUNT="$(mktemp -d "$DOWNLOAD/.vlc-mount-XXXXXX")"
  trap 'hdiutil detach "$MOUNT" >/dev/null 2>&1 || true; rmdir "$MOUNT" 2>/dev/null || true' EXIT
  hdiutil attach -readonly -nobrowse -mountpoint "$MOUNT" "$DOWNLOAD/vlc-3.0.24-arm64.dmg" >/dev/null
  # Only the local MP4 decoder, audio/video outputs and their conversion modules are used.
  # Avoid scanning the full VLC application/interface/network plugin collection at startup.
  rm -rf "$RUNTIME/VLC"
  mkdir -p "$RUNTIME/VLC/plugins"
  ditto "$MOUNT/VLC.app/Contents/MacOS/lib" "$RUNTIME/VLC/lib"
  for plugin in filesystem mp4 avcodec vmem vout_macosx caopengllayer auhal audio_format float_mixer integer_mixer \
    simple_channel_mixer trivial_channel_mixer ugly_resampler scaletempo swscale chain cvpx glconv_cvpx \
    i420_rgb i420_yuy2 i420_nv12 yuy2_i420 i422_i420 yuvp adummy vdummy console_logger file_logger amem equalizer subsdec deinterlace \
    marq logo adjust subtitle; do
    cp "$MOUNT/VLC.app/Contents/MacOS/plugins/lib${plugin}_plugin.dylib" "$RUNTIME/VLC/plugins/"
  done
  hdiutil detach "$MOUNT" >/dev/null
  rmdir "$MOUNT"; trap - EXIT
  printf '%s\n' "$STAMP" > "$RUNTIME/build-id"
fi
python3 "$ROOT/scripts/verify-macos-deployment-target.py" "$RUNTIME/FFmpeg/bin/ffmpeg" "$RUNTIME/VLC"
mkdir -p "$RUNTIME/Licenses"
cp "$SOURCE/ffmpeg-8.1.2/COPYING.LGPLv2.1" "$RUNTIME/Licenses/FFmpeg-LGPL-2.1.txt"
cp "$SOURCE/libwebp-1.6.0/COPYING" "$RUNTIME/Licenses/libwebp-LICENSE.txt"
cp "$SOURCE/libwebp-1.6.0/PATENTS" "$RUNTIME/Licenses/libwebp-PATENTS.txt"
cp "$SOURCE/vlc-3.0.24/COPYING" "$RUNTIME/Licenses/VLC-GPL-2.txt"
cp "$SOURCE/vlc-3.0.24/COPYING.LIB" "$RUNTIME/Licenses/LibVLC-LGPL-2.1.txt"
"$RUNTIME/FFmpeg/bin/ffmpeg" -version > "$RUNTIME/Licenses/FFmpeg-build.txt"
"$RUNTIME/FFmpeg/bin/ffmpeg" -hide_banner -encoders 2>/dev/null | grep -q 'libwebp_anim' || { echo "缺少动画 WebP 编码器。" >&2; exit 1; }
find "$RUNTIME/VLC/plugins" -type f -name '*.dylib' -exec basename {} \; | sort > "$RUNTIME/Licenses/VLC-plugins.txt"
echo "媒体依赖已准备：$RUNTIME"
