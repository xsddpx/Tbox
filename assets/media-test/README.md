# 合成媒体测试夹具

`fixture.mp4` 是 320×240、10 FPS、2 秒的 H.264 合成测试图，无声音，不包含任何用户屏幕内容。

由本项目固定版本的 FFmpeg 8.1.2 在 Apple Silicon 生成：

```sh
ffmpeg -hide_banner -loglevel error -nostdin -y -f lavfi -i testsrc2=size=320x240:rate=10 -t 2 -c:v h264_videotoolbox -pix_fmt yuv420p fixture.mp4
```

它只验证本地回放和 MP4/GIF/WebP 转换；不验证系统屏幕录制授权或真实音频采集。
