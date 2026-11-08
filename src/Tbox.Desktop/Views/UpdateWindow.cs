using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Tbox.Desktop.Updates;

namespace Tbox.Desktop.Views;

internal sealed class UpdateWindow : Window
{
    private readonly DesktopController _controller;
    private readonly GitHubUpdateClient _client;
    private readonly TextBlock _status = Ui.Text("点击检查更新，获取最新正式版本。", 15);
    private readonly TextBlock _notes = Ui.Caption("");
    private readonly ProgressBar _progress = new() { Minimum = 0, Maximum = 100, Height = 5, IsVisible = false };
    private readonly Button _check;
    private readonly Button _download;
    private readonly Button _install;
    private readonly Button _cancel;
    private readonly string _directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Tbox", "updates", Guid.NewGuid().ToString("N"));
    private readonly Version _installed = UpdateReleasePolicy.Normalize(typeof(UpdateWindow).Assembly.GetName().Version!);
    private CancellationTokenSource? _operation;
    private UpdateRelease? _release;
    private string? _file;
    private bool _closed;
    private bool _handedToInstaller;

    public UpdateWindow(DesktopController controller, GitHubUpdateClient? client = null)
    {
        _controller = controller; _client = client ?? new();
        Ui.Configure(this, "Tbox · 软件更新", 520, 440); MinWidth = 400; MinHeight = 340;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _check = Ui.Primary("检查更新", () => Run(CheckAsync));
        _download = Ui.Primary("下载更新", () => Run(DownloadAsync));
        _install = Ui.Primary(OperatingSystem.IsMacOS() ? "安装并重启" : "打开安装程序", () => Run(InstallAsync));
        _cancel = Ui.Button("取消", () => _operation?.Cancel());
        var header = Ui.Header("软件更新", $"当前版本 {_installed.ToString(3)} · 公开下载，无需登录", null, out var brand);
        Closed += (_, _) => brand.Dispose();
        var status = Ui.Card(new StackPanel { Spacing = 12, Children = { _status, _progress } }); status.Padding = new Thickness(16);
        var actions = Ui.Row(_cancel, _check, _download, _install); actions.HorizontalAlignment = HorizontalAlignment.Right;
        var notes = new ScrollViewer { Content = _notes, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        var layout = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto"), RowSpacing = 14, Margin = new Thickness(20, 14, 20, 16),
            Children = { header, status, notes, actions } };
        Grid.SetRow(status, 1); Grid.SetRow(notes, 2); Grid.SetRow(actions, 3);
        Content = layout;
        Closed += (_, _) =>
        {
            _closed = true; _operation?.Cancel();
            if (_operation is null) { _client.Dispose(); Cleanup(); }
        };
        RefreshActions();
    }

    private async void Run(Func<CancellationToken, Task> action)
    {
        if (_operation is not null || _closed) return;
        using var stop = new CancellationTokenSource(); _operation = stop; RefreshActions();
        try { await action(stop.Token); }
        catch (OperationCanceledException) { _status.Text = stop.IsCancellationRequested ? "已取消，可以重新尝试。" : "连接超时，请稍后重试。"; }
        catch (Exception ex)
        {
            Tbox.App.AppLog.Write("Update: " + ex);
            _status.Text = ex is HttpRequestException ? "无法连接更新服务，请检查网络后重试。" : ex.Message;
        }
        finally
        {
            _operation = null; _progress.IsVisible = false;
            if (_closed) { _client.Dispose(); Cleanup(); } else RefreshActions();
        }
    }

    private async Task CheckAsync(CancellationToken token)
    {
        Cleanup(); _file = null; _release = null; _notes.Text = ""; _status.Text = "正在检查新版本…";
        _progress.IsIndeterminate = true; _progress.IsVisible = true;
        _release = await _client.CheckAsync(_installed, UpdateInstaller.Runtime, token);
        _status.Text = _release is null ? "当前没有可用的新正式版本。" : $"发现新版本 {_release.Version.ToString(3)}";
        _notes.Text = _release?.Notes;
    }

    private async Task DownloadAsync(CancellationToken token)
    {
        var release = _release ?? throw new InvalidOperationException("请先检查更新。");
        _progress.IsIndeterminate = false; _progress.Value = 0; _progress.IsVisible = true;
        _status.Text = $"正在下载 {release.Version.ToString(3)}…";
        _file = await _client.DownloadAsync(release, _directory, new Progress<double>(p => { if (!_closed) _progress.Value = p * 100; }), token);
        _status.Text = "下载完成，完整性校验通过。";
    }

    private async Task InstallAsync(CancellationToken token)
    {
        if (_controller.HasOpenWorkForUpdate) throw new InvalidOperationException("请先完成截图、录屏并关闭贴图及结果窗口，再安装更新。");
        var message = OperatingSystem.IsMacOS() ? "即将安装新版本并重启 Tbox。已有设置和缓存将保留。" : "即将打开 Windows 安装程序。请确认升级，完成后重新打开 Tbox。";
        if (!await new ConfirmWindow(message, "继续安装").ShowDialog<bool>(this)) return;
        token.ThrowIfCancellationRequested();
        _status.Text = "正在验证安装包与应用身份…"; _progress.IsVisible = true; _progress.IsIndeterminate = true;
        using var prepared = await UpdateInstaller.PrepareAsync(_file!, _release!, token);
        token.ThrowIfCancellationRequested();
        if (_controller.HasOpenWorkForUpdate) throw new InvalidOperationException("存在未结束的任务，请完成后重试。");
        prepared.Launch(); _handedToInstaller = true;
        if (prepared.RequiresExit) await _controller.QuitAsync();
        else { _status.Text = "安装程序已打开。完成升级后，请重新打开 Tbox。"; _file = null; _release = null; }
    }

    private void RefreshActions()
    {
        var busy = _operation is not null;
        _check.IsEnabled = !busy && !_handedToInstaller;
        _download.IsVisible = _release is not null && _file is null; _download.IsEnabled = !busy;
        _install.IsVisible = _file is not null; _install.IsEnabled = !busy;
        _cancel.IsVisible = busy;
    }
    private void Cleanup()
    {
        if (_handedToInstaller) return;
        try { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
