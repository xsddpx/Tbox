# Third-party notices

## ScreenRecorderLib 6.6.0

- Project: https://github.com/sskodje/ScreenRecorderLib
- Package: https://www.nuget.org/packages/ScreenRecorderLib/6.6.0
- License: MIT; see `ScreenRecorderLib/LICENSE.txt`.

## FFmpeg n8.1.2-22-g94138f6973-20260715

- Project: https://ffmpeg.org/
- Windows build: `ffmpeg-n8.1-latest-win64-lgpl-shared-8.1.zip` from BtbN/FFmpeg-Builds.
- Original downloaded archive SHA-256: `F711E0478C03FD48624C59A0487E0C5D904779A3C67280A0A7B49B0002F12B43`.
- Build configuration: LGPL shared libraries, libwebp enabled, GPL and nonfree components disabled.
- License: GNU LGPL version 3; see `FFmpeg/LICENSE.txt`.
- Corresponding upstream source: https://ffmpeg.org/releases/ffmpeg-8.1.2.tar.xz

The FFmpeg executable and shared libraries are invoked as a separate process for GIF and animated WebP conversion. The local Tbox development certificate is added to the vendored Windows binaries so that they can run under the target computer's executable policy; this does not change their license.
