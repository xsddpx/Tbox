using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using LibVLCSharp.Shared;
using Tbox.Core;
using Tbox.Desktop.Models;

if (args.Length < 3) throw new ArgumentException("参数：测试 MP4、输出目录、FFmpeg 路径、可选 VLC 目录。");
var source = Path.GetFullPath(args[0]); var output = Path.GetFullPath(args[1]);
Directory.CreateDirectory(output);
Environment.SetEnvironmentVariable("TBOX_FFMPEG", Path.GetFullPath(args[2]));
if (args.Length > 3) Environment.SetEnvironmentVariable("TBOX_VLC_DIRECTORY", Path.GetFullPath(args[3]));
using var engine = MediaRuntime.Create();
using var media = new Media(engine, source, FromType.FromPath);
using var player = new MediaPlayer(engine);
var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
player.EndReached += (_, _) => ended.TrySetResult();
player.EncounteredError += (_, _) => ended.TrySetException(new InvalidOperationException("LibVLC 解码失败。"));
var frames = 0; var buffer = Marshal.AllocHGlobal(320 * 240 * 4);
try
{
    player.SetVideoFormat("RV32", 320, 240, 320 * 4);
    player.SetVideoCallbacks((_, planes) => { Marshal.WriteIntPtr(planes, buffer); return IntPtr.Zero; }, null,
        (_, _) => Interlocked.Increment(ref frames));
    if (!player.Play(media)) throw new InvalidOperationException("无法开始播放。");
    await ended.Task.WaitAsync(TimeSpan.FromSeconds(30));
    await Task.Run(player.Stop);
    if (frames < 2) throw new InvalidOperationException("没有解码出连续的视频帧。");
}
finally { await Task.Run(player.Stop); player.Media = null; Marshal.FreeHGlobal(buffer); }
var gif = Path.Combine(output, "export.gif"); var webp = Path.Combine(output, "export.webp");
await RecordingExporter.ExportAsync(source, gif, RecordingExportFormat.Gif, TimeSpan.FromSeconds(2), null, CancellationToken.None);
await RecordingExporter.ExportAsync(source, webp, RecordingExportFormat.WebP, TimeSpan.FromSeconds(2), null, CancellationToken.None);
var gifBytes = await File.ReadAllBytesAsync(gif);
if (!gifBytes.AsSpan(0, 6).SequenceEqual("GIF89a"u8) || BitConverter.ToUInt16(gifBytes, 6) != 320 || BitConverter.ToUInt16(gifBytes, 8) != 240)
    throw new InvalidOperationException("GIF 格式或不放大尺寸检查失败。");
var webpBytes = await File.ReadAllBytesAsync(webp);
var animatedFrames = 0;
var webpDuration = 0; var webpLoop = -1; var webpWidth = 0; var webpHeight = 0;
for (var cursor = 12; cursor + 8 <= webpBytes.Length;)
{
    if (webpBytes.AsSpan(cursor, 4).SequenceEqual("ANMF"u8)) { animatedFrames++; webpDuration += UInt24(webpBytes, cursor + 20); }
    if (webpBytes.AsSpan(cursor, 4).SequenceEqual("ANIM"u8)) webpLoop = BitConverter.ToUInt16(webpBytes, cursor + 12);
    if (webpBytes.AsSpan(cursor, 4).SequenceEqual("VP8X"u8)) { webpWidth = UInt24(webpBytes, cursor + 12) + 1; webpHeight = UInt24(webpBytes, cursor + 15) + 1; }
    var size = checked((int)BitConverter.ToUInt32(webpBytes, cursor + 4)); cursor = checked(cursor + 8 + size + size % 2);
}
if (!webpBytes.AsSpan(0, 4).SequenceEqual("RIFF"u8) || !webpBytes.AsSpan(8, 4).SequenceEqual("WEBP"u8) || animatedFrames < 2 ||
    webpLoop != 0 || webpWidth != 320 || webpHeight != 240 || Math.Abs(webpDuration - 2000) > 150)
    throw new InvalidOperationException("WebP 不是有效的循环动画。");
var gifFrames = 0; var gifDuration = 0;
var gifCursor = 13 + ((gifBytes[10] & 128) != 0 ? 3 * (1 << ((gifBytes[10] & 7) + 1)) : 0);
while (gifCursor < gifBytes.Length)
{
    var marker = gifBytes[gifCursor++];
    if (marker == 0x3b) break;
    if (marker == 0x21)
    {
        var label = gifBytes[gifCursor++];
        if (label == 0xf9) gifDuration += BitConverter.ToUInt16(gifBytes, gifCursor + 2) * 10;
    }
    else if (marker == 0x2c)
    {
        gifFrames++; var flags = gifBytes[gifCursor + 8]; gifCursor += 9;
        if ((flags & 128) != 0) gifCursor += 3 * (1 << ((flags & 7) + 1));
        gifCursor++; // LZW code size.
    }
    else throw new InvalidOperationException("GIF 数据块损坏。");
    int size; while ((size = gifBytes[gifCursor++]) != 0) gifCursor += size;
}
if (gifFrames != 24 || Math.Abs(gifDuration - 2000) > 100) throw new InvalidOperationException("GIF 帧率或时长错误。");
var copy = Path.Combine(output, "copy.mp4");
await RecordingExporter.SaveAtomicallyAsync(source, copy, CancellationToken.None);
if (!SHA256.HashData(await File.ReadAllBytesAsync(source)).SequenceEqual(SHA256.HashData(await File.ReadAllBytesAsync(copy))))
    throw new InvalidOperationException("MP4 直接复制改变了母版内容。");
var json = JsonSerializer.Serialize(new { success = true, player = engine.Version, decodedFrames = frames,
    gifBytes = gifBytes.Length, gifFrames, gifDurationMs = gifDuration, webpBytes = webpBytes.Length,
    webpFrames = animatedFrames, webpDurationMs = webpDuration, mp4CopyIdentical = true });
await File.WriteAllTextAsync(Path.Combine(output, "result.json"), json);
Console.WriteLine(json);

static int UInt24(byte[] bytes, int offset) => bytes[offset] | bytes[offset + 1] << 8 | bytes[offset + 2] << 16;
