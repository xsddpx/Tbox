using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Tbox.Core;
using Tbox.Desktop.Models;
using Tbox.Desktop.Platform;

namespace Tbox.Desktop.Views;

internal sealed class RecordingReviewWindow : Window
{
    private readonly VideoConverterView _converter;
    private Task<bool>? _closeRequest;
    private bool _allowClose;

    internal static IReadOnlyList<RecordingExportFormat> Formats => VideoConversionEditor.Formats;
    private VideoConversionEditor Editor => _converter.Editor!;
    internal RecordingTrimBar Track => Editor.Track;
    internal ComboBox FrameRate => Editor.FrameRate;
    internal ComboBox Size => Editor.Size;
    internal TextBox CustomWidth => Editor.CustomWidth;
    internal Popup ThumbnailPopup => Editor.ThumbnailPopup;
    internal int LoadedThumbnails => Editor.LoadedThumbnails;
    internal RecordingExportOptions Options() => Editor.Options();
    public event Action? Released;

    public RecordingReviewWindow(DesktopController controller, RecordingWorkspace workspace, RecordingArea area,
        RecordingOutput output, TimeSpan duration, Action? restoreForeground = null)
    {
        Ui.Configure(this, "视频转换", 960, 650);
        MinWidth = 480; MinHeight = 395;
        Ui.ExtendIntoTitleBar(this);
        _converter = new VideoConverterView(controller, async () => { await RequestCloseAsync(); }) { Margin = new Thickness(16, 32, 16, 14) };
        _converter.SetSource(workspace, new(output.FilePath, "屏幕录制", new(area.PixelWidth, area.PixelHeight),
            duration, RecordingFrameRatePolicy.RecordingFrameRate, output.HasSystemAudio, true));
        Content = new ScrollViewer { Content = _converter, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Opened += (_, _) => _converter.Start();
        Closed += (_, _) => { if (!controller.IsQuitting) restoreForeground?.Invoke(); };
        Closing += (_, args) => { if (!_allowClose) { args.Cancel = true; controller.Run(async () => { await RequestCloseAsync(); }); } };
        KeyDown += (_, args) =>
        {
            if (args.Key != Key.Escape) return;
            args.Handled = true;
            controller.Run(async () => { await RequestCloseAsync(); });
        };
    }

    public async Task<bool> RequestCloseAsync(bool discardUnsaved = false)
    {
        if (_allowClose) return true;
        if (_closeRequest is not null)
        {
            if (discardUnsaved && _converter.Editor is { } editor) await editor.ConfirmDiscardAsync(this, true);
            return await _closeRequest;
        }
        _closeRequest = _converter.RequestCloseAsync(this, discardUnsaved, () => { _allowClose = true; Close(); });
        try { return await _closeRequest; }
        finally
        {
            _closeRequest = null;
            if (_allowClose) Released?.Invoke();
        }
    }

    internal static string FormatTime(TimeSpan duration) => duration.TotalHours >= 1 ? duration.ToString(@"h\:mm\:ss") : duration.ToString(@"mm\:ss");
}
