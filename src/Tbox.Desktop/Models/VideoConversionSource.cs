using System.Globalization;
using System.Buffers.Binary;
using System.Text.RegularExpressions;
using Avalonia.Platform.Storage;

using SkiaSharp;
using Tbox.Core;

namespace Tbox.Desktop.Models;

internal sealed record VideoConversionSource(string Path, string Name, VideoSize Size, TimeSpan Duration, double FramesPerSecond,
    bool HasAudio, bool IsRecording, TimeSpan[]? AnimationFrames = null, string? OriginalPath = null)
{
    public static FilePickerFileType FileType { get; } = new("视频与动图")
    {
        Patterns = ["*.mp4", "*.mov", "*.m4v", "*.webm", "*.mkv", "*.avi", "*.gif", "*.webp"],
        MimeTypes = ["video/*", "image/gif", "image/webp"],
        AppleUniformTypeIdentifiers = ["public.movie", "com.compuserve.gif", "org.webmproject.webp"]
    };

    public static async Task<VideoConversionSource> OpenAsync(string path, RecordingWorkspace workspace, CancellationToken token)
    {
        var extension = System.IO.Path.GetExtension(path).ToLowerInvariant();
        var name = System.IO.Path.GetFileName(path);
        if (extension is ".gif" or ".webp")
        {
            var (size, duration, times) = await Task.Run(() => DecodeAnimation(path, workspace.SessionDirectory, token), token);
            var media = System.IO.Path.Combine(workspace.SessionDirectory, "animation.mov");
            await RecordingExporter.AssembleAnimationAsync(System.IO.Path.Combine(workspace.SessionDirectory, "frames.txt"), media, token);
            Directory.Delete(System.IO.Path.Combine(workspace.SessionDirectory, "frames"), true);
            return new(media, name, size, duration, 0, false, false, times, path);
        }
        var demuxer = extension switch
        {
            ".mp4" or ".mov" or ".m4v" => "mov",
            ".webm" or ".mkv" => "matroska",
            ".avi" => "avi",
            _ => throw new InvalidDataException("请选择 MP4、MOV、M4V、WebM、MKV、AVI、GIF 或 WebP 文件。")
        };
        var output = await RecordingExporter.ProbeAsync(path, demuxer, token);
        var durationText = Regex.Match(output, @"Duration: (\d+:\d+:\d+\.\d+)").Groups[1].Value;
        var video = Regex.Match(output, @"Video: rawvideo[^\r\n]*?, (\d+)x(\d+)[,\s]");
        var fps = Regex.Match(output, @"Video:[^\r\n]*?[, ](\d+(?:\.\d+)?) fps").Groups[1].Value;
        if (!TimeSpan.TryParse(durationText, CultureInfo.InvariantCulture, out var durationVideo) || durationVideo <= TimeSpan.Zero || !video.Success)
            throw new InvalidDataException("无法读取视频的画面尺寸或时长。");
        _ = double.TryParse(fps, CultureInfo.InvariantCulture, out var rate);
        return new(path, name, new(int.Parse(video.Groups[1].Value), int.Parse(video.Groups[2].Value)),
            durationVideo, rate, Regex.IsMatch(output, @"Stream #0[^\r\n]*Audio:"), false);
    }

    public TimeSpan FrameAt(TimeSpan time)
    {
        if (AnimationFrames is not { Length: > 0 } frames) return time;
        var result = Array.BinarySearch(frames, time);
        return frames[result >= 0 ? result : Math.Max(0, ~result - 1)];
    }

    public IReadOnlyList<TimeSpan> SampleTimes(RecordingTimeRange range, int count)
    {
        if (IsRecording) return ContactSheetPolicy.SampleTimes(range, RecordingFrameRatePolicy.RecordingFrameRate, count);
        var start = range.Start;
        if (AnimationFrames is { } frames)
        {
            var index = Array.BinarySearch(frames, start);
            if (index < 0 && ~index < frames.Length && frames[~index] < range.End) start = frames[~index];
        }
        var frameLength = AnimationFrames is not null ? TimeSpan.FromTicks(1) : TimeSpan.FromSeconds(1 / Math.Max(1, FramesPerSecond > 0 ? FramesPerSecond : 30));
        var end = range.End - frameLength > start ? range.End - frameLength : start;
        return Enumerable.Range(0, count).Select(index => FrameAt(start + (end - start) * (index / (double)(count - 1)))).ToArray();
    }

    private static void WriteFrame(string path, SKBitmap bitmap)
    {
        // Uncompressed 32-bit TGA carries straight alpha without adding a native compression dependency.
        Span<byte> header = stackalloc byte[18];
        header.Clear();
        header[2] = 2;
        BinaryPrimitives.WriteUInt16LittleEndian(header[12..], checked((ushort)bitmap.Width));
        BinaryPrimitives.WriteUInt16LittleEndian(header[14..], checked((ushort)bitmap.Height));
        header[16] = 32;
        header[17] = 0x28;
        using var file = File.Create(path);
        file.Write(header);
        file.Write(bitmap.GetPixelSpan());
    }

    private static (VideoSize Size, TimeSpan Duration, TimeSpan[] Frames) DecodeAnimation(string path, string session, CancellationToken token)
    {
        using var codec = SKCodec.Create(path) ?? throw new InvalidDataException("无法读取动图。");
        if (codec.EncodedFormat is not (SKEncodedImageFormat.Gif or SKEncodedImageFormat.Webp))
            throw new InvalidDataException("只能导入 GIF 或 WebP 动图。");
        var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        using var bitmap = new SKBitmap(info);
        var frames = codec.FrameInfo;
        var times = new TimeSpan[Math.Max(1, codec.FrameCount)];
        var folder = System.IO.Path.Combine(session, "frames");
        Directory.CreateDirectory(folder);
        using var manifest = File.CreateText(System.IO.Path.Combine(session, "frames.txt"));
        manifest.WriteLine("ffconcat version 1.0");
        var duration = TimeSpan.Zero;
        for (var index = 0; index < times.Length; index++)
        {
            token.ThrowIfCancellationRequested();
            var prior = index > 0 && frames[index].RequiredFrame >= 0 &&
                frames[index - 1].DisposalMethod != SKCodecAnimationDisposalMethod.RestorePrevious ? index - 1 : -1;
            if (prior == -1) bitmap.Erase(SKColors.Transparent);
            if (codec.GetPixels(info, bitmap.GetPixels(), new SKCodecOptions(index, prior)) != SKCodecResult.Success)
                throw new InvalidDataException($"无法读取动图的第 {index + 1} 帧。");
            var relative = $"frames/{index}.tga";
            WriteFrame(System.IO.Path.Combine(session, relative), bitmap);
            var milliseconds = frames.Length > index && frames[index].Duration > 10 ? frames[index].Duration : 100;
            times[index] = duration;
            duration += TimeSpan.FromMilliseconds(milliseconds);
            manifest.WriteLine($"file {relative}\noption framerate 1000");
            if (index == times.Length - 1)
            {
                manifest.WriteLine(FormattableString.Invariant($"duration {(milliseconds - 1) / 1000d:0.000}"));
                manifest.WriteLine($"file {relative}\noption framerate 1000");
            }
            else manifest.WriteLine(FormattableString.Invariant($"duration {milliseconds / 1000d:0.000}"));
        }
        return (new(info.Width, info.Height), duration, times);
    }
}
