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

internal sealed class LongCapturePreviewWindow : Window, IDisposable
{
    private readonly RectInt32 _region;
    private readonly Border _root;
    private readonly Grid _imageHost;
    private readonly Image _image;
    private readonly Border _viewportMarker;
    private readonly TextBlock _directionText;
    private readonly AppWindow _appWindow;
    private readonly IntPtr _windowHandle;
    private int _imageWidth;
    private int _imageHeight;
    private int _viewportTop;
    private int _viewportHeight;
    private bool _disposed;

    internal LongCapturePreviewWindow(RectInt32 region)
    {
        _region = region;
        Title = "Tbox 长截图实时预览";
        _image = new Image
        {
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        _viewportMarker = new Border
        {
            BorderBrush = TboxUi.Brush(TboxUi.SuccessColor),
            BorderThickness = new Thickness(2),
            Background = TboxUi.Brush(Color.FromArgb(32, 52, 199, 89)),
            IsHitTestVisible = false,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top
        };
        _imageHost = new Grid
        {
            Background = TboxUi.Brush(Color.FromArgb(255, 24, 24, 24)),
            Margin = new Thickness(4)
        };
        _imageHost.Children.Add(_image);
        _imageHost.Children.Add(_viewportMarker);
        _imageHost.SizeChanged += (_, _) => UpdateViewportMarker();

        _directionText = new TextBlock
        {
            Text = "等待手动滚动",
            Foreground = TboxUi.Brush(Color.FromArgb(255, 224, 224, 224)),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
            FontSize = TboxUi.CaptionFontSize
        };
        var header = new Grid
        {
            Height = TboxUi.StandardControlHeight,
            Padding = new Thickness(8, 0, 8, 0),
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }
            }
        };
        header.Children.Add(new TextBlock
        {
            Text = "实时预览",
            Foreground = TboxUi.Brush(Colors.White),
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            FontSize = TboxUi.BodyFontSize,
            VerticalAlignment = VerticalAlignment.Center
        });
        Grid.SetColumn(_directionText, 1);
        header.Children.Add(_directionText);

        var content = new Grid
        {
            RowDefinitions =
            {
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }
            }
        };
        content.Children.Add(header);
        Grid.SetRow(_imageHost, 1);
        content.Children.Add(_imageHost);
        _root = TboxUi.CreateTaskSurface(content);
        _root.Width = 236;
        _root.Height = 520;
        Content = _root;

        _windowHandle = WindowNative.GetWindowHandle(this);
        NativeMethods.DisableWindowTransitions(_windowHandle);
        NativeMethods.MakeWindowClickThroughAndExcludedFromCapture(_windowHandle);
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
        CaptureModeWindowPlacement.PrepareOnTargetDisplay(_windowHandle, region);
    }

    internal void Show()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _appWindow.Show(activateWindow: false);
        CaptureModeWindowPlacement.PositionSidePreview(_windowHandle, _root, _region);
    }

    internal async Task UpdateImageAsync(CapturedScreen screen, int viewportTop, int viewportHeight, ScrollCaptureDirection direction)
    {
        if (_disposed) return;
        var source = await screen.CreateBitmapSourceAsync(maximumDecodeDimension: 4096);
        if (_disposed) return;
        _image.Source = source;
        _imageWidth = screen.Width;
        _imageHeight = screen.Height;
        UpdateViewport(viewportTop, viewportHeight, screen.Height, direction);
    }

    internal void UpdateViewport(int viewportTop, int viewportHeight, int imageHeight, ScrollCaptureDirection direction)
    {
        if (_disposed) return;
        _viewportTop = Math.Max(0, viewportTop);
        _viewportHeight = Math.Max(1, viewportHeight);
        _imageHeight = Math.Max(1, imageHeight);
        _directionText.Text = direction switch
        {
            ScrollCaptureDirection.Up => "↑ 向上滚动",
            ScrollCaptureDirection.Down => "↓ 向下滚动",
            _ => "等待手动滚动"
        };
        UpdateViewportMarker();
    }

    private void UpdateViewportMarker()
    {
        if (_imageWidth <= 0 || _imageHeight <= 0 || _imageHost.ActualWidth <= 0 || _imageHost.ActualHeight <= 0)
        {
            _viewportMarker.Visibility = Visibility.Collapsed;
            return;
        }

        var scale = Math.Min(_imageHost.ActualWidth / _imageWidth, _imageHost.ActualHeight / _imageHeight);
        var renderedWidth = _imageWidth * scale;
        var renderedHeight = _imageHeight * scale;
        var left = (_imageHost.ActualWidth - renderedWidth) / 2;
        var top = (_imageHost.ActualHeight - renderedHeight) / 2;
        var markerTop = top + Math.Min(_viewportTop, Math.Max(0, _imageHeight - 1)) * scale;
        var markerHeight = Math.Min(renderedHeight, Math.Max(4, _viewportHeight * scale));
        if (markerTop + markerHeight > top + renderedHeight)
        {
            markerTop = Math.Max(top, top + renderedHeight - markerHeight);
        }
        _viewportMarker.Width = renderedWidth;
        _viewportMarker.Height = markerHeight;
        _viewportMarker.Margin = new Thickness(left, markerTop, 0, 0);
        _viewportMarker.Visibility = Visibility.Visible;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _appWindow.Hide();
        Close();
    }
}
