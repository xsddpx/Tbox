using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Tbox.Core;
using Windows.Graphics;
using Windows.UI;
using WinRT.Interop;

namespace Tbox.App;

internal static class CaptureFramePalette
{
    internal static readonly Color Screenshot = Color.FromArgb(255, 0, 120, 212);
    internal static readonly Color LongCapture = Color.FromArgb(255, 255, 204, 0);
    internal static readonly Color Recording = Color.FromArgb(255, 255, 59, 48);
}

internal sealed class CaptureRegionFrame : IDisposable
{
    private const double LogicalThickness = 2;
    private const double LogicalHandleSize = 8;
    private readonly FramePart[] _parts;
    private bool _disposed;

    internal CaptureRegionFrame(RectInt32 bounds, Color color)
    {
        var probe = new FramePart(color, isHandle: false);
        CaptureModeWindowPlacement.PrepareOnTargetDisplay(probe.WindowHandle, bounds);
        var dpi = Math.Max(96u, NativeMethods.GetDpiForWindow(probe.WindowHandle));
        var thickness = WindowDpiScalePolicy.LogicalToPhysicalPixels(LogicalThickness, dpi);
        var handleSize = WindowDpiScalePolicy.LogicalToPhysicalPixels(LogicalHandleSize, dpi);
        var halfThickness = thickness / 2;

        var parts = new List<FramePart>
        {
            probe,
            new FramePart(color, isHandle: false),
            new FramePart(color, isHandle: false),
            new FramePart(color, isHandle: false)
        };
        parts[0].Move(new RectInt32(bounds.X - halfThickness, bounds.Y - halfThickness, bounds.Width + thickness, thickness));
        parts[1].Move(new RectInt32(bounds.X - halfThickness, bounds.Y + bounds.Height - halfThickness, bounds.Width + thickness, thickness));
        parts[2].Move(new RectInt32(bounds.X - halfThickness, bounds.Y - halfThickness, thickness, bounds.Height + thickness));
        parts[3].Move(new RectInt32(bounds.X + bounds.Width - halfThickness, bounds.Y - halfThickness, thickness, bounds.Height + thickness));

        var centers = new[]
        {
            new PointInt32(bounds.X, bounds.Y),
            new PointInt32(bounds.X + bounds.Width / 2, bounds.Y),
            new PointInt32(bounds.X + bounds.Width, bounds.Y),
            new PointInt32(bounds.X, bounds.Y + bounds.Height / 2),
            new PointInt32(bounds.X + bounds.Width, bounds.Y + bounds.Height / 2),
            new PointInt32(bounds.X, bounds.Y + bounds.Height),
            new PointInt32(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height),
            new PointInt32(bounds.X + bounds.Width, bounds.Y + bounds.Height)
        };
        foreach (var center in centers)
        {
            var handle = new FramePart(color, isHandle: true);
            handle.Move(new RectInt32(
                center.X - handleSize / 2,
                center.Y - handleSize / 2,
                handleSize,
                handleSize));
            parts.Add(handle);
        }
        _parts = [.. parts];
    }

    internal void Show()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        foreach (var part in _parts) part.Show();
    }

    internal void SetColor(Color color)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        foreach (var part in _parts) part.SetColor(color);
    }

    public void Dispose()
    {
        if (_disposed) return;
        foreach (var part in _parts) part.Close();
        _disposed = true;
    }

    private sealed class FramePart
    {
        private readonly Window _window;
        private readonly Border _root;
        private readonly AppWindow _appWindow;
        private readonly bool _isHandle;

        internal FramePart(Color color, bool isHandle)
        {
            _isHandle = isHandle;
            _window = new Window { Title = "Tbox 捕获边框" };
            _root = new Border
            {
                Background = new SolidColorBrush(isHandle ? Colors.White : color),
                BorderBrush = new SolidColorBrush(isHandle ? color : Colors.Transparent),
                BorderThickness = isHandle ? new Thickness(1.5) : new Thickness(0),
                CornerRadius = isHandle ? new CornerRadius(1) : new CornerRadius(0),
                IsHitTestVisible = false
            };
            _window.Content = _root;
            var hwnd = WindowNative.GetWindowHandle(_window);
            NativeMethods.DisableWindowTransitions(hwnd);
            NativeMethods.MakeWindowClickThroughAndExcludedFromCapture(hwnd);
            _appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(hwnd));
            if (_appWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.SetBorderAndTitleBar(false, false);
                presenter.IsAlwaysOnTop = true;
                presenter.IsResizable = false;
                presenter.IsMaximizable = false;
                presenter.IsMinimizable = false;
            }
            _appWindow.IsShownInSwitchers = false;
        }

        internal IntPtr WindowHandle => WindowNative.GetWindowHandle(_window);

        internal void Move(RectInt32 bounds) => NativeMethods.MoveAndResizeWindow(WindowHandle, bounds);

        internal void Show() => _appWindow.Show(activateWindow: false);

        internal void SetColor(Color color)
        {
            if (_isHandle) _root.BorderBrush = new SolidColorBrush(color);
            else _root.Background = new SolidColorBrush(color);
        }

        internal void Close() => _window.Close();
    }
}
