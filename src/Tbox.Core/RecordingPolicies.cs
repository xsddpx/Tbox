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
    Mp4
}

public static class RecordingFrameRatePolicy
{
    private static readonly int[] FrameRates = [5, 10, 15, 25, 60];

    public const int DefaultFrameRate = 10;

    public static IReadOnlyList<int> AllowedFrameRates => FrameRates;

    public static bool IsAllowed(int framesPerSecond) => Array.BinarySearch(FrameRates, framesPerSecond) >= 0;

    public static int Validate(int framesPerSecond) => IsAllowed(framesPerSecond)
        ? framesPerSecond
        : throw new ArgumentOutOfRangeException(
            nameof(framesPerSecond),
            framesPerSecond,
            "录屏帧率必须为 5、10、15、25 或 60 FPS。");
}

public readonly record struct RecordingExportProfile(
    RecordingExportFormat Format,
    string DisplayName,
    string Description,
    string Extension,
    int FramesPerSecond,
    int? MaximumLongEdge,
    int? Quality,
    bool SupportsAudio);

public static class RecordingExportProfiles
{
    public static RecordingExportProfile Get(RecordingExportFormat format) => format switch
    {
        RecordingExportFormat.Gif => new(
            format,
            "GIF",
            "图像，兼容性高，质量较低",
            ".gif",
            12,
            1280,
            null,
            false),
        RecordingExportFormat.WebP => new(
            format,
            "WEBP",
            "图像，质量好，兼容性适中",
            ".webp",
            24,
            1920,
            80,
            false),
        RecordingExportFormat.Mp4 => new(
            format,
            "MP4",
            "视频，体积小，质量好",
            ".mp4",
            RecordingFrameRatePolicy.DefaultFrameRate,
            null,
            null,
            true),
        _ => throw new ArgumentOutOfRangeException(nameof(format))
    };
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
        int framesPerSecond = RecordingFrameRatePolicy.DefaultFrameRate)
    {
        if (width < 1 || height < 1 || framesPerSecond < 1) throw new ArgumentOutOfRangeException();
        var calculated = checked((long)Math.Round(width * (double)height * framesPerSecond * 0.12));
        return checked((int)Math.Clamp(calculated, 2_000_000, 20_000_000));
    }
}
