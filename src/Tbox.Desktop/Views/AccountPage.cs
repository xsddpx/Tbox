using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Tbox.Core.Accounts;
using Tbox.Desktop.Accounts;
using Tbox.Desktop.Models;
using Tbox.Desktop.Platform;

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
        var remote = _account.RemoteAvailable;
        var hosts = _account.RemoteHosts.Select(host => host.DeviceId).ToHashSet();
        IEnumerable<CloudDevice> devices = _account.Devices;
        if (remote) devices = devices.OrderBy(device => device.Id != _account.LocalDevice);
        var showedOtherDevices = false;
        foreach (var device in devices)
        {
            var available = remote && hosts.Contains(device.Id);
            var online = device.LastSeen is { } seen && DateTimeOffset.UtcNow - seen < TimeSpan.FromMinutes(1);
            var platform = device.Platform + (available ? " · 可远程控制" : online ? " · 在线" : " · 离线");
            if (device.Id == _account.LocalDevice)
            {
                if (remote) _deviceRows.Children.Add(Ui.GroupHeader("此设备"));
                var state = _account.RemoteHostEnabled ? "已允许同账号设备远程控制" : "远程控制已关闭";
                _deviceRows.Children.Add(new SettingsLine(device.Name + "（本机）", remote ? state : platform,
                    remote ? LocalRemoteAction() : new Border()));
                continue;
            }
            if (remote && !showedOtherDevices)
            {
                _deviceRows.Children.Add(Ui.GroupHeader("其他设备"));
                showedOtherDevices = true;
            }
            var remove = Ui.Button("移除", () => _controller.Run(async () =>
            {
                var effect = remote ? " 将退出此账号并停止同步和远程控制。" : " 将退出此账号并停止同步。";
                if (await new ConfirmWindow("移除此设备？", "移除", device.Name + effect, destructive: true)
                    .ShowDialog<bool>(_owner())) await _account.RemoveDeviceAsync(device.Id);
            }));
            Control actions = remove;
            if (remote)
            {
                var connect = Ui.Primary("远程控制", () => _controller.Run(() => _account.ConnectRemoteHostAsync(device.Id)));
                connect.IsEnabled = available && !_account.Busy;
                actions = Ui.Row(connect, remove);
            }
            _deviceRows.Children.Add(new SettingsLine(device.Name, platform, actions));
        }
    }

    private Button LocalRemoteAction()
    {
        var action = _account.RemoteHostEnabled
            ? Ui.Button("关闭远程控制", () => _controller.Run(_account.DisableRemoteHostAsync))
            : Ui.Button("允许此设备被控", () => _controller.Run(async () =>
            {
                var detail = "登录同一 Tbox 账号且仍获授权的设备可以查看此电脑画面并操作键鼠。关闭授权会结束进行中的连接。";
                if (OperatingSystem.IsWindows() && WindowsRemoteHost.Available)
                    detail += " Windows 会要求管理员确认，以便重启后在登录界面接受连接。";
                if (await new ConfirmWindow("允许无人值守控制？", "允许远程控制", detail)
                    .ShowDialog<bool>(_owner())) await _account.EnableRemoteHostAsync();
            }));
        action.IsEnabled = !_account.Busy;
        return action;
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
