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
    public override void Initialize() => Styles.Add(DesignTokens.CreateTheme());

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
            lifetime.MainWindow = main;
            _controller.Main = main;
            var activation = this.TryGetFeature<IActivatableLifetime>();
            void OnActivated(object? sender, ActivatedEventArgs args) => RestoreOnReopen(_controller, args);
            if (activation is not null) activation.Activated += OnActivated;
            var menu = new NativeMenu();
            Add(menu, "打开 Tbox", _controller.ShowMain);
            menu.Add(new NativeMenuItemSeparator());
            Add(menu, $"区域截图   {ShortcutLabels.Capture}", () => _controller.Run(_controller.CaptureAsync));
            Add(menu, $"剪贴板贴图   {ShortcutLabels.Pin}", () => _controller.Run(_controller.PinClipboardAsync));
            Add(menu, $"隐藏 / 显示贴图   {ShortcutLabels.Toggle}", _controller.TogglePins);
            menu.Add(new NativeMenuItemSeparator());
            Add(menu, "退出 Tbox", _controller.Quit);
            var trayResource = OperatingSystem.IsMacOS() ? "MenuBarIcon.png" : "AppIcon.png";
            using var trayAsset = AssetLoader.Open(new Uri($"avares://Tbox.Desktop/Assets/{trayResource}"));
            _tray = new TrayIcon
            {
                Icon = new WindowIcon(trayAsset),
                ToolTipText = "Tbox", Menu = menu, IsVisible = true
            };
            // Use the approved monochrome silhouette with the system menu bar tint.
            if (OperatingSystem.IsMacOS()) MacOSProperties.SetIsTemplateIcon(_tray, true);
            _tray.Clicked += (_, _) => _controller.ShowMain();
            TrayIcon.SetIcons(this, new TrayIcons { _tray });
            lifetime.Exit += (_, _) =>
            {
                if (activation is not null) activation.Activated -= OnActivated;
                _tray.Dispose(); _controller.Dispose();
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
            main.Opened += (_, _) => _controller.Run(() => _controller.InitializeAsync(lifetime.Args ?? []));
        }
        base.OnFrameworkInitializationCompleted();
    }

    internal static void RestoreOnReopen(DesktopController controller, ActivatedEventArgs args)
    {
        // macOS reopens an existing process through the application lifetime,
        // without starting the second process handled by SingleInstance.
        if (args.Kind == ActivationKind.Reopen)
            Dispatcher.UIThread.Post(controller.ShowMain);
    }

    private static void Add(NativeMenu menu, string title, Action action)
    {
        var item = new NativeMenuItem(title);
        item.Click += (_, _) => action();
        menu.Add(item);
    }
}
