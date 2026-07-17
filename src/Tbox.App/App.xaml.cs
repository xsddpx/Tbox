using Microsoft.UI.Xaml;

namespace Tbox.App;

public partial class App : Application
{
    private MainWindow? _window;
    private readonly SingleInstanceCoordinator _singleInstance;

    public App()
    {
        _singleInstance = new SingleInstanceCoordinator();
        if (!_singleInstance.IsPrimary)
        {
            _singleInstance.SignalPrimary();
            Environment.Exit(0);
        }
        try
        {
            InitializeComponent();
        }
        catch (Exception exception)
        {
            var logDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Tbox");
            Directory.CreateDirectory(logDirectory);
            File.WriteAllText(Path.Combine(logDirectory, "startup-error.log"), exception.ToString());
            throw;
        }
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            _window = new MainWindow();
            _window.Activate();
            await _window.InitializeAsync();
            _singleInstance.Listen(() => _window.DispatcherQueue.TryEnqueue(_window.ShowSettings));
        }
        catch (Exception exception)
        {
            var logDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Tbox");
            Directory.CreateDirectory(logDirectory);
            File.WriteAllText(Path.Combine(logDirectory, "startup-error.log"), exception.ToString());
            Environment.ExitCode = 1;
            Environment.Exit(1);
        }
    }
}
