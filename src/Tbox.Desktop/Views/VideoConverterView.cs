using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Tbox.Desktop.Models;
using Tbox.Desktop.Platform;


namespace Tbox.Desktop.Views;

internal sealed class VideoConverterView : UserControl
{
    private readonly DesktopController _controller;
    private readonly Func<Task> _savedRecording;
    private readonly ContentControl _body = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
    private readonly TextBlock _status = Ui.Description("");
    private readonly Button? _restart;
    private readonly Button _choose;
    private readonly Control _empty;
    private readonly Rectangle _dropSurface;
    private CancellationTokenSource? _importCancellation;
    private Task _importTask = Task.CompletedTask;
    private bool _busy, _closing;
    private bool _reading;
    internal VideoConversionEditor? Editor { get; private set; }

    internal VideoConverterView(DesktopController controller, Func<Task>? savedRecording = null, Func<Task>? restartRecording = null)
    {
        _controller = controller;
        _savedRecording = savedRecording ?? (() => Task.CompletedTask);
        _choose = Ui.Button("选择文件…", () => controller.Run(PickAsync));
        _choose.HorizontalAlignment = HorizontalAlignment.Center;
        var title = Ui.Text("视频转换", 25); title.FontWeight = FontWeight.SemiBold; title.VerticalAlignment = VerticalAlignment.Center;
        var header = new Grid { ColumnDefinitions = new("*,Auto"), MinHeight = 34,
            Margin = new Thickness(0, 0, 0, 13), Children = { Ui.WindowDragRegion(title) } };
        if (restartRecording is not null)
        {
            _restart = Ui.Button("重新录屏", () => controller.Run(restartRecording));
            _restart.Content = Ui.Row(new LineIcon(IconKind.Record) { Width = 16, Height = 16 }, new TextBlock { Text = "重新录屏" });
            header.Children.Add(Ui.Column(_restart, 1));
        }
        var options = Ui.Text("导出设置", 16); options.FontWeight = FontWeight.SemiBold;
        var save = Ui.Primary("保存视频…", () => { }); save.IsEnabled = false;
        save.HorizontalAlignment = HorizontalAlignment.Stretch; save.HorizontalContentAlignment = HorizontalAlignment.Center;
        save.Resources["ButtonBackgroundDisabled"] = Brush.Parse("#DBE3EC");
        save.Resources["ButtonForegroundDisabled"] = Brush.Parse("#57667A");
        save.Classes.Remove("accent");
        _dropSurface = new Rectangle { Fill = Brush.Parse("#F7FAFF"), Stroke = Brush.Parse("#AEBED0"), StrokeThickness = 1,
            StrokeDashArray = [5, 4], RadiusX = 9, RadiusY = 9, IsHitTestVisible = false };
        var emptyTitle = Ui.Text("拖入视频或动图", 18); emptyTitle.FontWeight = FontWeight.SemiBold;
        var dropContent = new StackPanel { Spacing = 8, Margin = new Thickness(9),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Children =
            { Ui.IconChip(IconKind.Film, ToolTone.Grey, 50, 25), emptyTitle, new TextBlock { Text = "支持 MP4、MOV、WebM、GIF、WebP 等", TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center, Foreground = Brush.Parse("#5F6270"), FontSize = 12 }, _choose } };
        foreach (var child in dropContent.Children) child.HorizontalAlignment = HorizontalAlignment.Center;
        var formats = Ui.Segmented(["MP4", "GIF", "WebP", "宫格图"], 0, _ => { }, out _, out var enableFormats, equalWidth: true);
        enableFormats(false);
        var rate = new ComboBox { ItemsSource = new[] { "30 FPS" }, SelectedIndex = 0, IsEnabled = false, MinWidth = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
        var size = new ComboBox { ItemsSource = new[] { "100%" }, SelectedIndex = 0, IsEnabled = false, MinWidth = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
        var emptySettings = new DockPanel { LastChildFill = true, Children =
        {
            new StackPanel { Spacing = 5, Children = { Ui.Description("等待导入素材"), save } },
            new StackPanel { Spacing = 8, Children = { options, Ui.Description("格式"), formats, Setting("帧率", rate), Setting("尺寸", size) } }
        } };
        DockPanel.SetDock(emptySettings.Children[0], Dock.Bottom);
        _empty = Workspace(new Border { Padding = new Thickness(10), Background = Brushes.White,
            BorderBrush = Brush.Parse("#DCE4EC"), BorderThickness = new Thickness(1), CornerRadius = new(11),
            Child = new Grid { Children = { _dropSurface, dropContent } } },
            new Border { Background = Brush.Parse("#F4F7FA"), Padding = new Thickness(13), CornerRadius = new(11),
                BorderBrush = Brush.Parse("#DCE4EC"), BorderThickness = new Thickness(1), Child = emptySettings });
        _body.Content = _empty;
        _status.IsVisible = false;
        Grid.SetRow(_body, 1); Grid.SetRow(_status, 2);
        var layout = new Grid { RowDefinitions = new("Auto,*,Auto"), Children = { header, _body, _status } };
        Content = layout;
        Loaded += (_, _) => Editor?.Start();
        DragDrop.SetAllowDrop(layout, true);
        layout.AddHandler(DragDrop.DragOverEvent, OnDragOver);
        layout.AddHandler(DragDrop.DragLeaveEvent, (_, _) => Highlight(false));
        layout.AddHandler(DragDrop.DropEvent, OnDrop);
    }

#pragma warning disable CS0618 // Avalonia 11's routed drag event still exposes IDataObject.
    private void OnDragOver(object? sender, DragEventArgs args)
    {
        var canDrop = !_busy && !_closing && args.Data.Contains(DataFormats.Files);
        args.DragEffects = canDrop ? DragDropEffects.Copy : DragDropEffects.None;
        Highlight(canDrop);
        args.Handled = true;
    }

    private void OnDrop(object? sender, DragEventArgs args)
    {
        Highlight(false);
        args.Handled = true;
        if (!_busy && !_closing && args.Data.GetFiles()?.FirstOrDefault()?.TryGetLocalPath() is { } path)
            _controller.Run(() => ImportAsync(path));
    }
#pragma warning restore CS0618

    internal static Grid Workspace(Control media, Control export) => new()
    {
        ColumnDefinitions =
        [
            new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
            new ColumnDefinition { Width = new GridLength(.61, GridUnitType.Star), MinWidth = 206, MaxWidth = 320 }
        ],
        ColumnSpacing = 12,
        Children = { media, Ui.Column(export, 1) }
    };

    private static Grid Setting(string name, Control control)
    {
        var label = Ui.Description(name);
        label.VerticalAlignment = VerticalAlignment.Center;
        return new Grid { ColumnDefinitions = new("*,1.4*"), ColumnSpacing = 5, Children = { label, Ui.Column(control, 1) } };
    }

    private void Highlight(bool on) => _dropSurface.Stroke = Brush.Parse(on ? "#41678F" : "#AEBED0");

    private async Task PickAsync()
    {
        if (_busy || _closing || TopLevel.GetTopLevel(this) is not { } owner) return;
        SetBusy(true);
        IReadOnlyList<IStorageFile> files;
        try
        {
            files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "导入视频或动图", AllowMultiple = false, FileTypeFilter = [VideoConversionSource.FileType]
            });
        }
        finally { SetBusy(false); }
        if (files.FirstOrDefault() is not { } file) return;
        if (file.TryGetLocalPath() is { } path) await ImportAsync(path);
        else SetStatus("请选择本地文件。");
    }

    internal Task ImportAsync(string path)
    {
        if (_busy || _closing) return Task.CompletedTask;
        _importTask = ImportCoreAsync(path);
        return _importTask;
    }

    private async Task ImportCoreAsync(string path)
    {
        SetBusy(true);
        _reading = true;
        if (Editor is { } current) current.IsEnabled = false;
        SetStatus("正在读取素材…");
        using var cancellation = _importCancellation = new CancellationTokenSource();
        RecordingWorkspace? workspace = null;
        try
        {
            workspace = new RecordingWorkspace();
            var source = await VideoConversionSource.OpenAsync(path, workspace, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (!await RemoveAsync()) { SetStatus(""); return; }
            if (_closing) return;
            SetSource(workspace, source);
            workspace = null;
            SetStatus("");
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            AppLog.Write("视频转换导入：" + error.Message);
            SetStatus(error is InvalidDataException ? error.Message : "无法读取该素材。请检查文件格式或尝试其他文件。");
        }
        finally
        {
            workspace?.Dispose();
            _importCancellation = null;
            _reading = false;
            if (Editor is { } active) active.IsEnabled = true;
            SetBusy(false);
        }
    }

    internal void SetSource(RecordingWorkspace workspace, VideoConversionSource source)
    {
        Editor = new VideoConversionEditor(_controller, workspace, source, () => _controller.Run(async () =>
        {
            if (!_busy) await RemoveByUserAsync();
        }), _savedRecording);
        Editor.BusyChanged += SetBusy;
        _body.Content = Editor;
        if (this.IsLoaded) Editor.Start();
    }

    internal void Start() => Editor?.Start();
    internal void Pause() => Editor?.Pause();

    private async Task RemoveByUserAsync()
    {
        SetBusy(true);
        try { if (await RemoveAsync()) SetStatus(""); }
        finally { SetBusy(false); }
    }

    private async Task<bool> RemoveAsync()
    {
        if (Editor is not { } editor) return true;
        if (TopLevel.GetTopLevel(this) is Window owner && !await editor.ConfirmDiscardAsync(owner, false)) return false;
        await editor.ReleaseAsync(() => { _body.Content = _empty; Editor = null; });
        return true;
    }

    internal async Task<bool> RequestCloseAsync(Window owner, bool discard, Action detach)
    {
        if (Editor is { } existing && !await existing.ConfirmDiscardAsync(owner, discard)) return false;
        _closing = true;
        _importCancellation?.Cancel();
        await _importTask;
        if (Editor is { } editor) await editor.ReleaseAsync(() => { detach(); Editor = null; });
        else detach();
        return true;
    }

    private void SetStatus(string status) { _status.Text = status; _status.IsVisible = status.Length > 0; }
    private void SetBusy(bool busy)
    {
        _busy = busy || _reading;
        _choose.IsEnabled = !_busy && !_closing;
        if (_restart is not null) _restart.IsEnabled = _choose.IsEnabled;
    }
}
