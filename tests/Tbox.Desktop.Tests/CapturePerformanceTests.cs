using System.Diagnostics;
using System.Text.Json;
using Avalonia.Headless.XUnit;
using Tbox.Desktop.Models;
using Xunit;

namespace Tbox.Desktop.Tests;

public sealed class CapturePerformanceTests
{
    [AvaloniaFact]
    public void ComparePngRoundTripWithDirectPixelPresentationWhenRequested()
    {
        // Opt-in local benchmark; no timing thresholds or screen/personal data in normal CI tests.
        var reportPath = Environment.GetEnvironmentVariable("TBOX_CAPTURE_BENCHMARK");
        if (string.IsNullOrEmpty(reportPath)) return;
        var results = new List<object>();
        foreach (var (width, height) in new[] { (1920, 1080), (3840, 2160) })
        {
            var pixels = new byte[width * height * 4];
            new Random(42).NextBytes(pixels);
            for (var i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
            Action roundTrip = () =>
            {
                using var raw = Picture.FromPixels(pixels, width, height);
                using var decoded = new Picture(raw.Png);
                Assert.Equal(width, decoded.Bitmap.PixelSize.Width);
            };
            Action direct = () =>
            {
                using var picture = Picture.FromPixels(pixels, width, height);
                Assert.False(picture.HasEncodedPng);
                Assert.Equal(width, picture.Bitmap.PixelSize.Width);
            };
            roundTrip(); direct();
            results.Add(new { width, height, iterations = 7, pngRoundTrip = Measure(roundTrip), directPixels = Measure(direct) });
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath))!);
        File.WriteAllText(reportPath, JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static object Measure(Action action)
    {
        var times = new List<double>();
        var allocations = new List<long>();
        for (var i = 0; i < 7; i++)
        {
            var allocated = GC.GetAllocatedBytesForCurrentThread();
            var timer = Stopwatch.StartNew();
            action();
            times.Add(timer.Elapsed.TotalMilliseconds);
            allocations.Add(GC.GetAllocatedBytesForCurrentThread() - allocated);
        }
        times.Sort(); allocations.Sort();
        return new { medianMs = times[3], maxMs = times[^1], medianManagedBytes = allocations[3] };
    }
}
