using System.Text.Json;
using Tbox.Desktop.Models;
using Tbox.Desktop.Platform;

namespace Tbox.Desktop;

internal static class PermissionDiagnostics
{
    public static async Task<string> RunAsync(IPlatformServices platform)
    {
        var permission = platform.HasScreenPermission;
        var sizes = new List<object>();
        string? error = null;
        if (permission)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(35));
                foreach (var display in await platform.CaptureAsync(timeout.Token))
                {
                    using var picture = display.CreatePicture();
                    sizes.Add(new { width = picture.Bitmap.PixelSize.Width, height = picture.Bitmap.PixelSize.Height });
                }
            }
            catch (Exception failure) { error = failure.Message; }
        }
        // Do not request/reset permissions, save pixels, record window names, or expose recognized text.
        return JsonSerializer.Serialize(new
        {
            processId = Environment.ProcessId, applicationDirectory = AppContext.BaseDirectory,
            version = typeof(PermissionDiagnostics).Assembly.GetName().Version?.ToString(),
            screenPermission = permission, captureVerified = permission && sizes.Count > 0 && error is null,
            displays = sizes, error
        });
    }
}
