using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Tbox.Core;

namespace Tbox.Desktop.Views;

// A settings-row control for one global shortcut: key caps when idle; click to record a new combination.
// While recording, Tbox hotkeys are released so they can be checked for duplicates. Esc cancels.
internal sealed class ShortcutRecorder : Button
{
    private readonly DesktopController _controller;
    private readonly HotkeyAction _action;
    private readonly StackPanel _caps = new() { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _prompt = new() { FontSize = DesignTokens.Caption, VerticalAlignment = VerticalAlignment.Center, Foreground = Brush.Parse(DesignTokens.Accent) };
    private bool _saving;

    public bool IsRecording { get; private set; }
    // Raised when recording starts or stops, so the row can show "按 Esc 取消".
    public event Action? RecordingChanged;
    // The reason a recorded combination was rejected, or null after a successful change.
    public event Action<string?>? Finished;

    public ShortcutRecorder(DesktopController controller, HotkeyAction action)
    {
        _controller = controller; _action = action;
        MinHeight = DesignTokens.ControlHeight; MinWidth = 96; Padding = new Thickness(10, 0);
        CornerRadius = new CornerRadius(DesignTokens.ControlRadius); HorizontalContentAlignment = HorizontalAlignment.Center;
        VerticalContentAlignment = VerticalAlignment.Center;
        var name = ShortcutPolicy.Name(action);
        AutomationProperties.SetName(this, $"修改{name}快捷键");
        ToolTip.SetTip(this, "单击后按下新的组合键 · Esc 取消");
        Content = new Panel { Children = { _caps, _prompt } };
        Click += (_, _) => { if (IsRecording) Stop(); else Start(); };
        LostFocus += (_, _) => { if (IsRecording && !_saving) Stop(); };
        Refresh();
    }

    public void Refresh()
    {
        _caps.Children.Clear();
        foreach (var cap in ShortcutLabels.Caps(_action)) _caps.Children.Add(Ui.Key(cap));
        _caps.IsVisible = !IsRecording; _prompt.IsVisible = IsRecording;
        _prompt.Text = "按下组合键…";
        // Idle uses the theme's secondary-button outline; recording shows an amethyst one.
        if (IsRecording) { BorderBrush = Brush.Parse(DesignTokens.Accent); BorderThickness = new Thickness(1.5); }
        else { ClearValue(BorderBrushProperty); ClearValue(BorderThicknessProperty); }
    }

    public void Start()
    {
        if (_saving || IsRecording || !_controller.TryPauseHotkeys()) return;
        IsRecording = true;
        Focus(); Refresh(); RecordingChanged?.Invoke();
    }

    public void Stop()
    {
        if (_saving || !IsRecording) return;
        IsRecording = false; _controller.ResumeHotkeys(); Refresh(); RecordingChanged?.Invoke();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (!IsRecording) { base.OnKeyDown(e); return; }
        e.Handled = true;
        if (_saving) return;
        if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None) { Stop(); return; }
        if (e.Key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin) return;
        if (KeyName(e.Key) is not { } key)
        {
            Stop();
            Finished?.Invoke("不支持这个按键，请使用字母、主键区数字或 F1–F12。");
            return;
        }
        var shortcut = new Shortcut((e.KeyModifiers & KeyModifiers.Control) != 0, (e.KeyModifiers & KeyModifiers.Alt) != 0,
            (e.KeyModifiers & KeyModifiers.Shift) != 0, (e.KeyModifiers & KeyModifiers.Meta) != 0, key);
        _ = SaveAsync(shortcut);
    }

    private async Task SaveAsync(Shortcut? shortcut)
    {
        if (_saving) return;
        _saving = true;
        string? error;
        try { error = await _controller.SetShortcutAsync(_action, shortcut); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { error = "无法保存设置：" + exception.Message; }
        catch (OperationCanceledException) { return; }
        finally
        {
            _saving = false;
            if (IsRecording) Stop();
        }
        Refresh();
        Finished?.Invoke(error);
    }

    public Task ResetAsync() => SaveAsync(null);
    // Keys a global shortcut may use; null for modifiers and anything the platforms cannot register.
    internal static string? KeyName(Key key) => key switch
    {
        >= Key.A and <= Key.Z => ((char)('A' + (key - Key.A))).ToString(),
        >= Key.D0 and <= Key.D9 => ((char)('0' + (key - Key.D0))).ToString(),
        >= Key.F1 and <= Key.F12 => $"F{1 + (key - Key.F1)}",
        Key.PrintScreen => "PrintScreen",
        _ => null
    };
}

// One shortcut setting: name, what it does (or why the last change failed), "恢复默认" and the recorder.
internal sealed class ShortcutRow : SettingsLine
{
    private readonly DesktopController _controller;
    private readonly HotkeyAction _action;
    private readonly Button _reset;
    private string? _error;
    public ShortcutRecorder Recorder { get; }

    public ShortcutRow(DesktopController controller, HotkeyAction action) : base(ShortcutPolicy.Name(action) + "快捷键", "")
    {
        _controller = controller; _action = action;
        Recorder = new ShortcutRecorder(controller, action);
        Recorder.Finished += error => { _error = error; Refresh(); };
        Recorder.RecordingChanged += () => { if (Recorder.IsRecording) _error = null; Refresh(); };
        _reset = Ui.Link("恢复默认", () => _ = Recorder.ResetAsync(), $"恢复{ShortcutPolicy.Name(action)}默认快捷键", accent: false);
        Trailing = Ui.Row(_reset, Recorder);
        Refresh();
    }

    public void Refresh()
    {
        Recorder.Refresh();
        var enabled = _action == HotkeyAction.Folder ? _controller.Settings.FolderJumpEnabled : _controller.Settings.ScreenPasteEnabled;
        _reset.IsVisible = !_controller.IsDefaultShortcut(_action) && !Recorder.IsRecording;
        if (Recorder.IsRecording) { SetDescription("按 Esc 取消"); return; }
        if (_error is not null) { SetDescription(_error, error: true); return; }
        if (enabled && _controller.HasHotkeyConflictFor(_action)) { SetDescription("被其他应用占用，请换一个组合", error: true); return; }
        SetDescription(_action switch
        {
            HotkeyAction.Capture => "框选区域后标注、贴图或识别文字",
            HotkeyAction.Pin => "把剪贴板里的图片或文字贴到屏幕",
            HotkeyAction.Toggle => "隐藏或重新显示所有贴图",
            _ => "只在打开 / 保存对话框里生效"
        });
    }
}
