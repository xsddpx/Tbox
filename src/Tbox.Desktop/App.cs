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
    private TrayMenuWindow? _menu;
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
            // A primary click opens the main window; secondary click opens the quick actions and quit.
            // Windows draws that menu as a UI 3.0 card (TrayMenuWindow); macOS keeps the system status menu.
            // Windows titles stay plain because the menu draws the shortcuts in its own caps column.
            // Titles are functions because the shortcuts in them can be changed in the main window.
            var statusMenu = OperatingSystem.IsMacOS();
            (Func<string>? Title, Action Action)[] menu =
            [
                (() => "打开 Tbox", _controller.ShowMain), (null, () => { }),
                (() => statusMenu ? $"区域截图   {ShortcutLabels.Capture}" : "区域截图", () => _controller.Run(_controller.CaptureAsync)),
                (() => statusMenu ? $"剪贴板贴图   {ShortcutLabels.Pin}" : "剪贴板贴图", () => _controller.Run(_controller.PinClipboardAsync)),
                (() => statusMenu ? $"隐藏 / 显示贴图   {ShortcutLabels.Toggle}" : "隐藏 / 显示贴图", _controller.TogglePins), (null, () => { }),
                (() => "退出 Tbox", _controller.Quit)
            ];
            if (OperatingSystem.IsMacOS()) CreateStatusItem(_controller, menu);
            else CreateTrayIcon(_controller);
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

    private void CreateTrayIcon(DesktopController controller)
    {
        using var trayAsset = AssetLoader.Open(new Uri("avares://Tbox.Desktop/Assets/WindowsTrayIcon.ico"));
        _tray = new TrayIcon { Icon = new WindowIcon(trayAsset), ToolTipText = "Tbox", IsVisible = true };
        // A left click raises Clicked; a right click is intercepted on the tray window and opens the
        // app's own menu (see HookTrayWindow and NativeMethods.SetTrayRightClickHandler).
        _tray.Clicked += (_, _) => controller.ShowMain();
        TrayIcon.SetIcons(this, new TrayIcons { _tray });
#if WINDOWS
        NativeMethods.SetTrayRightClickHandler(() => Dispatcher.UIThread.Post(() => ToggleMenu(controller)));
        // Avalonia creates the tray window once the icon is attached; hooking it must happen after that.
        Dispatcher.UIThread.Post(HookTrayWindow, DispatcherPriority.Loaded);
#endif
    }

    // Replaces the tray window procedure so a right click opens the menu before Avalonia handles it.
    private static void HookTrayWindow()
    {
#if WINDOWS
        NativeMethods.HookTrayWindow(NativeMethods.FindOwnWindow());
#endif
    }

    // The menu closes on any click outside it, so the same click that closes it does not reopen it.
    private void ToggleMenu(DesktopController controller)
    {
        if (_menu is not null) { _menu.Dismiss(); return; }
        var menu = new TrayMenuWindow(controller, () => controller.PinsVisible);
        _menu = menu;
        menu.Closed += (_, _) => { if (ReferenceEquals(_menu, menu)) _menu = null; };
        menu.Show();
        PlaceMenu(menu);
        // A window that does not take focus when shown never gets deactivated either; taking focus makes
        // the menu dismiss when the user clicks back into another window.
        menu.Activate();
    }

    // The menu sits just above the taskbar on the screen that owns the tray icon. The pointer is not a
    // reliable hint: Windows reports the cursor over a tray icon at the taskbar's far corner, which is
    // the next screen when that corner is a monitor boundary. The tray window's own monitor is used.
    private void PlaceMenu(TrayMenuWindow menu)
    {
        var screens = menu.Screens;
        var screen = TrayScreen(screens) ?? screens.Primary;
        if (screen is null) return;
        var area = screen.WorkingArea;
        var point = menu.PlaceInside(new Rect(0, 0, area.Width / screen.Scaling, area.Height / screen.Scaling));
        menu.Position = new PixelPoint(area.X, area.Y) + new PixelPoint((int)(point.X * screen.Scaling), (int)(point.Y * screen.Scaling));
    }

    private static Screen? TrayScreen(Screens screens)
    {
#if WINDOWS
        // The tray icon is drawn on the taskbar, which belongs to one monitor; the menu follows it.
        var taskbar = NativeMethods.TrayWindowBounds();
        if (taskbar is { } bounds) return screens.All.FirstOrDefault(screen => screen.Bounds.Contains(bounds));
#endif
        return null;
    }

    private void RetitleMenu((Func<string>? Title, Action Action)[] entries)
    {
        _menu?.Refresh();
        var titles = entries.Where(entry => entry.Title is not null).Select(entry => entry.Title!()).ToArray();
        for (var index = 0; _statusItem is not null && index < titles.Length; index++) _statusItem.Retitle(index + 1, titles[index]);
    }

    private void CreateStatusItem(DesktopController controller, (Func<string>? Title, Action Action)[] entries)
    {
        using var asset = AssetLoader.Open(new Uri("avares://Tbox.Desktop/Assets/MenuBarIcon.png"));
        using var png = new MemoryStream(); asset.CopyTo(png);
        var actions = entries.Where(entry => entry.Title is not null).Select(entry => entry.Action).ToArray();
        // The approved monochrome silhouette is drawn as a template image with the system menu bar tint.
        _statusItem = new MacStatusItem(png.ToArray(), "Tbox", entries.Select(entry => entry.Title?.Invoke()).ToArray(),
            () => Dispatcher.UIThread.Post(controller.ShowMain),
            command => Dispatcher.UIThread.Post(() => { if (command <= actions.Length) actions[command - 1](); }));
    }

    internal static void RestoreOnReopen(DesktopController controller, ActivatedEventArgs args)
    {
        // macOS reopens an existing process through the application lifetime,
        // without starting the second process handled by SingleInstance.
        if (args.Kind == ActivationKind.Reopen)
            Dispatcher.UIThread.Post(controller.ShowMain);
    }
}
