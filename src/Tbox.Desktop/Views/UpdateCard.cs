using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Tbox.Desktop.Platform;
using Tbox.Desktop.Updates;

namespace Tbox.Desktop.Views;

// The About page banner: installed version, manual public update check, download progress and install.
internal sealed class UpdateCard : StackPanel
{
    private readonly DesktopController _controller;
    private readonly GitHubUpdateClient _client;
    private readonly Func<string, UpdateRelease, CancellationToken, Task<PreparedUpdate>> _prepareUpdate;
    private readonly TextBlock _title = new() { FontSize = DesignTokens.SectionTitle, FontWeight = FontWeight.SemiBold, LineHeight = 22 };
    private readonly TextBlock _detail = Ui.Description("公开下载，无需登录");
    private readonly ProgressBar _progress = new() { Minimum = 0, Maximum = 100, Height = 6, MinHeight = 6, MaxWidth = 360,
        CornerRadius = new CornerRadius(3), Margin = new Thickness(0, 8, 0, 6), IsVisible = false, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly Button _check;
    private readonly Button _notesToggle;
    private readonly Button _download;
    private readonly Button _install;
    private readonly Button _cancel;
    private readonly Border _notes;
    private readonly TextBlock _notesText = Ui.Description("");
    private readonly Bitmap _logo;
    private readonly string _directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Tbox", "updates", Guid.NewGuid().ToString("N"));
    private readonly Version _installed = UpdateReleasePolicy.Normalize(typeof(UpdateCard).Assembly.GetName().Version!);
    private CancellationTokenSource? _operation;
    private UpdateRelease? _release;
    private string? _file;
    private bool _closed;
    private bool _handedToInstaller;

    // Raised when a newer release is found or handled, so the sidebar can show its yellow dot.
    public event Action? AvailabilityChanged;
    public bool UpdateAvailable => _release is not null && !_handedToInstaller;

    public UpdateCard(DesktopController controller, GitHubUpdateClient? client = null,
        Func<string, UpdateRelease, CancellationToken, Task<PreparedUpdate>>? prepareUpdate = null)
    {
        _controller = controller; _client = client ?? new();
        _prepareUpdate = prepareUpdate ?? UpdateInstaller.PrepareAsync;
        _check = Ui.Button("检查更新", () => Run(CheckAsync));
        _notesToggle = Ui.Button("更新内容", () => { _notes!.IsVisible = !_notes.IsVisible; _notesToggle!.Content = _notes.IsVisible ? "收起内容" : "更新内容"; });
        _download = Ui.Primary("下载并安装", () => Run(DownloadAsync));
        _install = Ui.Primary(OperatingSystem.IsMacOS() ? "安装并重启" : "打开安装程序", () => Run(InstallAsync));
        _cancel = Ui.Button("取消", () => _operation?.Cancel());
        _title.Text = $"Tbox {_installed.ToString(3)}";

        using (var asset = AssetLoader.Open(new Uri("avares://Tbox.Desktop/Assets/StoreLogo.png"))) _logo = new Bitmap(asset);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 2, Children = { _title, _progress, _detail } };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center,
            Children = { _check, _notesToggle, _download, _install, _cancel } };
        var layout = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 16, Children =
        {
            new Image { Source = _logo, Width = 52, Height = 52, IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Center },
            Ui.Column(text, 1), Ui.Column(actions, 2)
        } };
        _notes = new Border { CornerRadius = new CornerRadius(12), Padding = new Thickness(14, 12), Background = Brush.Parse("#F7F6FB"),
            Margin = new Thickness(0, 10, 0, 0), IsVisible = false,
            Child = new ScrollViewer { Content = _notesText, MaxHeight = 180 } };
        Children.Add(Ui.Hero(layout, minHeight: 0));
        Children.Add(_notes);
        RefreshActions();
    }

    // Called when the main window closes for good (quit).
    public void Release()
    {
        _closed = true; _operation?.Cancel(); _logo.Dispose();
        if (_operation is null) { _client.Dispose(); Cleanup(); }
    }

    private async void Run(Func<CancellationToken, Task> action)
    {
        if (_operation is not null || _closed) return;
        using var stop = new CancellationTokenSource(); _operation = stop; RefreshActions();
        try { await action(stop.Token); }
        catch (OperationCanceledException) { Detail(stop.IsCancellationRequested ? "已取消，可以重新尝试。" : "连接超时，请稍后重试。"); }
        catch (Exception ex)
        {
            AppLog.Write("Update: " + ex);
            Detail(ex is HttpRequestException ? "无法连接更新服务，请检查网络后重试。" : ex.Message);
        }
        finally
        {
            _operation = null; _progress.IsVisible = false;
            if (_file is null) _title.Text = $"Tbox {_installed.ToString(3)}";
            if (_closed) { _client.Dispose(); Cleanup(); } else RefreshActions();
        }
    }

    private async Task CheckAsync(CancellationToken token)
    {
        Cleanup(); _file = null; _release = null; _notesText.Text = ""; _notes.IsVisible = false;
        Detail("正在检查新版本…");
        _progress.IsIndeterminate = true; _progress.IsVisible = true;
        _release = await _client.CheckAsync(_installed, UpdateInstaller.Runtime, token);
        AvailabilityChanged?.Invoke();
        if (_release is null) { Detail($"已是最新版本 · 今天 {DateTime.Now:HH:mm} 检查"); return; }
        _detail.Inlines = new InlineCollection
        {
            new Run("发现新版本 "), new Run(_release.Version.ToString(3)) { Foreground = Brush.Parse(DesignTokens.Accent), FontWeight = FontWeight.SemiBold },
            new Run(" · 公开下载，无需登录")
        };
        _notesText.Text = string.IsNullOrWhiteSpace(_release.Notes) ? "这个版本没有填写更新说明。" : _release.Notes;
    }

    private async Task DownloadAsync(CancellationToken token)
    {
        var release = _release ?? throw new InvalidOperationException("请先检查更新。");
        _title.Text = $"正在下载 {release.Version.ToString(3)}";
        _progress.IsIndeterminate = false; _progress.Value = 0; _progress.IsVisible = true;
        Detail(Megabytes(0, release.Size));
        _file = await _client.DownloadAsync(release, _directory, new Progress<double>(p =>
        {
            if (_closed) return;
            _progress.Value = p * 100; Detail(Megabytes(p, release.Size));
        }), token);
        _title.Text = $"{release.Version.ToString(3)} 已下载";
        Detail(OperatingSystem.IsMacOS() ? "完整性校验通过，安装后 Tbox 会自动重启。" : "完整性校验通过，可以打开安装程序。");
    }

    private static string Megabytes(double fraction, long size) =>
        $"{fraction * size / 1048576:0.0} MB / {size / 1048576.0:0.0} MB";

    private async Task InstallAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Detail("正在验证安装包与应用身份…"); _progress.IsVisible = true; _progress.IsIndeterminate = true;
        using var prepared = await _prepareUpdate(_file!, _release!, token);
        token.ThrowIfCancellationRequested();
        Detail("正在结束当前任务并启动安装程序…"); _cancel.IsVisible = false;
        await _controller.InstallUpdateAsync(() => { prepared.Launch(); _handedToInstaller = true; });
    }

    private void Detail(string text) { _detail.Inlines = null; _detail.Text = text; }

    private void RefreshActions()
    {
        var busy = _operation is not null;
        _cancel.IsVisible = busy;
        _check.IsVisible = !busy && _release is null; _check.IsEnabled = !_handedToInstaller;
        _notesToggle.IsVisible = !busy && _release is not null && _file is null;
        _download.IsVisible = !busy && _release is not null && _file is null;
        _install.IsVisible = !busy && _file is not null;
    }

    private void Cleanup()
    {
        if (_handedToInstaller) return;
        try { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
