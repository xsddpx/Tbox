using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Tbox.Desktop.Accounts;
using Tbox.Desktop.Models;

namespace Tbox.Desktop.Views;

internal sealed class AccountPage : StackPanel
{
    private readonly DesktopController _controller;
    private readonly AccountCoordinator _account;
    private readonly Func<Window> _owner;
    private readonly TextBlock _status = Ui.Description("");
    private readonly CheckBox _preferences = new() { Content = "同步通用和工具偏好" };
    private readonly CheckBox _screenshots = new() { Content = "上传此设备之后保存或复制的截图成品" };
    private readonly StackPanel _signedOut = new() { Spacing = 8 };
    private readonly StackPanel _signedIn = new() { Spacing = 10 };
    private readonly StackPanel _deviceRows = new() { Spacing = 6 };
    private readonly StackPanel _captureRows = new() { Spacing = 6 };
    private readonly StackPanel _content = new() { Spacing = 9 };
    private readonly Button _signIn, _cancel, _signOut, _sync, _devices;
    private bool _refreshing, _showDevices;

    internal AccountPage(DesktopController controller, Func<Window> owner)
    {
        _controller = controller;
        _account = controller.Account;
        _owner = owner;
        Spacing = 12;
        _signIn = Ui.Primary("使用 GitHub 登录", () => _controller.Run(_account.SignInAsync));
        _cancel = Ui.Button("取消登录", _account.CancelSignIn);
        _signOut = Ui.Button("退出登录", () => _controller.Run(_account.SignOutAsync));
        _sync = Ui.Button("同步内容", () => Select(showDevices: false));
        _devices = Ui.Button("我的设备", () => Select(showDevices: true));
        _signedOut.Children.Add(Ui.Description("登录指定的 GitHub 账号后，此设备会自动出现在列表中。"));
        _signedOut.Children.Add(Ui.Row(_signIn, _cancel));
        _signedIn.Children.Add(Ui.Row(_signOut, Ui.Link("刷新", () => _controller.Run(_account.RefreshAsync))));
        _signedIn.Children.Add(Ui.Row(_sync, _devices));
        _signedIn.Children.Add(_content);
        Children.Add(_signedOut);
        Children.Add(_signedIn);
        Children.Add(_status);
        Select(false);
        _preferences.IsCheckedChanged += (_, _) =>
        {
            if (!_refreshing) _controller.Run(() => _account.SetPreferenceSyncAsync(_preferences.IsChecked == true));
        };
        _screenshots.IsCheckedChanged += async (_, _) =>
        {
            if (_refreshing) return;
            if (_screenshots.IsChecked == true &&
                !await new ConfirmWindow("同步此电脑的后续截图？", "开启同步",
                    "之后完成的选区成品会上传至配置的账号服务，其他同账号设备可以查看。").ShowDialog<bool>(_owner()))
            {
                Refresh();
                return;
            }
            _controller.Run(() => _account.SetCaptureSyncAsync(_screenshots.IsChecked == true));
        };
        _account.Changed += Refresh;
        Refresh();
    }

    private void Select(bool showDevices)
    {
        _showDevices = showDevices;
        _content.Children.Clear();
        if (showDevices)
        {
            _content.Children.Add(_deviceRows);
            _content.Children.Add(Ui.Description("在另一台电脑登录同一账号后，设备会自动显示。"));
        }
        else
        {
            _content.Children.Add(_preferences);
            _content.Children.Add(_screenshots);
            _content.Children.Add(Ui.GroupHeader("同步的截图"));
            _content.Children.Add(_captureRows);
        }
        Refresh();
    }

    internal void Refresh()
    {
        _refreshing = true;
        try
        {
            _signedIn.IsVisible = _account.SignedIn;
            _signedOut.IsVisible = !_account.SignedIn;
            _status.Text = !_account.SignedIn && !_account.CanSignIn ? "账号服务尚未配置。" : _account.Status;
            _signIn.IsEnabled = !_account.Busy && _account.CanSignIn;
            _signOut.IsEnabled = !_account.Busy;
            _cancel.IsVisible = _account.SigningIn;
            _preferences.IsEnabled = _screenshots.IsEnabled = !_account.Busy;
            _preferences.IsChecked = _controller.Settings.Cloud.SyncPreferences;
            _screenshots.IsChecked = _controller.Settings.Cloud.SyncCaptures;
            _sync.IsEnabled = _showDevices;
            _devices.IsEnabled = !_showDevices;
            if (_account.SignedIn)
            {
                if (_showDevices) RenderDevices();
                else RenderCaptures();
            }
        }
        finally { _refreshing = false; }
    }

    private void RenderDevices()
    {
        _deviceRows.Children.Clear();
        foreach (var device in _account.Devices)
        {
            var self = device.Id == _account.LocalDevice;
            var online = device.LastSeen is { } seen && DateTimeOffset.UtcNow - seen < TimeSpan.FromMinutes(1);
            var platform = device.Platform + (online ? " · 在线" : " · 离线");
            var label = self ? device.Name + "（本机）" : device.Name;
            Control action = self ? new Border() : Ui.Button("移除", () => _controller.Run(async () =>
            {
                if (await new ConfirmWindow("移除此设备？", "移除", device.Name + " 将退出此账号并停止同步。", destructive: true)
                    .ShowDialog<bool>(_owner())) await _account.RemoveDeviceAsync(device.Id);
            }));
            _deviceRows.Children.Add(new SettingsLine(label, platform, action));
        }
    }

    private void RenderCaptures()
    {
        _captureRows.Children.Clear();
        if (_account.Captures.Count == 0) _captureRows.Children.Add(Ui.Description("暂无同步的截图。"));
        foreach (var capture in _account.Captures)
        {
            var open = Ui.Button("查看", () => _controller.Run(() => _account.OpenCaptureAsync(capture.Id,
                bytes => _controller.ShowImageResult(new Picture(bytes), false))));
            var remove = Ui.Button("删除云端副本", () => _controller.Run(async () =>
            {
                if (await new ConfirmWindow("删除这张同步截图？", "删除", "本机原有历史不受影响。", destructive: true)
                    .ShowDialog<bool>(_owner())) await _account.DeleteCaptureAsync(capture.Id);
            }));
            _captureRows.Children.Add(new SettingsLine(capture.CreatedAt.ToLocalTime().ToString("g"), "选区成品", Ui.Row(open, remove)));
        }
    }
}
