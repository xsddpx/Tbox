namespace Tbox.Core;

public enum ScrollCaptureFrameDecision
{
    Started,
    Duplicate,
    Appended,
    Moved,
    NoMatch,
    LimitReached
}

public enum ScrollCaptureDirection
{
    None,
    Up,
    Down
}

public readonly record struct ScrollCaptureFrameResult(
    ScrollCaptureFrameDecision Decision,
    int AddedRows,
    int TotalHeight,
    double MatchError,
    ScrollCaptureDirection Direction,
    int ViewportTop);

public sealed class ScrollCaptureStitcher
{
    public const int DefaultMaximumPixels = 25_000_000;
    public const int DefaultMaximumHeight = 50_000;

    private const double ExactDuplicateErrorThreshold = 0.18;
    private const double SimilarFrameErrorThreshold = 2.5;
    private const double MatchErrorThreshold = 13;
    private readonly int _width;
    private readonly int _frameHeight;
    private readonly int _maximumHeight;
    private readonly List<byte[]> _segments = [];
    private byte[]? _lastFrame;
    private ScrollCaptureDirection _lastDirection;

    public ScrollCaptureStitcher(
        int width,
        int frameHeight,
        int maximumPixels = DefaultMaximumPixels,
        int maximumHeight = DefaultMaximumHeight)
    {
        if (width < 1) throw new ArgumentOutOfRangeException(nameof(width));
        if (frameHeight < 2) throw new ArgumentOutOfRangeException(nameof(frameHeight));
        if (maximumPixels < width * 2L) throw new ArgumentOutOfRangeException(nameof(maximumPixels));
        if (maximumHeight < 2) throw new ArgumentOutOfRangeException(nameof(maximumHeight));

        _width = width;
        _frameHeight = frameHeight;
        _maximumHeight = Math.Min(maximumHeight, maximumPixels / width);
    }

    public int Width => _width;
    public int Height { get; private set; }
    public int ViewportTop { get; private set; }
    public bool HasFrame => _lastFrame is not null;

    public ScrollCaptureFrameResult AddFrame(ReadOnlySpan<byte> bgraPixels)
    {
        var expectedLength = checked(_width * _frameHeight * 4);
        if (bgraPixels.Length != expectedLength)
        {
            throw new ArgumentException("滚动截图帧尺寸不匹配。", nameof(bgraPixels));
        }

        if (_lastFrame is null)
        {
            var firstRows = Math.Min(_frameHeight, _maximumHeight);
            var first = bgraPixels[..checked(firstRows * _width * 4)].ToArray();
            _segments.Add(first);
            _lastFrame = bgraPixels.ToArray();
            Height = firstRows;
            var firstDecision = firstRows < _frameHeight
                ? ScrollCaptureFrameDecision.LimitReached
                : ScrollCaptureFrameDecision.Started;
            ViewportTop = 0;
            return new ScrollCaptureFrameResult(
                firstDecision,
                firstRows,
                Height,
                0,
                ScrollCaptureDirection.None,
                ViewportTop);
        }

        var difference = CalculateFrameDifference(_lastFrame, bgraPixels);
        if (difference.MeanError <= ExactDuplicateErrorThreshold || difference.ChangedRatio <= 0.0015)
        {
            return new ScrollCaptureFrameResult(
                ScrollCaptureFrameDecision.Duplicate,
                0,
                Height,
                difference.MeanError,
                ScrollCaptureDirection.None,
                ViewportTop);
        }

        var downward = FindBestShift(_lastFrame, bgraPixels, ScrollCaptureDirection.Down);
        var upward = FindBestShift(_lastFrame, bgraPixels, ScrollCaptureDirection.Up);
        var match = ChooseMatch(downward, upward);
        if (difference.MeanError <= SimilarFrameErrorThreshold &&
            difference.ChangedRatio <= 0.02 &&
            match.Error >= difference.MeanError * 0.6)
        {
            return new ScrollCaptureFrameResult(
                ScrollCaptureFrameDecision.Duplicate,
                0,
                Height,
                difference.MeanError,
                ScrollCaptureDirection.None,
                ViewportTop);
        }
        if (match.Shift <= 0 || match.Error > MatchErrorThreshold)
        {
            return new ScrollCaptureFrameResult(
                ScrollCaptureFrameDecision.NoMatch,
                0,
                Height,
                match.Error,
                ScrollCaptureDirection.None,
                ViewportTop);
        }

        return match.Direction == ScrollCaptureDirection.Up
            ? ApplyUpwardFrame(bgraPixels, match)
            : ApplyDownwardFrame(bgraPixels, match);
    }

    public byte[] Complete()
    {
        if (_segments.Count == 0) throw new InvalidOperationException("还没有可完成的滚动截图。");

        var output = new byte[checked(_width * Height * 4)];
        var offset = 0;
        foreach (var segment in _segments)
        {
            Buffer.BlockCopy(segment, 0, output, offset, segment.Length);
            offset += segment.Length;
        }
        return output;
    }

    public static double CalculateFrameError(ReadOnlySpan<byte> first, ReadOnlySpan<byte> second)
        => CalculateFrameDifference(first, second).MeanError;

    public static bool AreFramesVisuallyStable(ReadOnlySpan<byte> first, ReadOnlySpan<byte> second)
    {
        var difference = CalculateFrameDifference(first, second);
        return difference.MeanError <= 1.5 && difference.ChangedRatio <= 0.05;
    }

    private static FrameDifference CalculateFrameDifference(
        ReadOnlySpan<byte> first,
        ReadOnlySpan<byte> second)
    {
        if (first.Length != second.Length || first.Length % 4 != 0)
        {
            throw new ArgumentException("画面像素数据尺寸不一致。", nameof(second));
        }

        if (first.IsEmpty) return new FrameDifference(0, 0);
        long difference = 0;
        var pixelCount = first.Length / 4;
        var sampleCount = Math.Min(pixelCount, 20_000);
        var changedSamples = 0;
        for (var sample = 0; sample < sampleCount; sample++)
        {
            var cellStart = (int)((long)sample * pixelCount / sampleCount);
            var cellEnd = (int)((long)(sample + 1) * pixelCount / sampleCount);
            var cellWidth = Math.Max(1, cellEnd - cellStart);
            var jitter = (int)((uint)(sample * 1_103_515_245 + 12_345) % (uint)cellWidth);
            var pixel = Math.Min(pixelCount - 1, cellStart + jitter);
            var index = pixel * 4;
            var sampleDifference = Math.Abs(Luma(first, index) - Luma(second, index));
            difference += sampleDifference;
            if (sampleDifference >= 8) changedSamples++;
        }
        return new FrameDifference(
            difference / (double)sampleCount,
            changedSamples / (double)sampleCount);
    }

    private ScrollMatch FindBestShift(
        ReadOnlySpan<byte> previous,
        ReadOnlySpan<byte> current,
        ScrollCaptureDirection direction)
    {
        var minimumOverlap = Math.Max(24, _frameHeight / 5);
        var maximumShift = _frameHeight - minimumOverlap;
        if (maximumShift < 1) return new ScrollMatch(direction, 0, double.MaxValue);

        var coarseStep = Math.Max(1, _frameHeight / 160);
        var bestShift = 0;
        var bestError = double.MaxValue;
        for (var shift = 1; shift <= maximumShift; shift += coarseStep)
        {
            var error = CalculateOverlapError(previous, current, shift, direction);
            if (error < bestError)
            {
                bestShift = shift;
                bestError = error;
            }
        }

        var refineStart = Math.Max(1, bestShift - coarseStep);
        var refineEnd = Math.Min(maximumShift, bestShift + coarseStep);
        for (var shift = refineStart; shift <= refineEnd; shift++)
        {
            var error = CalculateOverlapError(previous, current, shift, direction);
            if (error < bestError)
            {
                bestShift = shift;
                bestError = error;
            }
        }
        return new ScrollMatch(direction, bestShift, bestError);
    }

    private double CalculateOverlapError(
        ReadOnlySpan<byte> previous,
        ReadOnlySpan<byte> current,
        int shift,
        ScrollCaptureDirection direction)
    {
        var overlap = _frameHeight - shift;
        var startX = _width / 12;
        var endX = _width - startX;
        var xStep = Math.Max(1, (endX - startX) / 160);
        var verticalMargin = Math.Min(
            Math.Max(2, _frameHeight / 12),
            Math.Max(0, (overlap - 24) / 3));
        var sampleStartY = verticalMargin;
        var sampleEndY = overlap - verticalMargin;
        var sampleHeight = sampleEndY - sampleStartY;
        var rowSamples = Math.Min(64, sampleHeight);
        long difference = 0;
        var samples = 0;

        for (var rowSample = 0; rowSample < rowSamples; rowSample++)
        {
            var currentY = rowSamples == 1
                ? sampleStartY
                : sampleStartY + rowSample * (sampleHeight - 1) / (rowSamples - 1);
            var previousY = direction == ScrollCaptureDirection.Down
                ? currentY + shift
                : currentY;
            var matchedCurrentY = direction == ScrollCaptureDirection.Down
                ? currentY
                : currentY + shift;
            for (var x = startX; x < endX; x += xStep)
            {
                var previousIndex = checked((previousY * _width + x) * 4);
                var currentIndex = checked((matchedCurrentY * _width + x) * 4);
                difference += Math.Abs(Luma(previous, previousIndex) - Luma(current, currentIndex));
                samples++;
            }
        }

        return samples == 0 ? double.MaxValue : difference / (double)samples;
    }

    private static int Luma(ReadOnlySpan<byte> pixels, int index) =>
        (pixels[index] * 29 + pixels[index + 1] * 150 + pixels[index + 2] * 77) >> 8;

    private ScrollMatch ChooseMatch(ScrollMatch downward, ScrollMatch upward)
    {
        const double equivalentErrorTolerance = 0.05;
        if (_lastDirection == ScrollCaptureDirection.Down &&
            downward.Error <= upward.Error + equivalentErrorTolerance)
        {
            return downward;
        }
        if (_lastDirection == ScrollCaptureDirection.Up &&
            upward.Error <= downward.Error + equivalentErrorTolerance)
        {
            return upward;
        }
        return downward.Error <= upward.Error ? downward : upward;
    }

    private ScrollCaptureFrameResult ApplyDownwardFrame(
        ReadOnlySpan<byte> bgraPixels,
        ScrollMatch match)
    {
        var nextViewportTop = ViewportTop + match.Shift;
        var requiredBottom = nextViewportTop + _frameHeight;
        var requestedRows = Math.Max(0, requiredBottom - Height);
        if (requestedRows == 0)
        {
            _lastFrame = bgraPixels.ToArray();
            _lastDirection = ScrollCaptureDirection.Down;
            ViewportTop = nextViewportTop;
            return new ScrollCaptureFrameResult(
                ScrollCaptureFrameDecision.Moved,
                0,
                Height,
                match.Error,
                match.Direction,
                ViewportTop);
        }

        var allowedRows = Math.Min(requestedRows, _maximumHeight - Height);
        if (allowedRows <= 0)
        {
            return new ScrollCaptureFrameResult(
                ScrollCaptureFrameDecision.LimitReached,
                0,
                Height,
                match.Error,
                match.Direction,
                ViewportTop);
        }

        var sourceRow = _frameHeight - requestedRows;
        var sourceOffset = checked(sourceRow * _width * 4);
        var segmentLength = checked(allowedRows * _width * 4);
        _segments.Add(bgraPixels.Slice(sourceOffset, segmentLength).ToArray());
        _lastFrame = bgraPixels.ToArray();
        _lastDirection = ScrollCaptureDirection.Down;
        Height += allowedRows;
        ViewportTop = Math.Clamp(nextViewportTop, 0, Math.Max(0, Height - _frameHeight));

        var decision = allowedRows < requestedRows || Height >= _maximumHeight
            ? ScrollCaptureFrameDecision.LimitReached
            : ScrollCaptureFrameDecision.Appended;
        return new ScrollCaptureFrameResult(
            decision,
            allowedRows,
            Height,
            match.Error,
            match.Direction,
            ViewportTop);
    }

    private ScrollCaptureFrameResult ApplyUpwardFrame(
        ReadOnlySpan<byte> bgraPixels,
        ScrollMatch match)
    {
        var nextViewportTop = ViewportTop - match.Shift;
        var requestedRows = Math.Max(0, -nextViewportTop);
        if (requestedRows == 0)
        {
            _lastFrame = bgraPixels.ToArray();
            _lastDirection = ScrollCaptureDirection.Up;
            ViewportTop = nextViewportTop;
            return new ScrollCaptureFrameResult(
                ScrollCaptureFrameDecision.Moved,
                0,
                Height,
                match.Error,
                match.Direction,
                ViewportTop);
        }

        var allowedRows = Math.Min(requestedRows, _maximumHeight - Height);
        if (allowedRows <= 0)
        {
            return new ScrollCaptureFrameResult(
                ScrollCaptureFrameDecision.LimitReached,
                0,
                Height,
                match.Error,
                match.Direction,
                ViewportTop);
        }

        var sourceRow = requestedRows - allowedRows;
        var sourceOffset = checked(sourceRow * _width * 4);
        var segmentLength = checked(allowedRows * _width * 4);
        _segments.Insert(0, bgraPixels.Slice(sourceOffset, segmentLength).ToArray());
        _lastFrame = bgraPixels.ToArray();
        _lastDirection = ScrollCaptureDirection.Up;
        Height += allowedRows;
        ViewportTop = Math.Max(0, nextViewportTop + allowedRows);

        var decision = allowedRows < requestedRows || Height >= _maximumHeight
            ? ScrollCaptureFrameDecision.LimitReached
            : ScrollCaptureFrameDecision.Appended;
        return new ScrollCaptureFrameResult(
            decision,
            allowedRows,
            Height,
            match.Error,
            match.Direction,
            ViewportTop);
    }

    private readonly record struct FrameDifference(double MeanError, double ChangedRatio);
    private readonly record struct ScrollMatch(
        ScrollCaptureDirection Direction,
        int Shift,
        double Error);
}
