using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI;
using Tbox.Core;
using Windows.Storage.Streams;
using Windows.UI;

namespace Tbox.App;

internal static class ClipboardTextCardRenderer
{
    private const int Padding = 28;
    private const int MinimumWidth = 280;
    private const int MaximumWidth = 760;
    private const int MinimumHeight = 112;
    private const int MaximumHeight = 4096;
    private const float FontSize = 22;
    private const float MeasurementWidth = 100_000;
    private static readonly Color BackgroundColor = Color.FromArgb(255, 249, 250, 251);
    private static readonly Color ForegroundColor = Color.FromArgb(255, 31, 35, 40);

    internal static async Task<byte[]> RenderPngAsync(string text)
    {
        var normalized = ClipboardPinContentPolicy.NormalizeText(text) ??
                         throw new ArgumentException("不能渲染空白剪贴板文本。", nameof(text));
        var device = CanvasDevice.GetSharedDevice();

        using var measurementFormat = CreateTextFormat(CanvasWordWrapping.NoWrap);
        using var unwrappedLayout = new CanvasTextLayout(
            device,
            normalized,
            measurementFormat,
            MeasurementWidth,
            MaximumHeight - (Padding * 2));
        var measuredWidth = unwrappedLayout.LayoutBoundsIncludingTrailingWhitespace.Width;
        var widthOnly = ClipboardPinContentPolicy.FitTextCard(
            measuredWidth,
            0,
            Padding,
            MinimumWidth,
            MaximumWidth,
            MinimumHeight,
            MaximumHeight);
        var contentWidth = widthOnly.Width - (Padding * 2);

        using var wrappedFormat = CreateTextFormat(CanvasWordWrapping.Wrap);
        using var wrappedLayout = new CanvasTextLayout(
            device,
            normalized,
            wrappedFormat,
            contentWidth,
            MaximumHeight - (Padding * 2));
        var cardSize = ClipboardPinContentPolicy.FitTextCard(
            measuredWidth,
            wrappedLayout.LayoutBoundsIncludingTrailingWhitespace.Height,
            Padding,
            MinimumWidth,
            MaximumWidth,
            MinimumHeight,
            MaximumHeight);

        using var finalLayout = new CanvasTextLayout(
            device,
            normalized,
            wrappedFormat,
            cardSize.Width - (Padding * 2),
            cardSize.Height - (Padding * 2));
        using var target = new CanvasRenderTarget(device, cardSize.Width, cardSize.Height, 96);
        using (var drawingSession = target.CreateDrawingSession())
        {
            drawingSession.Clear(BackgroundColor);
            drawingSession.DrawTextLayout(finalLayout, Padding, Padding, ForegroundColor);
        }

        using var output = new InMemoryRandomAccessStream();
        await target.SaveAsync(output, CanvasBitmapFileFormat.Png);
        output.Seek(0);
        var length = checked((uint)output.Size);
        var bytes = new byte[checked((int)length)];
        using var reader = new DataReader(output.GetInputStreamAt(0));
        var loaded = await reader.LoadAsync(length);
        if (loaded != length) throw new EndOfStreamException("无法完整读取剪贴板文字贴图。");
        reader.ReadBytes(bytes);
        return bytes;
    }

    private static CanvasTextFormat CreateTextFormat(CanvasWordWrapping wordWrapping) => new()
    {
        FontFamily = "Segoe UI",
        FontSize = FontSize,
        HorizontalAlignment = CanvasHorizontalAlignment.Left,
        VerticalAlignment = CanvasVerticalAlignment.Top,
        WordWrapping = wordWrapping,
        TrimmingGranularity = CanvasTextTrimmingGranularity.Character,
        TrimmingSign = CanvasTrimmingSign.Ellipsis
    };
}
