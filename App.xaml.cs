using System.Diagnostics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppNotifications;
using Win10CalendarFlyout.Services;

namespace Win10CalendarFlyout;

public partial class App : Application
{
    private MainWindow? _window;
    private WelcomeWindow? _welcomeWindow;
    private TrayIconService? _trayIcon;
    private DispatcherQueue? _dispatcher;
    private bool _notificationsRegistered;
    private SettingsWindow? _settingsOnlyWindow;
    private GoogleCalendarService? _settingsOnlyCalendarService;

    public App()
    {
        InitializeComponent();
        UnhandledException += App_UnhandledException;
    }

    private static void App_UnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        try
        {
            string directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Win10CalendarFlyout");
            Directory.CreateDirectory(directory);

            string path = Path.Combine(directory, "last-crash.txt");
            string details =
                $"Calendar Flyout unhandled exception\r\n" +
                $"Time: {DateTimeOffset.Now:O}\r\n" +
                $"Message: {e.Message}\r\n" +
                $"Exception: {e.Exception}\r\n";

            File.WriteAllText(path, details);
            Debug.WriteLine(details);
        }
        catch
        {
            // Diagnostics must never replace the original exception.
        }

        // Do not mark the exception handled. The log is diagnostic only; swallowing
        // arbitrary WinUI exceptions can leave the XAML tree in an invalid state.
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _dispatcher = DispatcherQueue.GetForCurrentThread();

        if (Environment.GetCommandLineArgs().Any(arg =>
                string.Equals(arg, "--settings", StringComparison.OrdinalIgnoreCase)))
        {
            LaunchSettingsOnly();
            return;
        }

        _window = new MainWindow();

        RegisterNotifications();

        // If the user previously opted into startup and later moved/replaced the
        // single-file EXE, repair the per-user Run entry to the executable that is
        // actually running now.
        if (AppSettings.WelcomeCompleted && StartupService.IsEnabled())
        {
            try { StartupService.SetEnabled(true); }
            catch { }
        }

        _trayIcon = new TrayIconService(_dispatcher);
        _window.AttachTrayIcon(_trayIcon);

        // Build the hidden XAML/Acrylic surface now so the first real tray click does
        // not expose an unpainted desktop window.
        _ = _window.PrepareFlyoutSurfaceAsync();

        _trayIcon.Invoked += (_, _) => _window.ToggleFlyout();
        _trayIcon.RefreshRequested += async (_, _) => await _window.RefreshAgendaAsync();
        _trayIcon.ExitRequested += (_, _) => ExitApp();

        if (!AppSettings.WelcomeCompleted)
        {
            _welcomeWindow = new WelcomeWindow();
            _welcomeWindow.Closed += (_, _) => _welcomeWindow = null;
            _ = _welcomeWindow.ShowPreparedAsync();
        }

        // Warm Google, the month cache, and scheduled reminders while the calendar
        // flyout itself stays hidden.
        _ = _window.InitializeInBackgroundAsync();
    }

    private void LaunchSettingsOnly()
    {
        _settingsOnlyCalendarService = new GoogleCalendarService();
        _settingsOnlyCalendarService.ApplyColorOverrides(AppSettings.Current.CalendarColorOverrides);

        IReadOnlyList<Models.CalendarDescriptor> cached = Array.Empty<Models.CalendarDescriptor>();
        _settingsOnlyWindow = new SettingsWindow(cached);
        _settingsOnlyWindow.Closed += SettingsOnlyWindow_Closed;
        _ = _settingsOnlyWindow.ShowPreparedAsync();
        _ = LoadSettingsCalendarsAsync();
    }

    private async Task LoadSettingsCalendarsAsync()
    {
        GoogleCalendarService? service = _settingsOnlyCalendarService;
        SettingsWindow? window = _settingsOnlyWindow;
        if (service is null || window is null) return;

        if (!service.HasCredentialsFile || !service.HasStoredToken)
        {
            window.SetCalendarLoading(false);
            return;
        }

        window.SetCalendarLoading(true);
        try
        {
            await service.SignInAsync();
            IReadOnlyList<Models.CalendarDescriptor> calendars = await service.GetSelectedCalendarsAsync();
            _dispatcher?.TryEnqueue(() => _settingsOnlyWindow?.UpdateCalendars(calendars));
        }
        catch
        {
            _dispatcher?.TryEnqueue(() => _settingsOnlyWindow?.SetCalendarLoading(false));
        }
    }

    private void SettingsOnlyWindow_Closed(object sender, WindowEventArgs args)
    {
        if (_settingsOnlyWindow is not null)
        {
            _settingsOnlyWindow.Closed -= SettingsOnlyWindow_Closed;
        }

        _settingsOnlyWindow = null;
        _settingsOnlyCalendarService = null;

        // This process exists only for Settings. Exiting here guarantees that every
        // WinUI, Mica, HWND, managed heap and native resource created for Settings is
        // reclaimed by the OS instead of relying on secondary-window collection.
        Exit();
    }

    private void RegisterNotifications()
    {
        try
        {
            AppNotificationManager.Default.NotificationInvoked += OnNotificationInvoked;
            AppNotificationManager.Default.Register();
            _notificationsRegistered = true;
        }
        catch
        {
            // The calendar still works on systems/policies that block app notifications.
            _notificationsRegistered = false;
        }
    }

    private void OnNotificationInvoked(AppNotificationManager sender, AppNotificationActivatedEventArgs args)
    {
        string action = args.Arguments.TryGetValue("action", out string? actionValue)
            ? actionValue
            : "open";
        string url = args.Arguments.TryGetValue("url", out string? urlValue)
            ? urlValue
            : string.Empty;

        _dispatcher?.TryEnqueue(() =>
        {
            if (string.Equals(action, "dismiss", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if ((string.Equals(action, "open", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(action, "reschedule", StringComparison.OrdinalIgnoreCase)) &&
                !string.IsNullOrWhiteSpace(url))
            {
                try
                {
                    Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                }
                catch
                {
                    // Notification activation should never crash the tray process.
                }
            }
        });
    }

    private void ExitApp()
    {
        _trayIcon?.Dispose();
        if (_notificationsRegistered)
        {
            try
            {
                AppNotificationManager.Default.NotificationInvoked -= OnNotificationInvoked;
                AppNotificationManager.Default.Unregister();
            }
            catch { }
        }

        _welcomeWindow?.Close();
        _window?.CloseForReal();
        Exit();
    }
}
