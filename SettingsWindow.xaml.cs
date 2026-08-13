using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.UI;
using Win10CalendarFlyout.Interop;
using Win10CalendarFlyout.Models;
using Win10CalendarFlyout.Services;
using WinRT.Interop;

namespace Win10CalendarFlyout;

public sealed partial class SettingsWindow : Window
{
    private const int LogicalWidth = 760;
    private const int LogicalHeight = 760;

    private readonly AppWindow _appWindow;
    private readonly IntPtr _hwnd;
    private IReadOnlyList<CalendarDescriptor> _calendars;
    private bool _initializing = true;
    private bool _disposed;

    public SettingsWindow(IReadOnlyList<CalendarDescriptor> calendars)
    {
        InitializeComponent();
        _calendars = calendars;
        Title = "Calendar Flyout settings";
        SystemBackdrop = new MicaBackdrop();

        _hwnd = WindowNative.GetWindowHandle(this);
        WindowId id = Win32Interop.GetWindowIdFromWindow(_hwnd);
        _appWindow = AppWindow.GetFromWindowId(id);
        _appWindow.IsShownInSwitchers = true;
        Closed += SettingsWindow_Closed;

        ConfigureTitleBar();
        SetWindowIcon();
        ResizeForCurrentDpi();

        if (_appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsMaximizable = true;
            presenter.IsMinimizable = true;
            presenter.IsResizable = true;
        }

        SwitchToTodayToggle.IsOn = AppSettings.Current.SwitchToTodayOnOpen;
        MaxDotsComboBox.SelectedIndex = Math.Clamp(AppSettings.Current.MaxEventDots, 0, 4);
        GoogleAutoRefreshToggle.IsOn = AppSettings.Current.EnableGoogleAutoRefresh;
        ReminderChecksToggle.IsOn = AppSettings.Current.EnableEventReminderChecks;
        TrayDateIconToggle.IsOn = AppSettings.Current.EnableTrayDateIconUpdates;
        CurrentEventUpdatesToggle.IsOn = AppSettings.Current.EnableCurrentEventUpdates;
        ClickAwayDismissToggle.IsOn = AppSettings.Current.EnableClickAwayDismiss;
        VersionText.Text = GetDisplayVersion();
        BuildCalendarColorRows();
        _initializing = false;
        CenterOnPrimaryDisplay();
    }

    public Task ShowPreparedAsync()
    {
        if (_disposed) return Task.CompletedTask;

        // This is now a normal top-level window. Position it while it is still hidden,
        // then Activate once and let DWM/Windows provide the standard window-open
        // animation instead of moving a pre-rendered window in from off-screen.
        CenterOnPrimaryDisplay();
        _appWindow.IsShownInSwitchers = true;
        Activate();
        return Task.CompletedTask;
    }

    public void SetCalendarLoading(bool loading)
    {
        if (_disposed) return;
        CalendarColorLoadingRing.IsActive = loading;
        CalendarColorLoadingRing.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
        if (loading)
        {
            CalendarColorEmptyText.Text = "Loading calendars...";
            CalendarColorEmptyText.Visibility = Visibility.Visible;
        }
        else if (_calendars.Count == 0)
        {
            CalendarColorEmptyText.Text = "Sign in to Google Calendar to customize calendar colors.";
            CalendarColorEmptyText.Visibility = Visibility.Visible;
        }
        else
        {
            CalendarColorEmptyText.Visibility = Visibility.Collapsed;
        }
    }

    public void UpdateCalendars(IReadOnlyList<CalendarDescriptor> calendars)
    {
        if (_disposed) return;
        _calendars = calendars ?? Array.Empty<CalendarDescriptor>();
        BuildCalendarColorRows();
        SetCalendarLoading(false);
    }

    private void SettingsWindow_Closed(object sender, WindowEventArgs args)
    {
        if (_disposed) return;
        _disposed = true;
        Closed -= SettingsWindow_Closed;

        // Explicitly release the expensive native/XAML resources owned by this
        // secondary window. The next gear click creates a fresh SettingsWindow.
        try { SystemBackdrop = null; } catch { }

        foreach (FrameworkElement child in CalendarColorRows.Children.OfType<FrameworkElement>().ToArray())
        {
            if (child is Grid row)
            {
                foreach (Button button in row.Children.OfType<Button>())
                {
                    button.Click -= CalendarColorButton_Click;
                }
            }
        }
        CalendarColorRows.Children.Clear();
        _calendars = Array.Empty<CalendarDescriptor>();

        // Disconnect the XAML tree from the Window so its controls/composition
        // resources are eligible for collection immediately after the native HWND closes.
        try { Content = null; } catch { }
    }

    public void CloseForReal()
    {
        if (_disposed) return;
        Close();
    }

    private void ConfigureTitleBar()
    {
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindowTitleBar titleBar = _appWindow.TitleBar;
        titleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
        titleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
    }

    private void SetWindowIcon()
    {
        try
        {
            string iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "calendar.ico");
            if (File.Exists(iconPath)) _appWindow.SetIcon(iconPath);
        }
        catch { }
    }

    private void ResizeForCurrentDpi()
    {
        int dpi = (int)Math.Max(96u, NativeMethods.GetDpiForWindow(_hwnd));
        _appWindow.Resize(new SizeInt32(
            (int)Math.Round(LogicalWidth * dpi / 96.0),
            (int)Math.Round(LogicalHeight * dpi / 96.0)));
    }

    private void CenterOnPrimaryDisplay()
    {
        DisplayArea area = DisplayArea.Primary;
        RectInt32 work = area.WorkArea;
        SizeInt32 size = _appWindow.Size;
        _appWindow.Move(new PointInt32(
            work.X + Math.Max(0, (work.Width - size.Width) / 2),
            work.Y + Math.Max(0, (work.Height - size.Height) / 2)));
    }

    private void SwitchToTodayToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        AppSettings.SetSwitchToTodayOnOpen(SwitchToTodayToggle.IsOn);
    }

    private void MaxDotsComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing || MaxDotsComboBox.SelectedIndex < 0) return;
        AppSettings.SetMaxEventDots(Math.Clamp(MaxDotsComboBox.SelectedIndex, 0, 4));
    }

    private void GoogleAutoRefreshToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        AppSettings.SetGoogleAutoRefreshEnabled(GoogleAutoRefreshToggle.IsOn);
    }

    private void ReminderChecksToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        AppSettings.SetEventReminderChecksEnabled(ReminderChecksToggle.IsOn);
    }

    private void TrayDateIconToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        AppSettings.SetTrayDateIconUpdatesEnabled(TrayDateIconToggle.IsOn);
    }

    private void CurrentEventUpdatesToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        AppSettings.SetCurrentEventUpdatesEnabled(CurrentEventUpdatesToggle.IsOn);
    }

    private void ClickAwayDismissToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        AppSettings.SetClickAwayDismissEnabled(ClickAwayDismissToggle.IsOn);
    }

    private void BuildCalendarColorRows()
    {
        CalendarColorRows.Children.Clear();
        if (!CalendarColorLoadingRing.IsActive)
        {
            CalendarColorEmptyText.Text = "Sign in to Google Calendar to customize calendar colors.";
            CalendarColorEmptyText.Visibility = _calendars.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        foreach (CalendarDescriptor calendar in _calendars)
        {
            var row = new Grid { ColumnSpacing = 10 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var name = new TextBlock
            {
                Text = calendar.Name,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            row.Children.Add(name);

            string selectedHex = AppSettings.Current.CalendarColorOverrides.TryGetValue(calendar.Id, out string? overridden)
                ? overridden
                : calendar.BackgroundColor;

            var colorButton = new Button
            {
                Width = 44,
                Height = 30,
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(5),
                BorderThickness = new Thickness(1),
                Background = new SolidColorBrush(ParseColor(selectedHex)),
                Tag = calendar
            };
            colorButton.Click += CalendarColorButton_Click;
            Grid.SetColumn(colorButton, 1);
            row.Children.Add(colorButton);

            CalendarColorRows.Children.Add(row);
        }
    }

    private void CalendarColorButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not CalendarDescriptor calendar) return;

        string currentHex = AppSettings.Current.CalendarColorOverrides.TryGetValue(calendar.Id, out string? overridden)
            ? overridden
            : calendar.BackgroundColor;

        var picker = new ColorPicker
        {
            Color = ParseColor(currentHex),
            IsAlphaEnabled = false,
            IsColorChannelTextInputVisible = true,
            IsHexInputVisible = true,
            MinWidth = 300
        };

        var resetButton = new Button
        {
            Content = "Use Google color"
        };
        var applyButton = new Button
        {
            Content = "Apply"
        };

        var buttonRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8
        };
        buttonRow.Children.Add(resetButton);
        buttonRow.Children.Add(applyButton);

        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(picker);
        panel.Children.Add(buttonRow);

        var flyout = new Flyout { Content = panel };
        applyButton.Click += (_, _) =>
        {
            string hex = ToHex(picker.Color);
            AppSettings.SetCalendarColor(calendar.Id, hex);
            button.Background = new SolidColorBrush(picker.Color);
            flyout.Hide();
        };
        resetButton.Click += (_, _) =>
        {
            AppSettings.ResetCalendarColor(calendar.Id);
            button.Background = new SolidColorBrush(ParseColor(calendar.BackgroundColor));
            flyout.Hide();
        };

        flyout.ShowAt(button);
    }

    private static string GetDisplayVersion()
    {
        Version? version = typeof(SettingsWindow).Assembly.GetName().Version;
        if (version is null) return "Version 0.40.0";
        return $"Version {version.Major}.{version.Minor}.{Math.Max(0, version.Build)}";
    }

    private static Color ParseColor(string? value)
    {
        string hex = string.IsNullOrWhiteSpace(value) ? "#606AFF" : value.Trim();
        try
        {
            string raw = hex.TrimStart('#');
            if (raw.Length == 6)
            {
                return Microsoft.UI.ColorHelper.FromArgb(
                    255,
                    Convert.ToByte(raw[0..2], 16),
                    Convert.ToByte(raw[2..4], 16),
                    Convert.ToByte(raw[4..6], 16));
            }
        }
        catch { }

        return Microsoft.UI.ColorHelper.FromArgb(255, 96, 106, 255);
    }

    private static string ToHex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";
}
