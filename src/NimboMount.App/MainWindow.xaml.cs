using System.IO;
using System.ComponentModel;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using NimboMount.Drive;
using NimboMount.Mega;

namespace NimboMount;

public partial class MainWindow : Window
{
    private readonly object _logGate = new();
    private readonly DispatcherTimer _refreshTimer;
    private System.Windows.Forms.NotifyIcon? _tray;
    private System.Drawing.Icon? _trayIcon;
    private MegaClient? _client;
    private CloudFileSystem? _fileSystem;
    private DriveHost? _drive;
    private bool _exit;
    private bool _busy;
    private bool _balloonShown;
    private bool _mounted;
    private bool _applyingStartup;

    public MainWindow()
    {
        InitializeComponent();
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(45) };
        _refreshTimer.Tick += async (_, _) => await RefreshCloudAsync();
        for (var letter = 'A'; letter <= 'Z'; letter++)
        {
            LetterBox.Items.Add(letter + ":");
        }
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        CreateTray();
        var settings = SessionStore.LoadSettings();
        EmailInput.Text = settings.Email;
        RememberBox.IsChecked = settings.Remember;
        LaunchBox.IsChecked = settings.MountOnLaunch;
        var letter = string.IsNullOrWhiteSpace(settings.DriveLetter) ? "M:" : settings.DriveLetter.Trim().ToUpperInvariant();
        if (!letter.EndsWith(':'))
        {
            letter += ":";
        }

        LetterBox.SelectedItem = LetterBox.Items.Contains(letter) ? letter : "M:";
        _applyingStartup = true;
        StartupBox.IsChecked = settings.StartWithWindows;
        _applyingStartup = false;
        try
        {
            WindowsStartup.SetEnabled(settings.StartWithWindows);
        }
        catch (Exception ex)
        {
            Log("Windows startup could not be updated. " + ex.Message);
        }

        if (App.StartInTray)
        {
            Log("Started with Windows.");
        }

        if (!DriveHost.WinFspInstalled)
        {
            WinFspBanner.Visibility = Visibility.Visible;
            ConnectButton.IsEnabled = false;
            Log("Install WinFsp, then open NimboMount again.");
            return;
        }

        if (settings.Remember)
        {
            await RestoreSessionAsync(settings.MountOnLaunch);
        }
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        var enabled = 1;
        if (DwmSetWindowAttribute(handle, 20, ref enabled, sizeof(int)) != 0)
        {
            DwmSetWindowAttribute(handle, 19, ref enabled, sizeof(int));
        }
    }

    private async void OnConnect(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }

        try
        {
            if (_client == null)
            {
                await SignInAsync();
            }

            if (_client != null && !_mounted)
            {
                await MountCurrentAsync();
            }
        }
        catch (Exception ex)
        {
            Log(Unwrap(ex).Message);
            SetBusy(false);
        }
    }

    private async void OnUnmount(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }

        SetBusy(true);
        SetStatus("Uploading changes before unmount...", "#D0D0D0");
        var unmounted = await UnmountAsync();
        SetBusy(false);
        if (unmounted)
        {
            UpdateVisualState();
        }
    }

    private async void OnSignOut(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }

        SetBusy(true);
        var unmounted = await UnmountAsync();
        SetBusy(false);
        if (!unmounted)
        {
            return;
        }

        SessionStore.Delete();

        _client?.Dispose();
        _client = null;
        _fileSystem = null;
        PasswordInput.Clear();
        UpdateQuota(0, 0);
        Log("Signed out.");
        UpdateVisualState();
    }

    private void OnDownloadWinFsp(object sender, RoutedEventArgs e)
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://winfsp.dev/rel/")
        {
            UseShellExecute = true,
        });
    }

    private void OnOpenLink(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(e.Uri.AbsoluteUri)
        {
            UseShellExecute = true,
        });
        e.Handled = true;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_exit)
        {
            e.Cancel = true;
            Hide();
            if (!_balloonShown && _tray != null)
            {
                _balloonShown = true;
                _tray.ShowBalloonTip(
                    2500,
                    "NimboMount",
                    "NimboMount keeps running in the tray. Right-click the icon to exit.",
                    System.Windows.Forms.ToolTipIcon.Info);
            }

            return;
        }

        SaveSettings();
        if (_tray != null)
        {
            _tray.Visible = false;
            _tray.Dispose();
            _tray = null;
            _trayIcon?.Dispose();
            _trayIcon = null;
        }

        base.OnClosing(e);
    }

    private async Task RestoreSessionAsync(bool mount)
    {
        MegaSession? session;
        try
        {
            session = SessionStore.TryLoad();
        }
        catch (Exception ex)
        {
            Log("The saved sign-in could not be opened. Sign in again.");
            Log(ex.Message);
            SessionStore.Delete();
            return;
        }

        if (session == null)
        {
            return;
        }

        SetBusy(true);
        SetStatus("Opening the saved MEGA session...", "#D0D0D0");
        var client = new MegaClient(Log);
        try
        {
            client.Resume(session);
            await client.RefreshAsync(CancellationToken.None);
            _client = client;
            EmailInput.Text = session.Email;
            UpdateQuota(client.UsedBytes, client.TotalBytes);
            UpdateVisualState();
            if (mount)
            {
                await MountCurrentAsync();
            }
        }
        catch (Exception ex)
        {
            client.Dispose();
            SessionStore.Delete();
            Log(Unwrap(ex).Message);
            SetStatus("The saved session expired. Sign in again.", "#FF8A80");
        }
        finally
        {
            SetBusy(false);
            UpdateVisualState();
        }
    }

    private async Task SignInAsync()
    {
        var email = EmailInput.Text.Trim();
        var password = PasswordInput.Password;
        var mfa = MfaInput.Text.Trim();
        if (email.Length == 0 || password.Length == 0)
        {
            SetStatus("Enter the email and password for the MEGA account.", "#FF8A80");
            return;
        }

        SetBusy(true);
        SetStatus("Signing in to MEGA...", "#D0D0D0");
        var client = new MegaClient(Log);
        try
        {
            await client.LoginAsync(email, password, string.IsNullOrWhiteSpace(mfa) ? null : mfa, CancellationToken.None);
            await client.RefreshAsync(CancellationToken.None);
            _client = client;
            PasswordInput.Clear();
            MfaRow.Visibility = Visibility.Collapsed;
            MfaInput.Clear();
            if (RememberBox.IsChecked == true)
            {
                SessionStore.Save(client.ExportSession());
            }
            else
            {
                SessionStore.Delete();
            }

            SaveSettings();
            UpdateQuota(client.UsedBytes, client.TotalBytes);
            Log("Signed in as " + client.Email + ".");
            UpdateVisualState();
        }
        catch (MegaException ex) when (ex.MfaRequired)
        {
            client.Dispose();
            MfaRow.Visibility = Visibility.Visible;
            MfaInput.Focus();
            SetStatus("Enter the six-digit authenticator code, then connect again.", "#F6E7C1");
            Log(ex.Message);
        }
        catch (Exception ex)
        {
            client.Dispose();
            SetStatus(Unwrap(ex).Message, "#FF8A80");
            Log(Unwrap(ex).Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task MountCurrentAsync()
    {
        if (_client == null || _mounted)
        {
            return;
        }

        if (!DriveHost.WinFspInstalled)
        {
            WinFspBanner.Visibility = Visibility.Visible;
            Log("WinFsp is not installed.");
            return;
        }

        var letter = SelectedLetter();
        SetBusy(true);
        SetStatus("Mounting " + letter + ": ...", "#D0D0D0");
        var fileSystem = new CloudFileSystem(_client, AppPaths.Directory, Log);
        var drive = new DriveHost();
        try
        {
            await Task.Run(() => fileSystem.RecoverInterrupted());
            await Task.Run(() => drive.Mount(fileSystem, letter));
            _fileSystem = fileSystem;
            _drive = drive;
            _mounted = true;
            SaveSettings();
            var point = drive.MountPoint ?? letter + ":";
            Log("MEGA is mounted at " + point);
            SetStatus("Mounted at " + point, "#3DDC97");
            _refreshTimer.Start();
        }
        catch (Exception ex)
        {
            drive.Dispose();
            SetStatus(Unwrap(ex).Message, "#FF8A80");
            Log(Unwrap(ex).Message);
        }
        finally
        {
            SetBusy(false);
            UpdateVisualState();
        }
    }

    private async Task<bool> UnmountAsync()
    {
        if (!_mounted || _drive == null)
        {
            _mounted = false;
            return true;
        }

        try
        {
            if (_fileSystem != null)
            {
                await _fileSystem.FlushDirtyAsync(CancellationToken.None);
            }

            _drive.Unmount();
            _drive = null;
            _fileSystem = null;
            _mounted = false;
            _refreshTimer.Stop();
            Log("The MEGA drive was unmounted.");
            SetStatus(_client == null ? "Signed out" : "Connected. The drive is not mounted.", "#D0D0D0");
            return true;
        }
        catch (Exception ex)
        {
            Log("The drive stayed mounted because an upload failed.");
            Log(Unwrap(ex).Message);
            SetStatus("Unmount stopped. An upload still needs to finish.", "#FF8A80");
            return false;
        }
    }

    private async Task RefreshCloudAsync()
    {
        if (_client == null || _busy)
        {
            return;
        }

        if (_fileSystem != null && _fileSystem.HasPendingWork)
        {
            return;
        }

        try
        {
            await _client.RefreshQuotaAsync(CancellationToken.None);
            if (_fileSystem != null && !_fileSystem.HasPendingWork)
            {
                await Task.Run(() => _fileSystem.Reload());
            }

            UpdateQuota(_client.UsedBytes, _client.TotalBytes);
        }
        catch (Exception ex)
        {
            Log(Unwrap(ex).Message);
        }
    }

    private async Task UnmountFromTrayAsync()
    {
        SetBusy(true);
        var unmounted = await UnmountAsync();
        SetBusy(false);
        if (unmounted)
        {
            UpdateVisualState();
        }
    }

    private async Task ExitAsync()
    {
        if (_exit)
        {
            return;
        }

        Show();
        Activate();
        SetBusy(true);
        SetStatus("Uploading changes before exit...", "#D0D0D0");
        var unmounted = await UnmountAsync();
        SetBusy(false);
        if (!unmounted)
        {
            return;
        }

        _client?.Dispose();
        _client = null;
        _exit = true;
        Close();
    }

    private void CreateTray()
    {
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Open NimboMount", null, (_, _) =>
        {
            Dispatcher.Invoke(() =>
            {
                ShowInTaskbar = true;
                Show();
                WindowState = WindowState.Normal;
                Activate();
            });
        });
        menu.Items.Add("Unmount", null, (_, _) => Dispatcher.BeginInvoke(new Action(() => _ = UnmountFromTrayAsync())));
        menu.Items.Add("Exit", null, (_, _) => Dispatcher.BeginInvoke(new Action(() => _ = ExitAsync())));
        _trayIcon = LoadTrayIcon();
        _tray = new System.Windows.Forms.NotifyIcon
        {
            Icon = _trayIcon,
            Visible = true,
            Text = "NimboMount",
            ContextMenuStrip = menu,
        };
        _tray.DoubleClick += (_, _) => Dispatcher.Invoke(() =>
        {
            ShowInTaskbar = true;
            Show();
            WindowState = WindowState.Normal;
            Activate();
        });
    }

    private static System.Drawing.Icon LoadTrayIcon()
    {
        var info = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/NimboMount.ico"));
        if (info?.Stream == null)
        {
            throw new InvalidOperationException("NimboMount could not load its icon.");
        }

        using (info.Stream)
        {
            return new System.Drawing.Icon(info.Stream, 32, 32);
        }
    }

    private void UpdateVisualState()
    {
        var signedIn = _client != null;
        ConnectButton.IsEnabled = !_busy && DriveHost.WinFspInstalled && !_mounted;
        UnmountButton.IsEnabled = !_busy && _mounted;
        SignOutButton.IsEnabled = !_busy && signedIn;
        EmailInput.IsEnabled = !_busy && !signedIn;
        PasswordInput.IsEnabled = !_busy && !signedIn;
        LetterBox.IsEnabled = !_busy && !_mounted;
        ConnectButton.Content = signedIn ? "Mount" : "Connect and mount";
        if (_busy)
        {
            return;
        }

        if (_mounted)
        {
            var point = _drive?.MountPoint ?? SelectedLetter() + ":";
            SetStatus("Mounted at " + point, "#3DDC97");
            StatusDot.Fill = new SolidColorBrush(System.Windows.Media.Color.FromRgb(61, 220, 151));
        }
        else if (signedIn)
        {
            SetStatus("Connected as " + _client!.Email + ".", "#F4F4F4");
            StatusDot.Fill = new SolidColorBrush(System.Windows.Media.Color.FromRgb(240, 180, 60));
        }
        else
        {
            SetStatus("Signed out", "#9A9A9A");
            StatusDot.Fill = new SolidColorBrush(System.Windows.Media.Color.FromRgb(110, 110, 110));
        }
    }

    private void UpdateQuota(long used, long total)
    {
        if (total <= 0)
        {
            QuotaDetail.Text = "";
            QuotaBar.Value = 0;
            return;
        }

        QuotaDetail.Text = FormatBytes(used) + " of " + FormatBytes(total);
        QuotaBar.Value = Math.Clamp(used * 100.0 / total, 0, 100);
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        UpdateVisualState();
    }

    private void SetStatus(string text, string color)
    {
        StatusText.Text = text;
        StatusText.Foreground = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(color)!);
    }

    private char SelectedLetter()
    {
        var text = LetterBox.SelectedItem as string;
        if (string.IsNullOrWhiteSpace(text))
        {
            return 'M';
        }

        return char.ToUpperInvariant(text[0]);
    }

    private void SaveSettings()
    {
        SessionStore.SaveSettings(new AppSettings
        {
            Email = EmailInput.Text.Trim(),
            DriveLetter = SelectedLetter().ToString(),
            Remember = RememberBox.IsChecked == true,
            MountOnLaunch = LaunchBox.IsChecked == true,
            StartWithWindows = StartupBox.IsChecked == true,
        });
    }

    private void OnStartWithWindowsChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _applyingStartup)
        {
            return;
        }

        var enabled = StartupBox.IsChecked == true;
        try
        {
            WindowsStartup.SetEnabled(enabled);
            SaveSettings();
            Log(enabled
                ? "NimboMount will start when you sign in to Windows."
                : "NimboMount will no longer start with Windows.");
        }
        catch (Exception ex)
        {
            _applyingStartup = true;
            StartupBox.IsChecked = !enabled;
            _applyingStartup = false;
            Log("Windows startup could not be updated. " + ex.Message);
        }
    }

    private void Log(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        var line = DateTime.Now.ToString("HH:mm:ss") + "  " + message;
        lock (_logGate)
        {
            try
            {
                Directory.CreateDirectory(AppPaths.Directory);
                File.AppendAllText(AppPaths.LogPath, line + Environment.NewLine);
            }
            catch (IOException)
            {
            }
        }

        try
        {
            Dispatcher.BeginInvoke(() =>
            {
                LogList.Items.Add(line);
                while (LogList.Items.Count > 200)
                {
                    LogList.Items.RemoveAt(0);
                }

                LogList.ScrollIntoView(LogList.Items[^1]);
            });
        }
        catch (Exception)
        {
        }
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = Math.Max(0, bytes);
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{value:0} {units[unit]}" : $"{value:0.0} {units[unit]}";
    }

    private static Exception Unwrap(Exception ex)
    {
        while (ex is AggregateException aggregate && aggregate.InnerException != null)
        {
            ex = aggregate.InnerException;
        }

        return ex;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
