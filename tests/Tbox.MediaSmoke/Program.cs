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
var fixtureSize = new VideoSize(320, 240); var fixtureLength = TimeSpan.FromSeconds(2);
var whole = new RecordingTimeRange(TimeSpan.Zero, fixtureLength);
await RecordingExporter.ExportAsync(source, gif, new(RecordingExportFormat.Gif, 12, fixtureSize, whole), fixtureSize, fixtureLength, null, CancellationToken.None);
await RecordingExporter.ExportAsync(source, webp, new(RecordingExportFormat.WebP, 24, fixtureSize, whole), fixtureSize, fixtureLength, null, CancellationToken.None);
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
var (gifFrames, gifDuration) = GifFrames(gifBytes);
if (gifFrames != 24 || Math.Abs(gifDuration - 2000) > 100) throw new InvalidOperationException("GIF 帧率或时长错误。");
var copy = Path.Combine(output, "copy.mp4");
await RecordingExporter.SaveAtomicallyAsync(source, copy, CancellationToken.None);
if (!SHA256.HashData(await File.ReadAllBytesAsync(source)).SequenceEqual(SHA256.HashData(await File.ReadAllBytesAsync(copy))))
    throw new InvalidOperationException("MP4 直接复制改变了母版内容。");
// An unchanged MP4 is the master itself; trimming, rate or size changes re-encode with the platform's hardware encoder.
var masterOptions = new RecordingExportOptions(RecordingExportFormat.Mp4, 30, fixtureSize, whole);
if (await RecordingExporter.ExportAsync(source, Path.Combine(output, "unused.mp4"), masterOptions, fixtureSize, fixtureLength, null, CancellationToken.None) != source)
    throw new InvalidOperationException("未修改的 MP4 没有直接使用母版。");
var trimmed = Path.Combine(output, "trimmed.mp4");
var trim = new RecordingTimeRange(TimeSpan.FromSeconds(0.5), TimeSpan.FromSeconds(1.5));
await RecordingExporter.ExportAsync(source, trimmed, new(RecordingExportFormat.Mp4, 10, new(160, 120), trim), fixtureSize, fixtureLength, null, CancellationToken.None);
var mp4 = await Probe(trimmed);
if (mp4.Width != 160 || mp4.Height != 120 || mp4.Frames != 10) throw new InvalidOperationException($"重编码 MP4 尺寸、帧数或截取错误：{mp4}。");
var trimmedGif = Path.Combine(output, "trimmed.gif");
await RecordingExporter.ExportAsync(source, trimmedGif, new(RecordingExportFormat.Gif, 5, new(160, 120), trim), fixtureSize, fixtureLength, null, CancellationToken.None);
var trimmedGifBytes = await File.ReadAllBytesAsync(trimmedGif);
if (BitConverter.ToUInt16(trimmedGifBytes, 6) != 160 || BitConverter.ToUInt16(trimmedGifBytes, 8) != 120 || GifFrames(trimmedGifBytes) != (5, 1000))
    throw new InvalidOperationException("截取 GIF 的尺寸或帧数错误。");
var sheet = Path.Combine(output, "sheet.jpg");
var cell = RecordingSizePolicy.FitLongEdge(fixtureSize, 200, false);
await RecordingExporter.ExportAsync(source, sheet, new(RecordingExportFormat.ContactSheet, 0, cell, whole), fixtureSize, fixtureLength, null, CancellationToken.None);
using (var image = SkiaSharp.SKBitmap.Decode(sheet) ?? throw new InvalidOperationException("六宫格不是可解码的 JPG。"))
{
    var expected = ContactSheetPolicy.SheetSize(cell);
    if (image.Width != expected.Width || image.Height != expected.Height || expected != new VideoSize(648, 2 * (150 + 28) + 36))
        throw new InvalidOperationException($"六宫格尺寸错误：{image.Width} × {image.Height}。");
    // Margins stay black (within JPEG noise); each frame cell carries the fixture's picture.
    static int Sum(SkiaSharp.SKColor c) => c.Red + c.Green + c.Blue;
    var label = Enumerable.Range(0, ContactSheetPolicy.LabelStrip).Any(dy => Enumerable.Range(0, 30).Any(dx => Sum(image.GetPixel(12 + dx, 12 + cell.Height + dy)) > 600));
    if (Sum(image.GetPixel(4, 4)) > 40 || Sum(image.GetPixel(12 + cell.Width / 2, 12 + cell.Height / 2)) < 120 || !label)
        throw new InvalidOperationException("六宫格版式或画面错误。");
}
var json = JsonSerializer.Serialize(new { success = true, player = engine.Version, decodedFrames = frames,
    gifBytes = gifBytes.Length, gifFrames, gifDurationMs = gifDuration, webpBytes = webpBytes.Length,
    webpFrames = animatedFrames, webpDurationMs = webpDuration, mp4CopyIdentical = true, trimmedMp4 = new { mp4.Width, mp4.Height, mp4.Frames },
    sheetBytes = new FileInfo(sheet).Length });
await File.WriteAllTextAsync(Path.Combine(output, "result.json"), json);
Console.WriteLine(json);

static (int Frames, int DurationMs) GifFrames(byte[] gif)
{
    var frames = 0; var duration = 0;
    var cursor = 13 + ((gif[10] & 128) != 0 ? 3 * (1 << ((gif[10] & 7) + 1)) : 0);
    while (cursor < gif.Length)
    {
        var marker = gif[cursor++];
        if (marker == 0x3b) break;
        if (marker == 0x21)
        {
            var label = gif[cursor++];
            if (label == 0xf9) duration += BitConverter.ToUInt16(gif, cursor + 2) * 10;
        }
        else if (marker == 0x2c)
        {
            frames++; var flags = gif[cursor + 8]; cursor += 9;
            if ((flags & 128) != 0) cursor += 3 * (1 << ((flags & 7) + 1));
            cursor++; // LZW code size.
        }
        else throw new InvalidOperationException("GIF 数据块损坏。");
        int size; while ((size = gif[cursor++]) != 0) cursor += size;
    }
    return (frames, duration);
}

static int UInt24(byte[] bytes, int offset) => bytes[offset] | bytes[offset + 1] << 8 | bytes[offset + 2] << 16;

// Decodes a file with the bundled exporter itself (it has no null muxer, so frames go to a raw pipe) and counts its video frames.
static async Task<(int Width, int Height, int Frames)> Probe(string path)
{
    var info = new System.Diagnostics.ProcessStartInfo(RecordingExporter.FfmpegPath) { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
    foreach (var arg in new[] { "-hide_banner", "-nostdin", "-i", path, "-map", "0:v:0", "-f", "rawvideo", "-pix_fmt", "bgra", "pipe:1" }) info.ArgumentList.Add(arg);
    using var process = System.Diagnostics.Process.Start(info)!;
    var log = process.StandardError.ReadToEndAsync();
    var bytes = 0L; var chunk = new byte[1 << 16]; int read;
    while ((read = await process.StandardOutput.BaseStream.ReadAsync(chunk)) > 0) bytes += read;
    await process.WaitForExitAsync();
    var size = System.Text.RegularExpressions.Regex.Match(await log, @"Video: .*?, (\d+)x(\d+)");
    if (process.ExitCode != 0 || !size.Success) throw new InvalidOperationException("无法解码导出结果：" + await log);
    int width = int.Parse(size.Groups[1].Value), height = int.Parse(size.Groups[2].Value);
    return (width, height, (int)(bytes / (width * height * 4L)));
}
