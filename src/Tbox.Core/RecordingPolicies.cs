namespace Tbox.Core;

public enum RecordingSessionState
{
    Ready,
    Recording,
    Paused,
    Reviewing,
    Exporting,
    Closed,
    Failed
}

public enum RecordingExportFormat
{
    Gif,
    WebP,
    Mp4,
    ContactSheet
}

public static class RecordingFrameRatePolicy
{
    // Every master is recorded at one rate; output rates are chosen when saving.
    public const int RecordingFrameRate = 30;

    public static int Validate(int framesPerSecond) => framesPerSecond == RecordingFrameRate
        ? framesPerSecond
        : throw new ArgumentOutOfRangeException(nameof(framesPerSecond), framesPerSecond, "录屏帧率必须为 30 FPS。");
}

public readonly record struct RecordingExportProfile(
    RecordingExportFormat Format,
    string DisplayName,
    string Description,
    string Extension,
    int DefaultFramesPerSecond,
    int? DefaultMaximumLongEdge,
    int? Quality,
    bool SupportsAudio);

public static class RecordingExportProfiles
{
    public static IReadOnlyList<int> FrameRates { get; } = [1, 5, 10, 15, 25, 30];

    public static RecordingExportProfile Get(RecordingExportFormat format) => format switch
    {
        RecordingExportFormat.Gif => new(format, "GIF", "图像，兼容性高，质量较低", ".gif", 10, 1280, null, false),
        RecordingExportFormat.WebP => new(format, "WEBP", "图像，质量好，兼容性适中", ".webp", 25, null, 80, false),
        RecordingExportFormat.Mp4 => new(format, "MP4", "视频，体积小，质量好", ".mp4", RecordingFrameRatePolicy.RecordingFrameRate, null, null, true),
        RecordingExportFormat.ContactSheet => new(format, "宫格图", "图片，在截取范围内均匀取帧", ".jpg", 0, null, 90, false),
        _ => throw new ArgumentOutOfRangeException(nameof(format))
    };
}

public readonly record struct VideoSize(int Width, int Height);

public readonly record struct RecordingSizeChoice(string Label, VideoSize Size);

public static class RecordingSizePolicy
{
    private static readonly int[] Percentages = [100, 75, 50, 25];

    /// <summary>Output scaled to fit the requested width, never enlarged; videos need even edges.</summary>
    public static VideoSize ScaleToWidth(VideoSize source, int width, bool even)
    {
        if (source.Width < 1 || source.Height < 1) throw new ArgumentOutOfRangeException(nameof(source));
        width = Math.Clamp(width, 1, source.Width);
        var height = Math.Max(1, (int)Math.Round(source.Height * (double)width / source.Width));
        return even ? new(Math.Max(2, width - width % 2), Math.Max(2, height - height % 2)) : new(width, height);
    }

    public static VideoSize FitLongEdge(VideoSize source, int longEdge, bool even)
    {
        var scale = Math.Min(1, longEdge / (double)Math.Max(source.Width, source.Height));
        return ScaleToWidth(source, Math.Max(1, (int)Math.Round(source.Width * scale)), even);
    }

    public static IReadOnlyList<RecordingSizeChoice> Choices(VideoSize source, bool even) => Percentages
        .Select(p => new RecordingSizeChoice(p + "%", ScaleToWidth(source, Math.Max(1, (int)Math.Round(source.Width * p / 100.0)), even)))
        .ToArray();

    /// <summary>Index into <see cref="Choices"/> for a format's default size.</summary>
    public static int DefaultChoice(VideoSize source, RecordingExportFormat format, bool even)
    {
        if (RecordingExportProfiles.Get(format).DefaultMaximumLongEdge is not { } limit) return 0;
        var choices = Choices(source, even);
        for (var i = 0; i < choices.Count; i++)
            if (Math.Max(choices[i].Size.Width, choices[i].Size.Height) <= limit) return i;
        return choices.Count - 1;
    }
}

public readonly record struct RecordingTimeRange(TimeSpan Start, TimeSpan End)
{
    public static readonly TimeSpan MinimumLength = TimeSpan.FromMilliseconds(500);
    public TimeSpan Length => End - Start;
    public bool IsFull(TimeSpan duration) => Start <= TimeSpan.Zero && End >= duration;

    /// <summary>Clamp into the recording and keep at least the minimum length (or the whole clip if it is shorter).</summary>
    public static RecordingTimeRange Normalize(TimeSpan start, TimeSpan end, TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero) return new(TimeSpan.Zero, TimeSpan.Zero);
        var minimum = duration < MinimumLength ? duration : MinimumLength;
        start = Clamp(start, TimeSpan.Zero, duration - minimum);
        end = Clamp(end, start + minimum, duration);
        return new(start, end);
    }

    private static TimeSpan Clamp(TimeSpan value, TimeSpan min, TimeSpan max) => value < min ? min : value > max ? max : value;
}

public readonly record struct RecordingExportOptions(
    RecordingExportFormat Format,
    int FramesPerSecond,
    VideoSize Size,
    RecordingTimeRange Range,
    int FrameCount = ContactSheetPolicy.DefaultCount)
{
    /// <summary>An unchanged MP4 is copied from the master without re-encoding.</summary>
    public bool CopiesMaster(VideoSize source, TimeSpan duration) =>
        Format == RecordingExportFormat.Mp4 && FramesPerSecond == RecordingFrameRatePolicy.RecordingFrameRate &&
        Size == source && Range.IsFull(duration);
}

public readonly record struct SheetCell(int X, int Y, int LabelX, int LabelY, string Label);

public static class ContactSheetPolicy
{
    public const int MinCount = 2, DefaultCount = 6, MaxCount = 24, Margin = 12, LabelStrip = 28, LabelSize = 12;
    public const int Count = DefaultCount;
    public static IReadOnlyList<int> LongEdges { get; } = [640, 480, 320, 200];
    public const int DefaultLongEdge = 320;

    /// <summary>Frame indices spaced evenly from the first to the last frame, inclusive.</summary>
    public static IReadOnlyList<long> SampleFrames(long first, long last, int count = DefaultCount)
    {
        if (last < first) throw new ArgumentOutOfRangeException(nameof(last), "范围内没有可采样的帧。");
        count = Math.Clamp(count, MinCount, MaxCount);
        return Enumerable.Range(0, count).Select(i => first + (long)Math.Round((last - first) * i / (double)(count - 1), MidpointRounding.AwayFromZero)).ToArray();
    }

    /// <summary>Sample times for a range at the master frame rate.</summary>
    public static IReadOnlyList<TimeSpan> SampleTimes(RecordingTimeRange range, int framesPerSecond, int count = DefaultCount)
    {
        var first = (long)Math.Ceiling(range.Start.TotalSeconds * framesPerSecond - 1e-5);
        var last = Math.Max(first, (long)Math.Ceiling(range.End.TotalSeconds * framesPerSecond - 1e-5) - 1);
        return SampleFrames(first, last, count).Select(f => TimeSpan.FromSeconds(f / (double)framesPerSecond)).ToArray();
    }

    public static bool HasRepeats(IReadOnlyList<TimeSpan> samples) => samples.Distinct().Count() < samples.Count;

    public static (int Columns, int Rows) Grid(int count)
    {
        count = Math.Clamp(count, MinCount, MaxCount);
        var columns = count <= 4 ? 2 : count <= 6 || count == 9 ? 3 : count <= 16 ? 4 : 5;
        return (columns, (count + columns - 1) / columns);
    }

    public static VideoSize SheetSize(VideoSize cell, int count = DefaultCount)
    {
        var (columns, rows) = Grid(count);
        return new(columns * cell.Width + (columns + 1) * Margin, rows * (cell.Height + LabelStrip) + (rows + 1) * Margin);
    }

    public static IReadOnlyList<SheetCell> Layout(VideoSize cell, int count = DefaultCount)
    {
        count = Math.Clamp(count, MinCount, MaxCount);
        var (columns, _) = Grid(count);
        return Enumerable.Range(0, count).Select(i =>
        {
            var x = Margin + i % columns * (cell.Width + Margin);
            var y = Margin + i / columns * (cell.Height + LabelStrip + Margin);
            return new SheetCell(x, y, x + 5, y + cell.Height + 8, (i + 1).ToString("00"));
        }).ToArray();
    }
}

public static class RecordingRegionPolicy
{
    public static PixelRegion NormalizeForVideo(PixelRegion region)
    {
        if (region.Width < 16 || region.Height < 16)
        {
            throw new ArgumentOutOfRangeException(nameof(region), "录屏区域至少为 16×16 像素。");
        }

        var width = region.Width - region.Width % 2;
        var height = region.Height - region.Height % 2;
        return new PixelRegion(region.Left, region.Top, width, height);
    }

    public static int CalculateVideoBitrate(
        int width,
        int height,
        int framesPerSecond = RecordingFrameRatePolicy.RecordingFrameRate)
    {
        if (width < 1 || height < 1 || framesPerSecond < 1) throw new ArgumentOutOfRangeException();
        var calculated = checked((long)Math.Round(width * (double)height * framesPerSecond * 0.12));
        return checked((int)Math.Clamp(calculated, 2_000_000, 20_000_000));
    }
}
