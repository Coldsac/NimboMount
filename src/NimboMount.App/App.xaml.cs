using System.Windows;

namespace NimboMount;

public partial class App : System.Windows.Application
{
    private Mutex? _instance;

    public static bool StartInTray { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        StartInTray = e.Args.Any(arg => string.Equals(arg, "--startup", StringComparison.OrdinalIgnoreCase));
        _instance = new Mutex(true, @"Local\NimboMount", out var created);
        if (!created)
        {
            if (!StartInTray)
            {
                System.Windows.MessageBox.Show(
                    "NimboMount is already running. Look for N in the system tray.",
                    "NimboMount",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }

            Shutdown();
            return;
        }

        base.OnStartup(e);

        var window = new MainWindow();
        MainWindow = window;
        if (!StartInTray)
        {
            window.Show();
            return;
        }

        window.ShowActivated = false;
        window.ShowInTaskbar = false;
        window.Opacity = 0;
        window.Show();
        window.Hide();
        window.Opacity = 1;
        window.ShowInTaskbar = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _instance?.Dispose();
        base.OnExit(e);
    }
}
