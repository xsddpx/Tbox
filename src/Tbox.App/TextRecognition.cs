using System.Runtime.InteropServices.WindowsRuntime;
using Tbox.Core;
using Windows.ApplicationModel;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace Tbox.App;

internal sealed record TextRecognitionImage(int Width, int Height, byte[] BgraPixels)
{
    internal static TextRecognitionImage FromCapturedScreen(CapturedScreen screen) =>
        new(screen.Width, screen.Height, screen.Pixels);
}

internal readonly record struct TextRecognitionBounds(double X, double Y, double Width, double Height);

internal sealed record TextRecognitionWord(string Text, TextRecognitionBounds Bounds);

internal sealed record TextRecognitionLine(
    string Text,
    IReadOnlyList<TextRecognitionWord> Words);

internal sealed record TextRecognitionResult(
    string Text,
    string LanguageTag,
    IReadOnlyList<TextRecognitionLine> Lines);

internal interface ITextRecognitionService
{
    Task<TextRecognitionResult> RecognizeAsync(
        TextRecognitionImage image,
        CancellationToken cancellationToken = default);
}

internal sealed class TextRecognitionUnavailableException(string message, Exception? innerException = null)
    : Exception(message, innerException);

internal sealed class WindowsTextRecognitionService : ITextRecognitionService
{
    private readonly SemaphoreSlim _recognitionGate = new(1, 1);

    public async Task<TextRecognitionResult> RecognizeAsync(
        TextRecognitionImage image,
        CancellationToken cancellationToken = default)
    {
        ValidateImage(image);
        EnsurePackageIdentity();

        await _recognitionGate.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var engine = OcrEngine.TryCreateFromUserProfileLanguages();
            if (engine is null)
            {
                throw new TextRecognitionUnavailableException(
                    "Windows 未安装可用的文字识别语言。请在“设置 → 时间和语言 → 语言和区域”中添加所需语言后重试。");
            }

            var targetSize = OcrImageScalePolicy.FitWithinMaximumDimension(
                image.Width,
                image.Height,
                checked((int)OcrEngine.MaxImageDimension));
            using var bitmap = await CreateSoftwareBitmapAsync(image, targetSize, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var result = await engine.RecognizeAsync(bitmap).AsTask(cancellationToken);

            var scaleX = image.Width / (double)targetSize.Width;
            var scaleY = image.Height / (double)targetSize.Height;
            var lines = result.Lines.Select(line =>
            {
                var words = line.Words.Select(word =>
                {
                    var bounds = word.BoundingRect;
                    return new TextRecognitionWord(
                        word.Text,
                        new TextRecognitionBounds(
                            bounds.X * scaleX,
                            bounds.Y * scaleY,
                            bounds.Width * scaleX,
                            bounds.Height * scaleY));
                }).ToArray();
                return new TextRecognitionLine(line.Text, words);
            }).ToArray();

            return new TextRecognitionResult(
                string.Join(Environment.NewLine, lines.Select(line => line.Text)),
                engine.RecognizerLanguage.LanguageTag,
                lines);
        }
        finally
        {
            _recognitionGate.Release();
        }
    }

    private static void ValidateImage(TextRecognitionImage image)
    {
        if (image.Width <= 0 || image.Height <= 0)
        {
            throw new ArgumentException("待识别图像的尺寸无效。", nameof(image));
        }

        var expectedLength = checked(image.Width * image.Height * 4);
        if (image.BgraPixels.Length != expectedLength)
        {
            throw new ArgumentException("待识别图像的像素数据不完整。", nameof(image));
        }
    }

    private static void EnsurePackageIdentity()
    {
        try
        {
            _ = Package.Current.Id.Name;
        }
        catch (Exception exception)
        {
            throw new TextRecognitionUnavailableException(
                "系统文字识别仅在已安装的 Tbox MSIX 中可用。请安装当前版本后重试。",
                exception);
        }
    }

    private static async Task<SoftwareBitmap> CreateSoftwareBitmapAsync(
        TextRecognitionImage image,
        OcrImageSize targetSize,
        CancellationToken cancellationToken)
    {
        using var source = new SoftwareBitmap(
            BitmapPixelFormat.Bgra8,
            image.Width,
            image.Height,
            BitmapAlphaMode.Straight);
        source.CopyFromBuffer(image.BgraPixels.AsBuffer());

        if (targetSize.Width == image.Width && targetSize.Height == image.Height)
        {
            return SoftwareBitmap.Copy(source);
        }

        using var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetSoftwareBitmap(source);
        encoder.BitmapTransform.ScaledWidth = checked((uint)targetSize.Width);
        encoder.BitmapTransform.ScaledHeight = checked((uint)targetSize.Height);
        encoder.BitmapTransform.InterpolationMode = BitmapInterpolationMode.Fant;
        await encoder.FlushAsync().AsTask(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        stream.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(stream);
        return await decoder.GetSoftwareBitmapAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Straight)
            .AsTask(cancellationToken);
    }
}
