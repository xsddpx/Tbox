namespace Tbox.Core;

public enum ClipboardPinSource
{
    Bitmap,
    ImageFile,
    Text
}

public readonly record struct ClipboardTextCardSize(int Width, int Height);

public static class ClipboardPinContentPolicy
{
    public static IReadOnlyList<ClipboardPinSource> GetPreferredSources(
        bool hasBitmap,
        bool hasImageFiles,
        bool hasText)
    {
        var sources = new List<ClipboardPinSource>(3);
        if (hasBitmap) sources.Add(ClipboardPinSource.Bitmap);
        if (hasImageFiles) sources.Add(ClipboardPinSource.ImageFile);
        if (hasText) sources.Add(ClipboardPinSource.Text);
        return sources;
    }

    public static string? NormalizeText(string? text)
    {
        if (text is null) return null;

        var normalized = text
            .Replace("\0", string.Empty, StringComparison.Ordinal)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Trim('\n');
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    public static ClipboardTextCardSize FitTextCard(
        double measuredWidth,
        double measuredHeight,
        int padding,
        int minimumWidth,
        int maximumWidth,
        int minimumHeight,
        int maximumHeight)
    {
        if (!double.IsFinite(measuredWidth) || measuredWidth < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(measuredWidth));
        }
        if (!double.IsFinite(measuredHeight) || measuredHeight < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(measuredHeight));
        }
        if (padding < 0) throw new ArgumentOutOfRangeException(nameof(padding));
        if (minimumWidth <= 0 || maximumWidth < minimumWidth)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumWidth));
        }
        if (minimumHeight <= 0 || maximumHeight < minimumHeight)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumHeight));
        }

        var horizontalPadding = checked(padding * 2);
        var verticalPadding = checked(padding * 2);
        var width = Math.Clamp(
            checked((int)Math.Ceiling(measuredWidth) + horizontalPadding),
            minimumWidth,
            maximumWidth);
        var height = Math.Clamp(
            checked((int)Math.Ceiling(measuredHeight) + verticalPadding),
            minimumHeight,
            maximumHeight);
        return new ClipboardTextCardSize(width, height);
    }
}
