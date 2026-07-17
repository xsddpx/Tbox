using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Tbox.Core;
using Windows.Foundation;
using Windows.Graphics;
using Windows.System;
using WinRT.Interop;

namespace Tbox.App;

internal sealed class PinnedAnnotationToolbarWindow : Window
{
    private const int Gap = 8;
    private readonly AnnotationToolbar _toolbar;
    private readonly AppWindow _appWindow;
    private readonly IntPtr _windowHandle;
    private readonly IntPtr _ownerHandle;

    internal PinnedAnnotationToolbarWindow(
        AnnotationToolbar toolbar,
        IntPtr ownerHandle,
        Action finishEditing)
    {
        _toolbar = toolbar;
        _ownerHandle = ownerHandle;
        Title = "Tbox 贴图标注工具条";
        Content = toolbar.View;

        _windowHandle = WindowNative.GetWindowHandle(this);
        NativeMethods.DisableWindowTransitions(_windowHandle);
        NativeMethods.SetWindowOwner(_windowHandle, ownerHandle);
        _appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(_windowHandle));
        if (_appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsAlwaysOnTop = true;
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
        }
        _appWindow.IsShownInSwitchers = false;

        var escape = new KeyboardAccelerator { Key = VirtualKey.Escape };
        escape.Invoked += (_, args) =>
        {
            args.Handled = true;
            finishEditing();
        };
        toolbar.View.KeyboardAccelerators.Add(escape);
    }

    internal bool IsVisible { get; private set; }

    internal void ShowAdjacent(RectInt32 imageBounds)
    {
        _toolbar.Refresh();
        PositionAdjacent(imageBounds);
        _appWindow.Show(activateWindow: false);
        IsVisible = true;
    }

    internal void Reposition(RectInt32 imageBounds)
    {
        if (IsVisible) PositionAdjacent(imageBounds);
    }

    internal void Hide()
    {
        if (!IsVisible) return;
        _appWindow.Hide();
        IsVisible = false;
    }

    private void PositionAdjacent(RectInt32 imageBounds)
    {
        var center = new PointInt32(
            imageBounds.X + (imageBounds.Width / 2),
            imageBounds.Y + (imageBounds.Height / 2));
        var work = DisplayArea.GetFromPoint(center, DisplayAreaFallback.Primary).WorkArea;
        _toolbar.View.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var desired = _toolbar.View.DesiredSize;
        var dpi = Math.Max(96u, NativeMethods.GetDpiForWindow(_ownerHandle));
        var width = WindowDpiScalePolicy.LogicalToPhysicalPixels(Math.Max(120, desired.Width), dpi);
        var height = WindowDpiScalePolicy.LogicalToPhysicalPixels(Math.Max(40, desired.Height), dpi);
        var gap = WindowDpiScalePolicy.LogicalToPhysicalPixels(Gap, dpi);
        var placed = PinToolbarPlacementPolicy.PlaceAdjacent(
            new PinWindowBounds(imageBounds.X, imageBounds.Y, imageBounds.Width, imageBounds.Height),
            new PinWindowBounds(work.X, work.Y, work.Width, work.Height),
            width,
            height,
            gap);
        NativeMethods.MoveAndResizeWindow(
            _windowHandle,
            new RectInt32(placed.X, placed.Y, placed.Width, placed.Height));
    }
}
