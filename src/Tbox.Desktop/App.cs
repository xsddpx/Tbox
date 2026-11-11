using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Tbox.Desktop.Platform;
using Tbox.Desktop.Views;

namespace Tbox.Desktop;

public sealed class App : Application
{
    public App() => Name = "Tbox";
    internal static SingleInstance? Instance { get; set; }
    private DesktopController? _controller;
    private TrayIcon? _tray;
    private MacStatusItem? _statusItem;
    public override void Initialize()
    {
        Styles.Add(DesignTokens.CreateTheme());
        // UI 3.0 is light only; task surfaces request their own dark variant.
        RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Light;
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime lifetime)
        {
            IPlatformServices platform;
#if WINDOWS
            platform = new WindowsPlatformServices();
#else
            platform = OperatingSystem.IsMacOS() ? new MacPlatformServices()
                : throw new PlatformNotSupportedException("当前支持 macOS 和 Windows。Windows 请使用 Windows 目标构建。");
#endif
            _controller = new DesktopController(platform, lifetime);
            var main = new MainWindow(_controller);
            // Not lifetime.MainWindow: Avalonia shows that window at startup, and Tbox starts in the tray.
            _controller.Main = main;
            var activation = this.TryGetFeature<IActivatableLifetime>();
            void OnActivated(object? sender, ActivatedEventArgs args) => RestoreOnReopen(_controller, args);
            if (activation is not null) activation.Activated += OnActivated;
            // A primary click opens the main window; secondary click keeps a native menu with the quick actions and quit.
            // Titles are functions because the shortcuts in them can be changed in the main window.
            (Func<string>? Title, Action Action)[] menu =
            [
                (() => "打开 Tbox", _controller.ShowMain), (null, () => { }),
                (() => $"区域截图   {ShortcutLabels.Capture}", () => _controller.Run(_controller.CaptureAsync)),
                (() => $"剪贴板贴图   {ShortcutLabels.Pin}", () => _controller.Run(_controller.PinClipboardAsync)),
                (() => $"隐藏 / 显示贴图   {ShortcutLabels.Toggle}", _controller.TogglePins), (null, () => { }),
                (() => "退出 Tbox", _controller.Quit)
            ];
            if (OperatingSystem.IsMacOS()) CreateStatusItem(_controller, menu);
            else CreateTrayIcon(_controller, menu);
            _controller.ShortcutsChanged += () => RetitleMenu(menu);
            lifetime.Exit += (_, _) =>
            {
                if (activation is not null) activation.Activated -= OnActivated;
                _tray?.Dispose(); _statusItem?.Dispose(); _controller.Dispose();
            };
            lifetime.ShutdownRequested += (_, e) =>
            {
                if (!_controller.IsQuitting) { e.Cancel = true; _controller.Quit(); }
            };
            _ = Instance?.ListenAsync(async command =>
            {
                if (command == 3) return await PermissionDiagnostics.RunAsync(platform);
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (command == 2) _controller.Quit(); else if (command == 1) _controller.ShowMain();
                });
                return null;
            });
            // The main window stays hidden at startup, so initialization no longer waits for it to open.
            Dispatcher.UIThread.Post(() => _controller.Run(() => _controller.InitializeAsync(lifetime.Args ?? [])));
        }
        base.OnFrameworkInitializationCompleted();
    }

    private void CreateTrayIcon(DesktopController controller, (Func<string>? Title, Action Action)[] entries)
    {
        var menu = ResidentMenu.Create(entries);
        using var trayAsset = AssetLoader.Open(new Uri("avares://Tbox.Desktop/Assets/WindowsTrayIcon.ico"));
        _tray = new TrayIcon { Icon = new WindowIcon(trayAsset), ToolTipText = "Tbox", Menu = menu, IsVisible = true };
        _tray.Clicked += (_, _) => controller.ShowMain();
        TrayIcon.SetIcons(this, new TrayIcons { _tray });
    }

    private void RetitleMenu((Func<string>? Title, Action Action)[] entries)
    {
        if (_tray?.Menu is { } menu) ResidentMenu.Retitle(menu, ResidentMenu.Titles(entries));
        if (_statusItem is not null) ResidentMenu.RetitleMac(entries, _statusItem.Retitle);
    }

    private void CreateStatusItem(DesktopController controller, (Func<string>? Title, Action Action)[] entries)
    {
        using var asset = AssetLoader.Open(new Uri("avares://Tbox.Desktop/Assets/MenuBarIcon.png"));
        using var png = new MemoryStream(); asset.CopyTo(png);
        // The approved monochrome silhouette is drawn as a template image with the system menu bar tint.
        _statusItem = new MacStatusItem(png.ToArray(), "Tbox", entries.Select(entry => entry.Title?.Invoke()).ToArray(),
            () => Dispatcher.UIThread.Post(controller.ShowMain),
            command => Dispatcher.UIThread.Post(() => ResidentMenu.Choose(entries, command)));
    }

    internal static void RestoreOnReopen(DesktopController controller, ActivatedEventArgs args)
    {
        // macOS reopens an existing process through the application lifetime,
        // without starting the second process handled by SingleInstance.
        if (args.Kind == ActivationKind.Reopen)
            Dispatcher.UIThread.Post(controller.ShowMain);
    }
}
