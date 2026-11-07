using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;

namespace Tbox.Desktop;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Contains("--quit")) { SingleInstance.RequestExit(); return 0; }
        if (args.Contains("--version"))
        {
            Console.WriteLine(typeof(Program).Assembly.GetName().Version);
            return 0;
        }
        try
        {
            if (args.Contains("--permission-probe"))
            {
                // Query the independently launched app; this CLI process must never perform the capture itself.
                Console.WriteLine(SingleInstance.RequestPermissionProbeAsync().GetAwaiter().GetResult()); return 0;
            }
            using var instance = SingleInstance.Acquire(args.Contains("--smoke-test"));
            if (instance is null) return 0;
            App.Instance = instance;
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown);
        }
        catch (Exception exception)
        {
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Tbox");
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "desktop-startup-error.log"), exception.ToString());
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();
}
