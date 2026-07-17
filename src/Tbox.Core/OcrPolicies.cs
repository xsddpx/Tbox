namespace Tbox.Core;

public readonly record struct OcrImageSize(int Width, int Height);

public static class OcrImageScalePolicy
{
    public static OcrImageSize FitWithinMaximumDimension(int width, int height, int maximumDimension)
    {
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));
        if (maximumDimension <= 0) throw new ArgumentOutOfRangeException(nameof(maximumDimension));

        var largestDimension = Math.Max(width, height);
        if (largestDimension <= maximumDimension) return new OcrImageSize(width, height);

        var scale = maximumDimension / (double)largestDimension;
        return new OcrImageSize(
            Math.Max(1, (int)Math.Floor(width * scale)),
            Math.Max(1, (int)Math.Floor(height * scale)));
    }
}

public static class OcrLineNumberPolicy
{
    public static IReadOnlyList<int> GetLogicalLineStarts(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var starts = new List<int> { 0 };
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] == '\n') starts.Add(index + 1);
        }
        return starts;
    }
}
