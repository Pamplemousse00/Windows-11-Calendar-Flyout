using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Input;
using Windows.Graphics;
using Win10CalendarFlyout.Interop;
using Win10CalendarFlyout.Models;
using Win10CalendarFlyout.Services;
using WinRT;
using WinRT.Interop;

namespace Win10CalendarFlyout;

public sealed partial class MainWindow : Window
{
    // Compact Windows 11-like flyout proportions; the flyout always stays full-height.
    private const int LogicalWidth = 392;
    private const int ExpandedLogicalHeight = 720;
    private const float FlyoutSlideDistance = 180.0f;
    private static readonly TimeSpan CacheFreshness = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan BackgroundRefreshInterval = TimeSpan.FromMinutes(5);
    private const int MaxCachedMonths = 4;

    private readonly GoogleCalendarService _calendarService = new();
    private readonly NotificationSchedulerService _notificationScheduler;
    private readonly AppWindow _appWindow;
    private readonly IntPtr _hwnd;
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly Dictionary<(int Year, int Month), MonthAgendaCache> _monthCache = new();
    private readonly Dictionary<(int Year, int Month), Task> _monthLoads = new();

    private TrayIconService? _trayIcon;
    private Process? _settingsProcess;
    private DispatcherQueueTimer? _refreshTimer;
    private readonly DispatcherQueueTimer _nowMarkerTimer;
    private readonly DispatcherQueueTimer _lightDismissTimer;
    private DateTime _selectedDate = DateTime.Today;
    private bool _initialLoadAttempted;
    private bool _isWindowTransitioning;
    private bool _displayedMonthSyncQueued;
    private RectInt32 _restingBounds;
    private TaskbarEdge _taskbarEdge = TaskbarEdge.Bottom;
    private DateTime _ignoreDeactivationUntilUtc;
    private DateTime _observedToday = DateTime.Today;
    private Task? _prepareFlyoutTask;
    private bool _isPrewarming;
    private bool _flyoutOpen;
    private bool _pendingTrayToggle;
    private bool _agendaAutoScrollRequested = true;
    private bool _agendaNavigationInProgress;
    private bool _agendaNavigationLoading;
    private int _queuedAgendaDayDelta;
    private bool _suppressCalendarSelectionChanged;
    private readonly Dictionary<DateTime, CalendarViewDayItem> _realizedDayItems = new();
    private CalendarViewDayItem? _hoveredDayItem;
    private CalendarViewDisplayMode _previousCalendarDisplayMode = CalendarViewDisplayMode.Month;
    private (int Year, int Month)? _monthPickerEntryMonth;
    private (int Year, int Month)? _lastDisplayedMonth;
    private (int Year, int Month)? _selectionRequestedMonth;
    private (int Year, int Month)? _selectionRequestedHeaderBaseline;
    private DesktopAcrylicController? _acrylicController;
    private SystemBackdropConfiguration? _backdropConfiguration;
    private long _displayModeCallbackToken;
    private readonly SolidColorBrush _transparentBrush = new(Microsoft.UI.Colors.Transparent);

    public ObservableCollection<AgendaEvent> AgendaItems { get; } = new();

    public MainWindow()
    {
        InitializeComponent();

        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        _notificationScheduler = new NotificationSchedulerService(_dispatcherQueue);
        _hwnd = WindowNative.GetWindowHandle(this);
        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(_hwnd);
        _appWindow = AppWindow.GetFromWindowId(windowId);

        ConfigureFlyoutWindow();
        _calendarService.ApplyColorOverrides(AppSettings.Current.CalendarColorOverrides);
        AppSettings.Changed += AppSettings_Changed;
        ConfigureCalendar();

        _nowMarkerTimer = _dispatcherQueue.CreateTimer();
        _nowMarkerTimer.Interval = TimeSpan.FromMinutes(1);
        _nowMarkerTimer.IsRepeating = true;
        _nowMarkerTimer.Tick += (_, _) =>
        {
            HandleDateRolloverIfNeeded();
            UpdateAgendaTemporalState(autoScroll: false);
        };

        // Light-dismiss is state-driven instead of relying on delayed Activated events.
        // This makes click-away deterministic even when Explorer or another shell flyout
        // changes foreground ownership in an unusual order.
        _lightDismissTimer = _dispatcherQueue.CreateTimer();
        _lightDismissTimer.Interval = TimeSpan.FromMilliseconds(16);
        _lightDismissTimer.IsRepeating = true;
        _lightDismissTimer.Tick += (_, _) => EvaluateLightDismiss();

        Activated += MainWindow_Activated;
        _notificationScheduler.SetEnabled(AppSettings.Current.EnableEventReminderChecks);
    }

    public void AttachTrayIcon(TrayIconService trayIcon)
    {
        _trayIcon = trayIcon;
        _trayIcon.SetDateAutoUpdateEnabled(AppSettings.Current.EnableTrayDateIconUpdates);
    }

    /// <summary>
    /// Forces WinUI to create and paint the flyout surface once, off-screen, at app
    /// startup. Without this warm-up the first tray click can expose the bare HWND
    /// while XAML and Desktop Acrylic are producing their first frame.
    /// </summary>
    public Task PrepareFlyoutSurfaceAsync()
        => _prepareFlyoutTask ??= PrepareFlyoutSurfaceCoreAsync();

    private async Task PrepareFlyoutSurfaceCoreAsync()
    {
        if (_flyoutOpen) return;

        _isPrewarming = true;
        _isWindowTransitioning = true;
        try
        {
            _restingBounds = CalculateTargetBounds();
            RectInt32 parkedBounds = CalculateParkedBounds(_restingBounds);
            RootSurface.UpdateLayout();

            // Keep the prewarmed HWND just outside the taskbar edge on the same monitor so
            // XAML and the backdrop remain composed without flashing on the next open.
            _appWindow.MoveAndResize(parkedBounds);
            ApplyBorderlessWindowChrome();
            _appWindow.Show(false);
    
            // Yield the UI thread long enough for XAML measure/arrange, text rasterization
            // and the Acrylic controller to produce a real backing surface, then wait for
            // DWM to present that surface once before parking it.
            await Task.Delay(90);
            RootSurface.UpdateLayout();
            await Task.Delay(40);
            await EnsureAcrylicReadyAsync();
            NativeMethods.DwmFlush();

            // Keep the HWND alive off-screen instead of hiding it. Hiding a WinUI
            // desktop window can cause the backdrop/XAML swap chain to be reattached
            // on the next Show, which is the opaque blank frame seen in v10.
            _appWindow.MoveAndResize(parkedBounds);
            NativeMethods.DwmFlush();
        }
        finally
        {
            _isWindowTransitioning = false;
            _isPrewarming = false;
        }
    }

    /// <summary>
    /// Called at app launch while the window is still hidden. If a Google token
    /// already exists, restore it and fill the current month's cache immediately.
    /// This does not intentionally start a new browser sign-in on first-run.
    /// </summary>
    public async Task InitializeInBackgroundAsync()
    {
        if (_initialLoadAttempted) return;
        _initialLoadAttempted = true;

        if (!_calendarService.HasCredentialsFile || !_calendarService.HasStoredToken)
        {
            SetSignedOutUi();
            StatusText.Text = _calendarService.HasCredentialsFile
                ? "Google sign-in available"
                : "Google setup required";
            return;
        }

        try
        {
            await _calendarService.SignInAsync();
            SetConnectedUi();
            await EnsureMonthLoadedAsync(DateTime.Today, force: true, showProgress: false);
            StartBackgroundRefresh();
        }
        catch
        {
            // Keep startup silent because the flyout is hidden. If the saved token
            // can no longer be used, the normal Sign in button can repair it.
            SetSignedOutUi();
            StatusText.Text = "Google sign-in needed";
        }
    }

    public async void ToggleFlyout()
    {
        // TrayIconService already deduplicates Explorer's duplicate notification
        // callbacks. Do not add a second time-based debounce here: it made a tray
        // click feel delayed when it arrived just after a click-away dismissal.
        if (_isWindowTransitioning && !_isPrewarming)
        {
            _pendingTrayToggle = true;
            return;
        }

        await PrepareFlyoutSurfaceAsync();
        if (_isWindowTransitioning)
        {
            _pendingTrayToggle = true;
            return;
        }

        if (_flyoutOpen)
        {
            await HideFlyoutAnimatedAsync(returnFocusToTray: false);
            return;
        }

        await EnsureAcrylicReadyAsync();

        HandleDateRolloverIfNeeded();
        await PrimeOpenDatePreferenceAsync();
        UpdateDateHeader();
        _agendaAutoScrollRequested = true;
        DisplayAgendaFromCache();
        RefreshVisibleDayItemDots();
        QueueAdjacentAgendaMonthPrecache();
        RootSurface.UpdateLayout();

        _restingBounds = CalculateTargetBounds();
        RectInt32 entranceBounds = OffsetForTaskbar(_restingBounds, GetSlideDistancePixels());
        _appWindow.MoveAndResize(entranceBounds);
        ApplyBorderlessWindowChrome();

        _isWindowTransitioning = true;
        _flyoutOpen = true;
        _ignoreDeactivationUntilUtc = DateTime.UtcNow.AddMilliseconds(280);
        try
        {
            _appWindow.Show(true);
            NativeMethods.SetForegroundWindow(_hwnd);
            PlaceBelowTaskbarInZOrder();
            NativeMethods.DwmFlush();
            AnimateWindowBounds(entranceBounds, _restingBounds, durationMs: 285, easeOut: true);
            if (_backdropConfiguration is not null) _backdropConfiguration.IsInputActive = true;
            ApplyBorderlessWindowChrome();
            RefreshVisibleDayItemDots();
            UpdatePeriodicActivityState();
        }
        finally
        {
            _isWindowTransitioning = false;
            QueuePendingTrayToggleIfNeeded();
        }

        if (!_initialLoadAttempted)
        {
            _ = InitializeInBackgroundAsync();
        }
    }

    /// <summary>
    /// Manual refresh command from the tray menu or header button. Refreshes the
    /// current, selected, and displayed months without disturbing CalendarView scroll.
    /// </summary>
    public async Task RefreshAgendaAsync()
    {
        if (!_calendarService.IsSignedIn)
        {
            SetSignedOutUi();
            return;
        }

        RefreshButton.IsEnabled = false;
        StatusText.Text = "Refreshing...";
        try
        {
            var months = new HashSet<(int Year, int Month)>
            {
                MonthKey(DateTime.Today),
                MonthKey(_selectedDate)
            };
            if (_lastDisplayedMonth is { } displayed) months.Add(displayed);

            foreach (var month in months)
            {
                await EnsureMonthLoadedAsync(
                    new DateTime(month.Year, month.Month, 1),
                    force: true,
                    showProgress: true);
            }

            DisplayAgendaFromCache();
            RefreshVisibleDayItemDots();
        }
        finally
        {
            RefreshButton.IsEnabled = true;
        }
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
        => await RefreshAgendaAsync();

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_settingsProcess is { HasExited: false } existing)
            {
                IntPtr settingsHwnd = existing.MainWindowHandle;
                if (settingsHwnd != IntPtr.Zero)
                {
                    NativeMethods.SetForegroundWindow(settingsHwnd);
                }
                return;
            }

            string? executablePath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(executablePath)) return;

            var startInfo = new ProcessStartInfo(executablePath, "--settings")
            {
                UseShellExecute = true
            };

            Process? process = Process.Start(startInfo);
            if (process is null) return;

            _settingsProcess = process;
            _ = ObserveSettingsProcessAsync(process);
        }
        catch
        {
            _settingsProcess = null;
        }
    }

    private async Task ObserveSettingsProcessAsync(Process process)
    {
        try
        {
            await process.WaitForExitAsync();
        }
        catch
        {
            // Settings is an optional secondary process; never break the tray app.
        }
        finally
        {
            try { process.Dispose(); } catch { }

            _dispatcherQueue.TryEnqueue(() =>
            {
                if (ReferenceEquals(_settingsProcess, process))
                {
                    _settingsProcess = null;
                }

                // Settings writes to the shared JSON file from its own process.
                // Reload once that process has completely exited so the flyout picks
                // up toggles, dot count, and calendar colors without polling.
                AppSettings.ReloadFromDiskAndNotify();
            });
        }
    }

    private bool ApplyOpenDatePreferenceCore()
    {
        if (!AppSettings.Current.SwitchToTodayOnOpen) return false;

        DateTime today = DateTime.Today;
        if (_selectedDate.Date == today &&
            _lastDisplayedMonth == MonthKey(today))
        {
            return false;
        }

        DateTime localToday = DateTime.SpecifyKind(today, DateTimeKind.Unspecified);
        var todayOffset = new DateTimeOffset(localToday, TimeZoneInfo.Local.GetUtcOffset(localToday));

        MonthCalendar.SetDisplayDate(todayOffset);
        MonthCalendar.SelectedDates.Clear();
        MonthCalendar.SelectedDates.Add(todayOffset);
        _selectedDate = today;
        _lastDisplayedMonth = MonthKey(today);
        _selectionRequestedMonth = null;
        _selectionRequestedHeaderBaseline = null;
        _agendaAutoScrollRequested = true;
        return true;
    }

    private async Task PrimeOpenDatePreferenceAsync()
    {
        if (!ApplyOpenDatePreferenceCore()) return;

        // The flyout HWND stays composed while parked off-screen. Let CalendarView
        // finish the date change there so the user never sees it jump to today after
        // the entrance animation has already started.
        DisplayAgendaFromCache();
        RefreshVisibleDayItemDots();
        RootSurface.UpdateLayout();
        await WaitForNextRenderAsync();
        NativeMethods.DwmFlush();
    }

    private void AppSettings_Changed(object? sender, EventArgs e)
    {
        _calendarService.ApplyColorOverrides(AppSettings.Current.CalendarColorOverrides);

        foreach (AgendaEvent item in _monthCache.Values
                     .SelectMany(cache => cache.ByDay.Values)
                     .SelectMany(items => items)
                     .Distinct())
        {
            item.CalendarBrush = _calendarService.ResolveCalendarBrush(
                item.CalendarId,
                item.CalendarDefaultColorHex);
        }

        RefreshVisibleDayItemDots();
        DisplayAgendaFromCache();
        UpdatePeriodicActivityState();

        if (!_flyoutOpen && !_isWindowTransitioning && AppSettings.Current.SwitchToTodayOnOpen)
        {
            _ = PrimeOpenDatePreferenceAsync();
        }
    }

    public void CloseForReal()
    {
        _refreshTimer?.Stop();
        _nowMarkerTimer.Stop();
        _lightDismissTimer.Stop();
        _notificationScheduler.Dispose();
        AppSettings.Changed -= AppSettings_Changed;
        if (_settingsProcess is { HasExited: false } settingsProcess)
        {
            try { settingsProcess.Kill(entireProcessTree: true); } catch { }
        }
        try { _settingsProcess?.Dispose(); } catch { }
        _settingsProcess = null;
        _acrylicController?.Dispose();
        _acrylicController = null;
        _backdropConfiguration = null;
        Close();
    }

    private void ConfigureFlyoutWindow()
    {
        _appWindow.IsShownInSwitchers = false;

        if (_appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.IsAlwaysOnTop = true;
            presenter.SetBorderAndTitleBar(false, false);
        }

        // Use the controller form instead of Window.SystemBackdrop so we can keep
        // Acrylic in its active material state while Explorer temporarily owns input
        // during a tray click. This avoids the grey fallback-to-Acrylic transition.
        ConfigureAcrylicBackdrop();

        ApplyBorderlessWindowChrome();
        _appWindow.Hide();
    }

    private void ConfigureAcrylicBackdrop()
    {
        try
        {
            if (!DesktopAcrylicController.IsSupported())
            {
                SystemBackdrop = new DesktopAcrylicBackdrop();
                return;
            }

            _backdropConfiguration = new SystemBackdropConfiguration
            {
                IsInputActive = true,
                Theme = SystemBackdropTheme.Default
            };

            _acrylicController = new DesktopAcrylicController
            {
                Kind = DesktopAcrylicKind.Base
            };

            _acrylicController.AddSystemBackdropTarget(this.As<ICompositionSupportsSystemBackdrop>());
            _acrylicController.SetSystemBackdropConfiguration(_backdropConfiguration);
            RootSurface.ActualThemeChanged += RootSurface_ActualThemeChanged;
            UpdateBackdropTheme();
        }
        catch
        {
            _acrylicController?.Dispose();
            _acrylicController = null;
            _backdropConfiguration = null;
            SystemBackdrop = new DesktopAcrylicBackdrop();
        }
    }

    private void RootSurface_ActualThemeChanged(FrameworkElement sender, object args)
        => UpdateBackdropTheme();

    private void UpdateBackdropTheme()
    {
        if (_backdropConfiguration is null) return;

        _backdropConfiguration.Theme = RootSurface.ActualTheme switch
        {
            ElementTheme.Light => SystemBackdropTheme.Light,
            ElementTheme.Dark => SystemBackdropTheme.Dark,
            _ => SystemBackdropTheme.Default
        };

        // This flyout is transient UI. Keep the material active even while Explorer
        // briefly becomes foreground during the notification-area click itself.
        _backdropConfiguration.IsInputActive = true;
    }

    private async Task EnsureAcrylicReadyAsync()
    {
        if (_backdropConfiguration is not null)
        {
            _backdropConfiguration.IsInputActive = true;
        }

        if (_acrylicController is null) return;

        // The controller exposes whether it is currently drawing Acrylic or its solid
        // fallback. Give DWM a few presentation opportunities before beginning motion
        // so the first visible frame is already the active material whenever possible.
        for (int i = 0; i < 6 && _acrylicController.State != SystemBackdropState.Active; i++)
        {
            await WaitForNextRenderAsync();
            NativeMethods.DwmFlush();
        }
    }

    private void ApplyBorderlessWindowChrome()
    {
        try
        {
            // Strip the remaining overlapped-window frame. This removes the bright
            // one-pixel active-window border that can remain even after hiding the
            // AppWindow title bar.
            nint style = NativeMethods.GetWindowLongPtr(_hwnd, NativeMethods.GWL_STYLE);
            long styleValue = style.ToInt64();
            styleValue &= ~(NativeMethods.WS_CAPTION | NativeMethods.WS_THICKFRAME |
                            NativeMethods.WS_BORDER | NativeMethods.WS_DLGFRAME |
                            NativeMethods.WS_SYSMENU | NativeMethods.WS_MINIMIZEBOX |
                            NativeMethods.WS_MAXIMIZEBOX);
            styleValue |= NativeMethods.WS_POPUP;
            NativeMethods.SetWindowLongPtr(_hwnd, NativeMethods.GWL_STYLE, new IntPtr(styleValue));

            nint exStyle = NativeMethods.GetWindowLongPtr(_hwnd, NativeMethods.GWL_EXSTYLE);
            long exStyleValue = exStyle.ToInt64();
            exStyleValue |= NativeMethods.WS_EX_TOOLWINDOW;
            exStyleValue &= ~NativeMethods.WS_EX_APPWINDOW;
            NativeMethods.SetWindowLongPtr(_hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(exStyleValue));

            NativeMethods.SetWindowPos(
                _hwnd,
                NativeMethods.HWND_TOPMOST,
                0, 0, 0, 0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE |
                NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_FRAMECHANGED);

            // Keep the calendar in the topmost band, but directly *behind* the Windows
            // taskbar. That lets the entrance emerge from under the taskbar instead of
            // visibly sliding across the taskbar buttons/icons.
            PlaceBelowTaskbarInZOrder();

            int cornerPreference = NativeMethods.DWMWCP_ROUND;
            NativeMethods.DwmSetWindowAttribute(
                _hwnd,
                NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE,
                ref cornerPreference,
                sizeof(int));

            uint noBorder = NativeMethods.DWMWA_COLOR_NONE;
            NativeMethods.DwmSetWindowAttribute(
                _hwnd,
                NativeMethods.DWMWA_BORDER_COLOR,
                ref noBorder,
                sizeof(uint));

            // Request the Windows 11 transient-window system backdrop directly from DWM.
            // On supported builds this maps to Desktop Acrylic behind the entire HWND,
            // so DWM can establish the material before XAML's first visible frame.
            int backdropType = NativeMethods.DWMSBT_TRANSIENTWINDOW;
            NativeMethods.DwmSetWindowAttribute(
                _hwnd,
                NativeMethods.DWMWA_SYSTEMBACKDROP_TYPE,
                ref backdropType,
                sizeof(int));
        }
        catch
        {
            // These are polish-only Win32/DWM calls.
        }
    }

    private void PlaceBelowTaskbarInZOrder()
    {
        try
        {
            IntPtr taskbar = NativeMethods.FindWindow("Shell_TrayWnd", null);
            if (taskbar == IntPtr.Zero) return;

            NativeMethods.SetWindowPos(
                _hwnd,
                taskbar,
                0, 0, 0, 0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
        }
        catch
        {
            // Z-order polish only.
        }
    }

    private void ConfigureCalendar()
    {
        var today = DateTimeOffset.Now;
        MonthCalendar.SetDisplayDate(today);
        MonthCalendar.SelectedDates.Clear();
        MonthCalendar.SelectedDates.Add(today);
        _selectedDate = today.LocalDateTime.Date;
        _lastDisplayedMonth = (_selectedDate.Year, _selectedDate.Month);

        // Month view uses our custom day circles. Year/decade view should use the
        // native CalendarView today treatment so the current month/year is filled blue.
        _displayModeCallbackToken = MonthCalendar.RegisterPropertyChangedCallback(
            CalendarView.DisplayModeProperty,
            (_, _) => ApplyCalendarDisplayModeVisuals());
        ApplyCalendarDisplayModeVisuals();
        UpdateDateHeader();
    }

    private void ApplyCalendarDisplayModeVisuals()
    {
        CalendarViewDisplayMode currentMode = MonthCalendar.DisplayMode;
        CalendarViewDisplayMode previousMode = _previousCalendarDisplayMode;
        _previousCalendarDisplayMode = currentMode;
        bool monthMode = currentMode == CalendarViewDisplayMode.Month;
        MonthCalendar.IsTodayHighlighted = !monthMode;

        if (!monthMode && previousMode == CalendarViewDisplayMode.Month)
        {
            _monthPickerEntryMonth = _lastDisplayedMonth;
        }

        if (monthMode)
        {
            Brush transparent = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            // CalendarView renders its direct calendar-item background above the custom
            // CalendarViewDayItem content layer. Make every month-view background state
            // transparent so that layer cannot mask the event dots below our circles.
            MonthCalendar.CalendarItemBackground = transparent;
            MonthCalendar.OutOfScopeBackground = transparent;
            MonthCalendar.CalendarItemDisabledBackground = transparent;
            MonthCalendar.BlackoutBackground = transparent;
            MonthCalendar.TodayBlackoutBackground = transparent;
            MonthCalendar.TodayDisabledBackground = transparent;
            MonthCalendar.CalendarItemHoverBackground = transparent;
            MonthCalendar.CalendarItemPressedBackground = transparent;
            MonthCalendar.CalendarItemBorderBrush = transparent;
            MonthCalendar.CalendarItemBorderThickness = new Thickness(0);
            MonthCalendar.SelectedBorderBrush = transparent;
            MonthCalendar.SelectedHoverBorderBrush = transparent;
            MonthCalendar.SelectedPressedBorderBrush = transparent;
            MonthCalendar.TodayBackground = transparent;
            MonthCalendar.TodayHoverBackground = transparent;
            MonthCalendar.TodayPressedBackground = transparent;
            MonthCalendar.TodaySelectedInnerBorderBrush = transparent;

            // CalendarView renders its day glyph directly above the injected day-item
            // template.  Hide *all* of those direct-rendered foreground states in month
            // mode and render exactly one centered glyph in DayCellDecoration instead.
            // This avoids the state-by-state duplicate-number bugs while giving the
            // circle, number, and dot rows one deterministic layout.
            MonthCalendar.CalendarItemForeground = transparent;
            MonthCalendar.PressedForeground = transparent;
            MonthCalendar.DisabledForeground = transparent;
            MonthCalendar.BlackoutForeground = transparent;
            MonthCalendar.TodayForeground = transparent;
            MonthCalendar.TodayBlackoutForeground = transparent;
            MonthCalendar.OutOfScopeForeground = transparent;
            MonthCalendar.OutOfScopeHoverForeground = transparent;
            MonthCalendar.OutOfScopePressedForeground = transparent;
            MonthCalendar.SelectedForeground = transparent;
            MonthCalendar.SelectedHoverForeground = transparent;
            MonthCalendar.SelectedPressedForeground = transparent;
            MonthCalendar.SelectedDisabledForeground = transparent;
            QueueDisplayedMonthSync();
            // Returning from year/decade view means the user chose a month/year. Wait
            // for CalendarView's header/containers to settle, then load that month even
            // if no individual date has been selected.
            if (previousMode != CalendarViewDisplayMode.Month)
            {
                _ = SynchronizeMonthAfterPickerSelectionAsync(_monthPickerEntryMonth);
            }
            return;
        }

        // Clear the month-view overrides so the built-in year/decade visuals are restored.
        MonthCalendar.ClearValue(CalendarView.CalendarItemBackgroundProperty);
        MonthCalendar.ClearValue(CalendarView.OutOfScopeBackgroundProperty);
        MonthCalendar.ClearValue(CalendarView.CalendarItemDisabledBackgroundProperty);
        MonthCalendar.ClearValue(CalendarView.BlackoutBackgroundProperty);
        MonthCalendar.ClearValue(CalendarView.TodayBlackoutBackgroundProperty);
        MonthCalendar.ClearValue(CalendarView.TodayDisabledBackgroundProperty);
        MonthCalendar.ClearValue(CalendarView.CalendarItemHoverBackgroundProperty);
        MonthCalendar.ClearValue(CalendarView.CalendarItemPressedBackgroundProperty);
        MonthCalendar.ClearValue(CalendarView.CalendarItemBorderBrushProperty);
        MonthCalendar.ClearValue(CalendarView.CalendarItemBorderThicknessProperty);
        MonthCalendar.ClearValue(CalendarView.SelectedBorderBrushProperty);
        MonthCalendar.ClearValue(CalendarView.SelectedHoverBorderBrushProperty);
        MonthCalendar.ClearValue(CalendarView.SelectedPressedBorderBrushProperty);
        MonthCalendar.ClearValue(CalendarView.TodayBackgroundProperty);
        MonthCalendar.ClearValue(CalendarView.TodayHoverBackgroundProperty);
        MonthCalendar.ClearValue(CalendarView.TodayPressedBackgroundProperty);
        MonthCalendar.ClearValue(CalendarView.TodaySelectedInnerBorderBrushProperty);
        MonthCalendar.ClearValue(CalendarView.CalendarItemForegroundProperty);
        MonthCalendar.ClearValue(CalendarView.PressedForegroundProperty);
        MonthCalendar.ClearValue(CalendarView.DisabledForegroundProperty);
        MonthCalendar.ClearValue(CalendarView.BlackoutForegroundProperty);
        MonthCalendar.ClearValue(CalendarView.TodayForegroundProperty);
        MonthCalendar.ClearValue(CalendarView.TodayBlackoutForegroundProperty);
        MonthCalendar.ClearValue(CalendarView.OutOfScopeForegroundProperty);
        MonthCalendar.ClearValue(CalendarView.OutOfScopeHoverForegroundProperty);
        MonthCalendar.ClearValue(CalendarView.OutOfScopePressedForegroundProperty);
        MonthCalendar.ClearValue(CalendarView.SelectedForegroundProperty);
        MonthCalendar.ClearValue(CalendarView.SelectedHoverForegroundProperty);
        MonthCalendar.ClearValue(CalendarView.SelectedPressedForegroundProperty);
        MonthCalendar.ClearValue(CalendarView.SelectedDisabledForegroundProperty);

        // Force CalendarView to rebuild its native today visual after leaving month mode.
        // This restores the filled current-month/current-year marker in year/decade views.
        MonthCalendar.IsTodayHighlighted = false;
        _dispatcherQueue.TryEnqueue(() =>
        {
            if (MonthCalendar.DisplayMode != CalendarViewDisplayMode.Month)
            {
                MonthCalendar.IsTodayHighlighted = true;
            }
        });
    }

    private async Task SynchronizeMonthAfterPickerSelectionAsync((int Year, int Month)? previousMonth)
    {
        (int Year, int Month)? lastCandidate = null;

        // Picker selection updates the DisplayMode first and the header/virtualized
        // cells shortly afterward. Give it several compositor frames rather than
        // accepting the stale month that was visible before entering year view.
        for (int attempt = 0; attempt < 12; attempt++)
        {
            await WaitForNextRenderAsync();
            if (MonthCalendar.DisplayMode != CalendarViewDisplayMode.Month) return;

            if (TryGetDisplayedMonth(out int year, out int month))
            {
                var candidate = (Year: year, Month: month);
                lastCandidate = candidate;
                if (previousMonth is null || candidate != previousMonth.Value)
                {
                    CommitDisplayedMonth(candidate);
                    RefreshVisibleDayItemDots();
                    _monthPickerEntryMonth = null;
                    return;
                }
            }
        }

        // If the header was still stale, the realized cell population is a useful
        // one-shot fallback here (but is never used as a general scrolling authority).
        if (TryGetDisplayedMonthFromRealizedItems(out int realizedYear, out int realizedMonth))
        {
            var realized = (Year: realizedYear, Month: realizedMonth);
            if (previousMonth is null || realized != previousMonth.Value)
            {
                CommitDisplayedMonth(realized);
                RefreshVisibleDayItemDots();
                _monthPickerEntryMonth = null;
                return;
            }
        }

        if (lastCandidate is { } fallback)
        {
            CommitDisplayedMonth(fallback);
            RefreshVisibleDayItemDots();
        }
        _monthPickerEntryMonth = null;
    }

    private RectInt32 CalculateTargetBounds()
    {
        int dpi = (int)Math.Max(96u, NativeMethods.GetDpiForWindow(_hwnd));
        int width = LogicalWidth * dpi / 96;
        int height = ExpandedLogicalHeight * dpi / 96;
        int gap = Math.Max(6, 8 * dpi / 96);

        NativeMethods.RECT anchor;
        if (_trayIcon is not null && _trayIcon.TryGetIconRect(out anchor))
        {
            // Use the actual notification icon bounds whenever the Shell provides them.
        }
        else if (_trayIcon is not null && _trayIcon.TryGetLastActivationPoint(out NativeMethods.POINT activationPoint))
        {
            int halfAnchor = Math.Max(8, 8 * dpi / 96);
            anchor = new NativeMethods.RECT
            {
                Left = activationPoint.X - halfAnchor,
                Top = activationPoint.Y - halfAnchor,
                Right = activationPoint.X + halfAnchor,
                Bottom = activationPoint.Y + halfAnchor
            };
        }
        else
        {
            NativeMethods.GetCursorPos(out NativeMethods.POINT cursor);
            anchor = new NativeMethods.RECT
            {
                Left = cursor.X - 8,
                Top = cursor.Y - 8,
                Right = cursor.X + 8,
                Bottom = cursor.Y + 8
            };
        }

        IntPtr monitor = NativeMethods.MonitorFromRect(ref anchor, 2);
        var info = new NativeMethods.MONITORINFO
        {
            cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFO>()
        };

        if (!NativeMethods.GetMonitorInfo(monitor, ref info))
        {
            _taskbarEdge = TaskbarEdge.Bottom;
            return new RectInt32(anchor.Left - width / 2, anchor.Top - height - gap, width, height);
        }

        var work = info.rcWork;
        int iconCenterX = (anchor.Left + anchor.Right) / 2;
        int iconCenterY = (anchor.Top + anchor.Bottom) / 2;

        int distanceLeft = Math.Abs(iconCenterX - work.Left);
        int distanceRight = Math.Abs(iconCenterX - work.Right);
        int distanceTop = Math.Abs(iconCenterY - work.Top);
        int distanceBottom = Math.Abs(iconCenterY - work.Bottom);
        int nearest = Math.Min(Math.Min(distanceLeft, distanceRight), Math.Min(distanceTop, distanceBottom));

        int x;
        int y;
        if (nearest == distanceBottom)
        {
            _taskbarEdge = TaskbarEdge.Bottom;
            x = iconCenterX - width / 2;
            y = work.Bottom - height - gap;
        }
        else if (nearest == distanceTop)
        {
            _taskbarEdge = TaskbarEdge.Top;
            x = iconCenterX - width / 2;
            y = work.Top + gap;
        }
        else if (nearest == distanceRight)
        {
            _taskbarEdge = TaskbarEdge.Right;
            x = work.Right - width - gap;
            y = iconCenterY - height / 2;
        }
        else
        {
            _taskbarEdge = TaskbarEdge.Left;
            x = work.Left + gap;
            y = iconCenterY - height / 2;
        }

        x = Math.Clamp(x, work.Left, Math.Max(work.Left, work.Right - width));
        y = Math.Clamp(y, work.Top, Math.Max(work.Top, work.Bottom - height));
        return new RectInt32(x, y, width, height);
    }

    private void PositionNearTrayIcon()
    {
        _restingBounds = CalculateTargetBounds();
        _appWindow.MoveAndResize(_restingBounds);
    }

    private async Task EnsureMonthLoadedAsync(DateTime dateInMonth, bool force, bool showProgress)
    {
        if (!_calendarService.IsSignedIn) return;

        var key = MonthKey(dateInMonth);
        if (!force && _monthCache.TryGetValue(key, out MonthAgendaCache? cached) &&
            DateTime.UtcNow - cached.FetchedAtUtc < CacheFreshness)
        {
            if (key == MonthKey(_selectedDate)) DisplayAgendaFromCache();
            UpdateLoadingIndicator();
            return;
        }

        if (_monthLoads.TryGetValue(key, out Task? existingLoad))
        {
            UpdateLoadingIndicator();
            await existingLoad;
            UpdateLoadingIndicator();
            return;
        }

        Task load = LoadMonthCoreAsync(key, showProgress);
        _monthLoads[key] = load;
        UpdateLoadingIndicator();

        try
        {
            await load;
        }
        finally
        {
            _monthLoads.Remove(key);
            UpdateLoadingIndicator();
        }
    }

    private async Task LoadMonthCoreAsync((int Year, int Month) key, bool showProgress)
    {
        bool hadCache = _monthCache.ContainsKey(key);
        if (showProgress && !hadCache)
        {
            StatusText.Text = "Updating month...";
        }

        try
        {
            DateTime monthStart = new(key.Year, key.Month, 1);
            DateTime monthEnd = monthStart.AddMonths(1);
            IReadOnlyList<AgendaEvent> events = await _calendarService.GetEventsForRangeAsync(monthStart, monthEnd);

            var byDay = new Dictionary<DateTime, List<AgendaEvent>>();
            for (DateTime day = monthStart; day < monthEnd; day = day.AddDays(1))
            {
                byDay[day.Date] = events
                    .Where(item => OccursOnDate(item, day.Date))
                    .OrderBy(item => item.IsAllDay ? 0 : 1)
                    .ThenBy(item => item.Start ?? DateTimeOffset.MinValue)
                    .ThenBy(item => item.Title, StringComparer.CurrentCultureIgnoreCase)
                    .ToList();
            }

            _monthCache[key] = new MonthAgendaCache(DateTime.UtcNow, byDay);
            TrimMonthCache();

            // Mirror Google's popup reminders into Windows scheduled notifications.
            // Replacing a month's schedule also removes stale reminders after an edit.
            _notificationScheduler.ReplaceMonthSchedule(key.Year, key.Month, events);

            RefreshVisibleDayItemDots();

            if (key == MonthKey(_selectedDate))
            {
                DisplayAgendaFromCache();
            }
        }
        catch (Exception ex)
        {
            // Keep stale cached data visible if a background refresh fails.
            if (!hadCache)
            {
                StatusText.Text = "Could not load Google Calendar";
                if (_flyoutOpen && showProgress)
                {
                    await ShowErrorAsync("Calendar refresh failed", ex.Message);
                }
            }
        }
        finally
        {
            UpdateLoadingIndicator();
        }
    }

    private static bool OccursOnDate(AgendaEvent item, DateTime date)
    {
        date = date.Date;

        if (item.IsAllDay)
        {
            DateTime? start = item.AllDayStartDate?.Date;
            DateTime? endExclusive = item.AllDayEndDateExclusive?.Date;
            return start.HasValue && endExclusive.HasValue && date >= start.Value && date < endExclusive.Value;
        }

        if (!item.Start.HasValue) return false;

        DateTime dayStartLocal = DateTime.SpecifyKind(date, DateTimeKind.Unspecified);
        DateTime nextDayLocal = dayStartLocal.AddDays(1);
        var dayStart = new DateTimeOffset(dayStartLocal, TimeZoneInfo.Local.GetUtcOffset(dayStartLocal));
        var dayEnd = new DateTimeOffset(nextDayLocal, TimeZoneInfo.Local.GetUtcOffset(nextDayLocal));

        DateTimeOffset startTime = item.Start.Value;
        DateTimeOffset endTime = item.End ?? startTime.AddMinutes(1);
        return startTime < dayEnd && endTime > dayStart;
    }

    private void DisplayAgendaFromCache()
    {
        UpdateDateHeader();

        var key = MonthKey(_selectedDate);
        List<AgendaEvent>? items = null;
        bool hasSelectedDayCache = _monthCache.TryGetValue(key, out MonthAgendaCache? cache) &&
                                   cache.ByDay.TryGetValue(_selectedDate.Date, out items);

        if (hasSelectedDayCache && items is not null)
        {
            ReplaceAgendaItemsWithoutAnimation(items);
            EmptyAgendaPanel.Visibility = AgendaItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            StatusText.Text = AgendaItems.Count == 1 ? "1 event" : $"{AgendaItems.Count} events";
        }
        else
        {
            ReplaceAgendaItemsWithoutAnimation(Array.Empty<AgendaEvent>());
            EmptyAgendaPanel.Visibility = Visibility.Collapsed;
            if (_calendarService.IsSignedIn)
            {
                StatusText.Text = "Updating month...";

                // Date-to-date navigation remains cache-only for already loaded months.
                // If the user has just navigated into a genuinely uncached month, start
                // exactly one month fetch and show the agenda spinner while it completes.
                if (!_monthLoads.ContainsKey(key))
                {
                    _ = EnsureMonthLoadedAsync(_selectedDate, force: false, showProgress: true);
                }
            }
        }

        if (_calendarService.IsSignedIn)
        {
            SetConnectedUi();
        }

        UpdateLoadingIndicator();

        // Only consume the auto-scroll request after the selected day's cached data is
        // actually available. If a month is loading, the request survives until the
        // Google fetch completes and DisplayAgendaFromCache runs again.
        if (hasSelectedDayCache)
        {
            bool shouldAutoScroll = _agendaAutoScrollRequested;
            _agendaAutoScrollRequested = false;
            UpdateAgendaTemporalState(shouldAutoScroll);
        }
    }

    private void UpdateLoadingIndicator()
    {
        bool selectedMonthLoading = _monthLoads.ContainsKey(MonthKey(_selectedDate));
        bool displayedMonthLoading = false;
        if (TryGetDisplayedMonth(out int displayYear, out int displayMonth))
        {
            displayedMonthLoading = _monthLoads.ContainsKey((displayYear, displayMonth));
        }

        bool isLoading = _calendarService.IsSignedIn && (_agendaNavigationLoading || selectedMonthLoading || displayedMonthLoading);
        LoadingRing.IsActive = isLoading;
        LoadingRing.Visibility = isLoading ? Visibility.Visible : Visibility.Collapsed;
        Canvas.SetZIndex(LoadingRing, isLoading ? 20 : 0);

        if (_agendaNavigationLoading)
        {
            // Month-boundary arrow navigation should never look frozen. Keep the
            // agenda surface stable and put an unmistakable spinner above it until
            // the target month cache is ready.
            AgendaList.Opacity = 0.28;
            EmptyAgendaPanel.Visibility = Visibility.Collapsed;
        }
        else
        {
            AgendaList.Opacity = 1.0;
            if (isLoading && !_monthCache.ContainsKey(MonthKey(_selectedDate)))
            {
                EmptyAgendaPanel.Visibility = Visibility.Collapsed;
            }
        }
    }

    private void HandleDateRolloverIfNeeded()
    {
        DateTime today = DateTime.Today;
        if (today == _observedToday) return;

        DateTime previousToday = _observedToday;
        _observedToday = today;

        // Native CalendarView today styling is disabled; only yesterday and today need
        // their custom decoration refreshed at midnight.
        RefreshRealizedDayItem(previousToday);
        RefreshRealizedDayItem(today);
        UpdateDateHeader();

        // If the flyout was simply following today (the user had not intentionally
        // selected another date), advance the selection too. Otherwise preserve the
        // user's explicit selection across midnight.
        if (_selectedDate.Date == previousToday)
        {
            _selectedDate = today;
            MonthCalendar.SelectedDates.Clear();
            MonthCalendar.SelectedDates.Add(new DateTimeOffset(today));
            MonthCalendar.SetDisplayDate(new DateTimeOffset(today));
            _agendaAutoScrollRequested = true;
            DisplayAgendaFromCache();
        }

        if (_calendarService.IsSignedIn)
        {
            _ = EnsureMonthLoadedAsync(today, force: false, showProgress: false);
        }
    }

    private void UpdateAgendaTemporalState(bool autoScroll)
    {
        foreach (AgendaEvent item in AgendaItems)
        {
            item.NowIndicatorVisibility = Visibility.Collapsed;
        }

        if (_selectedDate.Date != DateTime.Today || AgendaItems.Count == 0)
        {
            return;
        }

        DateTimeOffset now = DateTimeOffset.Now;
        List<AgendaEvent> timed = AgendaItems
            .Where(item => !item.IsAllDay && item.Start.HasValue)
            .OrderBy(item => item.Start)
            .ToList();

        if (timed.Count == 0) return;

        AgendaEvent? active = timed.FirstOrDefault(item =>
        {
            DateTimeOffset start = item.Start!.Value;
            DateTimeOffset end = item.End ?? start.AddMinutes(1);
            return start <= now && end > now;
        });

        // If nothing is in progress, mark the next event when it is close enough to be
        // useful at a glance. Fifteen minutes mirrors the familiar "coming up" window.
        AgendaEvent? soon = active is null
            ? timed.FirstOrDefault(item => item.Start!.Value > now && item.Start.Value <= now.AddMinutes(15))
            : null;
        AgendaEvent? indicator = active ?? soon;
        if (indicator is not null)
        {
            indicator.NowIndicatorVisibility = Visibility.Visible;
        }

        if (!autoScroll) return;

        AgendaEvent? scrollTarget = active
            ?? timed.FirstOrDefault(item => item.Start!.Value >= now)
            ?? timed.LastOrDefault();
        if (scrollTarget is null) return;

        _dispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                AgendaList.UpdateLayout();
                AgendaList.ScrollIntoView(scrollTarget, ScrollIntoViewAlignment.Leading);
            }
            catch
            {
                // Agenda positioning is polish only; never break the flyout over it.
            }
        });
    }

    private void ReplaceAgendaItemsWithoutAnimation(IReadOnlyList<AgendaEvent> items)
    {
        bool same = AgendaItems.Count == items.Count;
        if (same)
        {
            for (int i = 0; i < items.Count; i++)
            {
                if (!AgendaItemEquivalent(AgendaItems[i], items[i]))
                {
                    same = false;
                    break;
                }
            }
        }

        if (same) return;

        AgendaItems.Clear();
        foreach (AgendaEvent item in items)
        {
            AgendaItems.Add(item);
        }
    }

    private static bool AgendaItemEquivalent(AgendaEvent left, AgendaEvent right)
    {
        return left.Title == right.Title &&
               left.CalendarName == right.CalendarName &&
               left.TimeText == right.TimeText &&
               left.HtmlLink == right.HtmlLink &&
               left.Start == right.Start &&
               left.End == right.End &&
               left.AllDayStartDate == right.AllDayStartDate &&
               left.AllDayEndDateExclusive == right.AllDayEndDateExclusive &&
               left.IsAllDay == right.IsAllDay;
    }


    private void StartBackgroundRefresh()
    {
        if (!AppSettings.Current.EnableGoogleAutoRefresh || !_calendarService.IsSignedIn)
        {
            _refreshTimer?.Stop();
            return;
        }

        if (_refreshTimer is null)
        {
            _refreshTimer = _dispatcherQueue.CreateTimer();
            _refreshTimer.Interval = BackgroundRefreshInterval;
            _refreshTimer.IsRepeating = true;
            _refreshTimer.Tick += async (_, _) =>
            {
                if (!AppSettings.Current.EnableGoogleAutoRefresh || !_calendarService.IsSignedIn) return;

                // Keep only the months that can affect what the user sees right now fresh.
                // Older cached months remain available instantly but are refreshed on demand.
                var refreshMonths = new HashSet<(int Year, int Month)>
                {
                    MonthKey(DateTime.Today),
                    MonthKey(_selectedDate)
                };
                if (_lastDisplayedMonth is { } displayed) refreshMonths.Add(displayed);

                foreach (var key in refreshMonths)
                {
                    await EnsureMonthLoadedAsync(new DateTime(key.Year, key.Month, 1), force: true, showProgress: false);
                }
                TrimMonthCache();
            };
        }

        if (!_refreshTimer.IsRunning) _refreshTimer.Start();
    }

    private void UpdatePeriodicActivityState()
    {
        if (AppSettings.Current.EnableGoogleAutoRefresh && _calendarService.IsSignedIn)
        {
            StartBackgroundRefresh();
        }
        else
        {
            _refreshTimer?.Stop();
        }

        _notificationScheduler.SetEnabled(AppSettings.Current.EnableEventReminderChecks);
        _trayIcon?.SetDateAutoUpdateEnabled(AppSettings.Current.EnableTrayDateIconUpdates);

        if (_flyoutOpen && AppSettings.Current.EnableCurrentEventUpdates)
        {
            if (!_nowMarkerTimer.IsRunning) _nowMarkerTimer.Start();
        }
        else
        {
            _nowMarkerTimer.Stop();
        }

        if (_flyoutOpen && AppSettings.Current.EnableClickAwayDismiss)
        {
            if (!_lightDismissTimer.IsRunning) _lightDismissTimer.Start();
        }
        else
        {
            _lightDismissTimer.Stop();
        }
    }

    private void TrimMonthCache()
    {
        if (_monthCache.Count <= MaxCachedMonths) return;

        var keep = new HashSet<(int Year, int Month)>
        {
            MonthKey(DateTime.Today),
            MonthKey(_selectedDate)
        };
        if (_lastDisplayedMonth is { } displayed) keep.Add(displayed);

        static int MonthIndex((int Year, int Month) key) => key.Year * 12 + key.Month;
        int selectedIndex = MonthIndex(MonthKey(_selectedDate));

        foreach (var key in _monthCache.Keys
                     .Where(key => !keep.Contains(key))
                     .OrderBy(key => Math.Abs(MonthIndex(key) - selectedIndex))
                     .ThenByDescending(key => _monthCache[key].FetchedAtUtc))
        {
            if (keep.Count >= MaxCachedMonths) break;
            keep.Add(key);
        }

        foreach (var key in _monthCache.Keys.Where(key => !keep.Contains(key)).ToList())
        {
            _monthCache.Remove(key);
            _notificationScheduler.RemoveMonthSchedule(key.Year, key.Month);
        }
    }

    private void SetSignedOutUi()
    {
        AgendaItems.Clear();
        EmptyAgendaPanel.Visibility = Visibility.Collapsed;
        SignInButton.Content = "Sign in to Google";
        SignInButton.IsEnabled = true;
        SignInButton.Visibility = Visibility.Visible;
    }

    private void SetConnectedUi()
    {
        // Once connected, the button is redundant and steals useful agenda width.
        SignInButton.IsEnabled = false;
        SignInButton.Visibility = Visibility.Collapsed;
    }

    private void UpdateDateHeader()
    {
        DateTime today = DateTime.Today;
        HeaderDateText.Text = today.ToString("dddd, d MMMM");

        DateTime selected = _selectedDate.Date;
        AgendaDateText.Text = selected == today
            ? "Today"
            : selected.ToString("dddd, MMMM d");
    }

    private int GetSlideDistancePixels()
    {
        int dpi = (int)Math.Max(96u, NativeMethods.GetDpiForWindow(_hwnd));
        return Math.Max(1, (int)Math.Round(FlyoutSlideDistance * dpi / 96.0));
    }

    private RectInt32 CalculateParkedBounds(RectInt32 restingBounds)
    {
        int dpi = (int)Math.Max(96u, NativeMethods.GetDpiForWindow(_hwnd));
        int extra = Math.Max(48, 64 * dpi / 96);

        // Keep the composed HWND adjacent to the same monitor/taskbar rather than at
        // (-32000,-32000). Desktop Acrylic is then much less likely to fall back and
        // re-acquire its capture source when the flyout is opened again.
        return _taskbarEdge switch
        {
            TaskbarEdge.Top => new RectInt32(
                restingBounds.X, restingBounds.Y - restingBounds.Height - extra,
                restingBounds.Width, restingBounds.Height),
            TaskbarEdge.Right => new RectInt32(
                restingBounds.X + restingBounds.Width + extra, restingBounds.Y,
                restingBounds.Width, restingBounds.Height),
            TaskbarEdge.Left => new RectInt32(
                restingBounds.X - restingBounds.Width - extra, restingBounds.Y,
                restingBounds.Width, restingBounds.Height),
            _ => new RectInt32(
                restingBounds.X, restingBounds.Y + restingBounds.Height + extra,
                restingBounds.Width, restingBounds.Height)
        };
    }

    private RectInt32 OffsetForTaskbar(RectInt32 bounds, int distance)
    {
        return _taskbarEdge switch
        {
            TaskbarEdge.Top => new RectInt32(bounds.X, bounds.Y - distance, bounds.Width, bounds.Height),
            TaskbarEdge.Right => new RectInt32(bounds.X + distance, bounds.Y, bounds.Width, bounds.Height),
            TaskbarEdge.Left => new RectInt32(bounds.X - distance, bounds.Y, bounds.Width, bounds.Height),
            _ => new RectInt32(bounds.X, bounds.Y + distance, bounds.Width, bounds.Height)
        };
    }

    private Task WaitForNextRenderAsync()
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<object>? handler = null;
        handler = (_, _) =>
        {
            CompositionTarget.Rendering -= handler;
            completion.TrySetResult(true);
        };
        CompositionTarget.Rendering += handler;
        return completion.Task;
    }

    private void AnimateWindowBounds(RectInt32 from, RectInt32 to, int durationMs, bool easeOut)
    {
        if (durationMs <= 0)
        {
            SetWindowPositionDirect(to.X, to.Y);
            return;
        }

        // Keep the HWND move + DWM present cycle synchronous. This is the v28 path
        // that kept Desktop Acrylic active throughout the flyout motion on the
        // target Windows 11 machine. Yielding the UI thread between these frames
        // allowed the system-backdrop surface to fall back to the opaque base layer.
        var stopwatch = Stopwatch.StartNew();
        while (true)
        {
            double linear = Math.Clamp(stopwatch.Elapsed.TotalMilliseconds / durationMs, 0.0, 1.0);
            double eased = easeOut
                ? 1.0 - Math.Pow(1.0 - linear, 3.0)
                : Math.Pow(linear, 3.0);

            SetWindowPositionDirect(
                LerpInt(from.X, to.X, eased),
                LerpInt(from.Y, to.Y, eased));
            NativeMethods.DwmFlush();

            if (linear >= 1.0) break;
        }

        SetWindowPositionDirect(to.X, to.Y);
    }

    private void SetWindowPositionDirect(int x, int y)
    {
        NativeMethods.SetWindowPos(
            _hwnd,
            IntPtr.Zero,
            x,
            y,
            0,
            0,
            NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_NOZORDER);
    }


    private static int LerpInt(int from, int to, double t)
        => (int)Math.Round(from + ((to - from) * t));


    private async Task HideFlyoutAnimatedAsync(bool returnFocusToTray = false)
    {
        if (!_flyoutOpen || _isWindowTransitioning) return;
        _isWindowTransitioning = true;
        _flyoutOpen = false;
        _nowMarkerTimer.Stop();
        _lightDismissTimer.Stop();

        try
        {
            _restingBounds = CalculateTargetBounds();
            RectInt32 exitBounds = OffsetForTaskbar(_restingBounds, GetSlideDistancePixels());

            // CalendarView interaction can cause the topmost band to be reordered.
            // Reassert our position directly behind the taskbar before the exit motion
            // so dismissal never slides across the taskbar after selecting a date.
            PlaceBelowTaskbarInZOrder();
            AnimateWindowBounds(_restingBounds, exitBounds, durationMs: 225, easeOut: false);

            _appWindow.MoveAndResize(CalculateParkedBounds(_restingBounds));
            NativeMethods.DwmFlush();

            if (returnFocusToTray)
            {
                _trayIcon?.ReturnFocusToTray();
            }
        }
        finally
        {
            _isWindowTransitioning = false;
            QueuePendingTrayToggleIfNeeded();
        }

        await Task.CompletedTask;
    }

    private void QueuePendingTrayToggleIfNeeded()
    {
        if (!_pendingTrayToggle || _isWindowTransitioning) return;
        _pendingTrayToggle = false;
        _dispatcherQueue.TryEnqueue(ToggleFlyout);
    }

    private void MainWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        if (_backdropConfiguration is not null)
        {
            _backdropConfiguration.IsInputActive = true;
        }

        if (args.WindowActivationState != WindowActivationState.Deactivated)
        {
            ApplyBorderlessWindowChrome();
            return;
        }

        if (AppSettings.Current.EnableClickAwayDismiss)
        {
            EvaluateLightDismiss();
        }
    }

    private void EvaluateLightDismiss()
    {
        if (!AppSettings.Current.EnableClickAwayDismiss) return;
        if (_isPrewarming || !_flyoutOpen || _isWindowTransitioning) return;
        if (DateTime.UtcNow < _ignoreDeactivationUntilUtc) return;
        if (IsScreenCaptureForeground()) return;

        IntPtr foreground = NativeMethods.GetForegroundWindow();
        if (foreground == IntPtr.Zero || foreground == _hwnd) return;

        // Explorer receives foreground first when the user clicks our own tray icon.
        // In that one case the tray NIN_SELECT callback exclusively owns the toggle.
        if (IsShellForeground() && IsPointerOverTrayIcon()) return;

        _ = HideFlyoutAnimatedAsync(returnFocusToTray: false);
    }

    private bool IsPointerOverTrayIcon()
    {
        if (_trayIcon is null) return false;

        int dpi = (int)Math.Max(96u, NativeMethods.GetDpiForWindow(_hwnd));
        int margin = Math.Max(8, 12 * dpi / 96);

        if (_trayIcon.TryGetIconRect(out NativeMethods.RECT rect))
        {
            // GetCursorPos is useful when the activation event is delivered immediately.
            if (NativeMethods.GetCursorPos(out NativeMethods.POINT cursor) &&
                IsPointInsideExpandedRect(cursor, rect, margin))
            {
                return true;
            }

            // More importantly, GetMessagePos reports the pointer coordinates attached to
            // the message that caused activation/deactivation. This remains useful even if
            // the physical mouse has already moved away before WinUI raises Activated.
            uint packed = NativeMethods.GetMessagePos();
            if (packed != uint.MaxValue)
            {
                var messagePoint = new NativeMethods.POINT
                {
                    X = unchecked((short)(packed & 0xFFFF)),
                    Y = unchecked((short)((packed >> 16) & 0xFFFF))
                };

                if (IsPointInsideExpandedRect(messagePoint, rect, margin))
                {
                    return true;
                }
            }
        }

        // Shell_NotifyIconGetRect can transiently fail while Explorer rearranges the
        // notification area. Fall back to the last NIN_SELECT anchor.
        if (_trayIcon.TryGetLastActivationPoint(out NativeMethods.POINT anchor) &&
            NativeMethods.GetCursorPos(out NativeMethods.POINT current))
        {
            int radius = Math.Max(18, 24 * dpi / 96);
            return Math.Abs(current.X - anchor.X) <= radius && Math.Abs(current.Y - anchor.Y) <= radius;
        }

        return false;
    }

    private static bool IsPointInsideExpandedRect(NativeMethods.POINT point, NativeMethods.RECT rect, int margin)
        => point.X >= rect.Left - margin && point.X <= rect.Right + margin &&
           point.Y >= rect.Top - margin && point.Y <= rect.Bottom + margin;

    private bool IsShellForeground()
    {
        try
        {
            IntPtr foreground = NativeMethods.GetForegroundWindow();
            if (foreground == IntPtr.Zero || foreground == _hwnd) return false;

            NativeMethods.GetWindowThreadProcessId(foreground, out uint processId);
            if (processId == 0) return false;

            using Process process = Process.GetProcessById((int)processId);
            string name = process.ProcessName;
            return name.Equals("explorer", StringComparison.OrdinalIgnoreCase) ||
                   name.Equals("ShellExperienceHost", StringComparison.OrdinalIgnoreCase) ||
                   name.Equals("StartMenuExperienceHost", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private bool IsScreenCaptureForeground()
    {
        try
        {
            IntPtr foreground = NativeMethods.GetForegroundWindow();
            if (foreground == IntPtr.Zero || foreground == _hwnd) return false;

            NativeMethods.GetWindowThreadProcessId(foreground, out uint processId);
            if (processId == 0) return false;

            using Process process = Process.GetProcessById((int)processId);
            string name = process.ProcessName;
            return name.Equals("SnippingTool", StringComparison.OrdinalIgnoreCase) ||
                   name.Equals("ScreenClippingHost", StringComparison.OrdinalIgnoreCase) ||
                   name.Equals("SnipAndSketch", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private void MonthCalendar_SelectedDatesChanged(CalendarView sender, CalendarViewSelectedDatesChangedEventArgs args)
    {
        if (_suppressCalendarSelectionChanged) return;
        if (sender.SelectedDates.Count == 0) return;

        DateTime previousSelected = _selectedDate.Date;
        _selectedDate = sender.SelectedDates[0].LocalDateTime.Date;
        _agendaAutoScrollRequested = true;

        // Only the previous and newly selected realized cells need visual updates.
        // Avoid rebuilding the rest of CalendarView on every click.
        RefreshRealizedDayItem(previousSelected);
        RefreshRealizedDayItem(_selectedDate);
        DisplayAgendaFromCache();

        // A click on a spillover date changes the visual focus/data scope to that
        // month WITHOUT calling CalendarView.SetDisplayDate(). SetDisplayDate scrolls
        // the control to the start of the target month, which caused the visible hitch
        // and destroyed the user's current scroll position in v22.
        var selectedMonth = MonthKey(_selectedDate);
        if (sender.DisplayMode == CalendarViewDisplayMode.Month &&
            _lastDisplayedMonth is { } displayedMonth &&
            displayedMonth != selectedMonth)
        {
            _selectionRequestedMonth = selectedMonth;
            _selectionRequestedHeaderBaseline = TryGetDisplayedMonth(out int baselineYear, out int baselineMonth)
                ? (baselineYear, baselineMonth)
                : displayedMonth;
            CommitDisplayedMonth(selectedMonth);
        }

        if (_calendarService.IsSignedIn &&
            !_monthCache.ContainsKey(selectedMonth) &&
            !_monthLoads.ContainsKey(selectedMonth))
        {
            _ = EnsureMonthLoadedAsync(_selectedDate, force: false, showProgress: true);
        }

        QueueDisplayedMonthSync();
        QueueAdjacentAgendaMonthPrecache();
    }

    private void MonthCalendar_CalendarViewDayItemChanging(CalendarView sender, CalendarViewDayItemChangingEventArgs args)
    {
        RemoveRealizedDayItem(args.Item);

        if (args.InRecycleQueue)
        {
            if (ReferenceEquals(_hoveredDayItem, args.Item))
            {
                _hoveredDayItem = null;
            }
            DetachDayItemPointerHandlers(args.Item);
            args.Item.Tag = null;
            return;
        }

        DateTime date = args.Item.Date.LocalDateTime.Date;
        _realizedDayItems[date] = args.Item;
        AttachDayItemPointerHandlers(args.Item);
        UpdateDayItemDots(args.Item);

        // A month scroll can realize dozens of cells. Defer the header/month-cache work
        // once per batch instead of doing it once for every recycled day item.
        QueueDisplayedMonthSync();
    }

    private void AttachDayItemPointerHandlers(CalendarViewDayItem dayItem)
    {
        DetachDayItemPointerHandlers(dayItem);
        dayItem.PointerEntered += DayItem_PointerEntered;
        dayItem.PointerExited += DayItem_PointerExited;
    }

    private void DetachDayItemPointerHandlers(CalendarViewDayItem dayItem)
    {
        dayItem.PointerEntered -= DayItem_PointerEntered;
        dayItem.PointerExited -= DayItem_PointerExited;
    }

    private void DayItem_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not CalendarViewDayItem dayItem) return;

        if (_hoveredDayItem is not null && !ReferenceEquals(_hoveredDayItem, dayItem))
        {
            CalendarViewDayItem old = _hoveredDayItem;
            _hoveredDayItem = null;
            UpdateDayItemDots(old);
        }

        _hoveredDayItem = dayItem;
        UpdateDayItemDots(dayItem);
    }

    private void DayItem_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not CalendarViewDayItem dayItem) return;
        if (ReferenceEquals(_hoveredDayItem, dayItem)) _hoveredDayItem = null;
        UpdateDayItemDots(dayItem);
    }

    private void RemoveRealizedDayItem(CalendarViewDayItem dayItem)
    {
        DateTime? keyToRemove = null;
        foreach ((DateTime key, CalendarViewDayItem value) in _realizedDayItems)
        {
            if (ReferenceEquals(value, dayItem))
            {
                keyToRemove = key;
                break;
            }
        }

        if (keyToRemove.HasValue)
        {
            _realizedDayItems.Remove(keyToRemove.Value);
        }
    }

    private void MonthCalendar_LayoutUpdated(object? sender, object e)
    {
        if (MonthCalendar.DisplayMode != CalendarViewDisplayMode.Month) return;
        if (!TryGetDisplayedMonth(out int year, out int month)) return;

        ApplyHeaderMonthIfAppropriate((Year: year, Month: month));
    }

    private void QueueDisplayedMonthSync()
    {
        if (_displayedMonthSyncQueued) return;
        _displayedMonthSyncQueued = true;

        _dispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                await WaitForNextRenderAsync();
                if (MonthCalendar.DisplayMode != CalendarViewDisplayMode.Month) return;

                if (TryGetDisplayedMonth(out int year, out int month))
                {
                    ApplyHeaderMonthIfAppropriate((Year: year, Month: month));
                }
            }
            finally
            {
                _displayedMonthSyncQueued = false;
            }
        });
    }

    private void ApplyHeaderMonthIfAppropriate((int Year, int Month) headerMonth)
    {
        if (_selectionRequestedMonth is { } selectedFocus)
        {
            // Immediately after clicking an adjacent-month cell the CalendarView header
            // still names the old month. Keep the selected month visually focused while
            // the user remains at the same scroll position. A genuine subsequent header
            // change (mouse wheel, arrows, touch scrolling) releases this override.
            if (headerMonth == selectedFocus)
            {
                _selectionRequestedMonth = null;
                _selectionRequestedHeaderBaseline = null;
                CommitDisplayedMonth(headerMonth);
                return;
            }

            if (_selectionRequestedHeaderBaseline is { } baseline && headerMonth == baseline)
            {
                return;
            }

            _selectionRequestedMonth = null;
            _selectionRequestedHeaderBaseline = null;
        }

        CommitDisplayedMonth(headerMonth);
    }

    private void CommitDisplayedMonth((int Year, int Month) displayed)
    {
        bool changed = _lastDisplayedMonth != displayed;
        _lastDisplayedMonth = displayed;

        if (changed)
        {
            TrimMonthCache();
            RefreshVisibleDayItemDots();
            UpdateLoadingIndicator();
        }

        if (_calendarService.IsSignedIn &&
            !_monthCache.ContainsKey(displayed) &&
            !_monthLoads.ContainsKey(displayed))
        {
            _ = EnsureMonthLoadedAsync(
                new DateTime(displayed.Year, displayed.Month, 1),
                force: false,
                showProgress: true);
        }
    }

    private void RefreshVisibleDayItemDots()
    {
        foreach (CalendarViewDayItem dayItem in _realizedDayItems.Values.ToArray())
        {
            UpdateDayItemDots(dayItem);
        }
    }

    private void RefreshRealizedDayItem(DateTime date)
    {
        if (_realizedDayItems.TryGetValue(date.Date, out CalendarViewDayItem? dayItem))
        {
            UpdateDayItemDots(dayItem);
        }
    }

    private void UpdateDayItemDots(CalendarViewDayItem dayItem)
    {
        DateTime date = dayItem.Date.LocalDateTime.Date;
        bool inDisplayedMonth = MonthCalendar.DisplayMode == CalendarViewDisplayMode.Month && IsDateInDisplayedMonth(date);
        bool isToday = date == DateTime.Today;
        bool isSelected = date == _selectedDate.Date;
        bool isHovered = ReferenceEquals(_hoveredDayItem, dayItem);

        Brush accent = GetThemeBrush("AccentFillColorDefaultBrush", Microsoft.UI.Colors.DeepSkyBlue);
        Brush hoverFill = isHovered
            ? GetThemeBrush("SubtleFillColorSecondaryBrush", GetThemeBrush("FlyoutHoverBrush", _transparentBrush))
            : _transparentBrush;
        Brush transparent = _transparentBrush;

        Brush? dot1 = null;
        Brush? dot2 = null;
        Brush? dot3 = null;
        Brush? dot4 = null;
        int dotCount = 0;
        if (inDisplayedMonth &&
            _monthCache.TryGetValue(MonthKey(date), out MonthAgendaCache? cache) &&
            cache.ByDay.TryGetValue(date, out List<AgendaEvent>? events) &&
            events.Count > 0)
        {
            dotCount = Math.Min(AppSettings.Current.MaxEventDots, Math.Min(4, events.Count));
            if (dotCount > 0) dot1 = events[0].CalendarBrush ?? accent;
            if (dotCount > 1) dot2 = events[1].CalendarBrush ?? accent;
            if (dotCount > 2) dot3 = events[2].CalendarBrush ?? accent;
            if (dotCount > 3) dot4 = events[3].CalendarBrush ?? accent;
        }

        Brush dayForeground = isToday
            ? GetThemeBrush("TextOnAccentFillColorPrimaryBrush", Microsoft.UI.Colors.Black)
            : inDisplayedMonth
                ? GetThemeBrush("TextFillColorPrimaryBrush", Microsoft.UI.Colors.White)
                : GetThemeBrush("FlyoutOutOfScopeDayBrush", Microsoft.UI.ColorHelper.FromArgb(112, 255, 255, 255));

        // Keep the day item itself fully opaque so dots/calendar colors are never dimmed.
        // Scope dimming belongs only to the custom date glyph.
        dayItem.Opacity = 1.0;

        dayItem.Tag = new DayCellDecoration
        {
            DayText = date.Day.ToString(CultureInfo.CurrentCulture),
            DayForeground = dayForeground,
            HoverFill = hoverFill,
            TodayFill = isToday ? accent : transparent,
            SelectionStroke = isSelected ? accent : transparent,
            SelectionStrokeThickness = isSelected ? 1.5 : 0.0,
            Dot1 = dot1,
            Dot2 = dot2,
            Dot3 = dot3,
            Dot4 = dot4,
            Dot1Visibility = dotCount > 0 ? Visibility.Visible : Visibility.Collapsed,
            Dot2Visibility = dotCount > 1 ? Visibility.Visible : Visibility.Collapsed,
            Dot3Visibility = dotCount > 2 ? Visibility.Visible : Visibility.Collapsed,
            Dot4Visibility = dotCount > 3 ? Visibility.Visible : Visibility.Collapsed
        };
    }

    private static Brush GetThemeBrush(string key, Windows.UI.Color fallbackColor)
    {
        try
        {
            if (Application.Current.Resources[key] is Brush brush)
            {
                return brush;
            }
        }
        catch { }

        return new SolidColorBrush(fallbackColor);
    }

    private static Brush GetThemeBrush(string key, Brush? fallback)
    {
        try
        {
            if (Application.Current.Resources[key] is Brush brush)
            {
                return brush;
            }
        }
        catch { }

        return fallback ?? new SolidColorBrush(Microsoft.UI.Colors.White);
    }

    private bool IsDateInDisplayedMonth(DateTime date)
    {
        // _lastDisplayedMonth is updated from the CalendarView header during scrolling
        // and is updated immediately from the selected spillover date on click. Keeping
        // this check cached avoids reparsing HeaderText for every realized day cell and
        // prevents a one-frame stale header from greying the newly selected month.
        if (_lastDisplayedMonth is { } displayed)
        {
            return date.Year == displayed.Year && date.Month == displayed.Month;
        }

        if (TryGetDisplayedMonth(out int year, out int month))
        {
            return date.Year == year && date.Month == month;
        }

        return date.Year == _selectedDate.Year && date.Month == _selectedDate.Month;
    }

    private bool TryGetDisplayedMonth(out int year, out int month)
    {
        year = 0;
        month = 0;

        string header = MonthCalendar.TemplateSettings?.HeaderText ?? string.Empty;
        if (string.IsNullOrWhiteSpace(header)) return false;

        string candidate = "1 " + header;
        if (DateTime.TryParse(candidate, CultureInfo.CurrentCulture, DateTimeStyles.AllowWhiteSpaces, out DateTime parsed) ||
            DateTime.TryParse(candidate, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out parsed))
        {
            year = parsed.Year;
            month = parsed.Month;
            return true;
        }

        return false;
    }

    private bool TryGetDisplayedMonthFromRealizedItems(out int year, out int month)
    {
        year = 0;
        month = 0;
        if (_realizedDayItems.Count == 0) return false;

        var majority = _realizedDayItems.Keys
            .GroupBy(date => (date.Year, date.Month))
            .OrderByDescending(group => group.Count())
            .ThenBy(group => Math.Abs((group.Key.Year * 12 + group.Key.Month) - (_selectedDate.Year * 12 + _selectedDate.Month)))
            .FirstOrDefault();

        if (majority is null) return false;
        year = majority.Key.Year;
        month = majority.Key.Month;
        return true;
    }

    private void PreviousAgendaDayButton_Click(object sender, RoutedEventArgs e)
        => QueueAgendaNavigation(-1);

    private void NextAgendaDayButton_Click(object sender, RoutedEventArgs e)
        => QueueAgendaNavigation(1);

    private void QueueAgendaNavigation(int dayDelta)
    {
        if (dayDelta == 0) return;

        // Keep accepting clicks while an animation is running. A burst of clicks is
        // coalesced into the next target date instead of disabling the arrow buttons.
        _queuedAgendaDayDelta = Math.Clamp(_queuedAgendaDayDelta + dayDelta, -366, 366);
        if (!_agendaNavigationInProgress)
        {
            _ = DrainAgendaNavigationQueueAsync();
        }
    }

    private async Task DrainAgendaNavigationQueueAsync()
    {
        if (_agendaNavigationInProgress) return;
        _agendaNavigationInProgress = true;

        try
        {
            while (_queuedAgendaDayDelta != 0)
            {
                int queuedDelta = _queuedAgendaDayDelta;
                _queuedAgendaDayDelta = 0;
                await NavigateAgendaBatchAsync(queuedDelta);
            }
        }
        finally
        {
            AgendaTranslateTransform.X = 0;
            AgendaContentHost.Opacity = 1;
            _agendaNavigationInProgress = false;

            // A click can arrive between the loop test and finally block.
            if (_queuedAgendaDayDelta != 0)
            {
                _ = DrainAgendaNavigationQueueAsync();
            }
        }
    }

    private async Task NavigateAgendaBatchAsync(int dayDelta)
    {
        if (dayDelta == 0) return;

        DateTime target = _selectedDate.Date.AddDays(dayDelta);

        // Same-month navigation is already cached. If a spam-click burst crosses a
        // month boundary, load only the final target month before the new agenda enters.
        // Show the agenda spinner immediately while that target month is unavailable.
        if (_calendarService.IsSignedIn)
        {
            var targetKey = MonthKey(target);
            bool needsTargetLoad = !_monthCache.TryGetValue(targetKey, out MonthAgendaCache? targetCache) ||
                                   DateTime.UtcNow - targetCache.FetchedAtUtc >= CacheFreshness;
            if (needsTargetLoad)
            {
                _agendaNavigationLoading = true;
                UpdateLoadingIndicator();
                await WaitForNextRenderAsync();
            }

            try
            {
                await EnsureMonthLoadedAsync(target, force: false, showProgress: false);
            }
            finally
            {
                _agendaNavigationLoading = false;
                UpdateLoadingIndicator();
            }
        }

        double exitX = dayDelta > 0 ? -34 : 34;
        double enterX = -exitX;
        await AnimateAgendaContentAsync(0, exitX, 1, 0, 90);

        SetSelectedDateWithoutScrolling(target);
        DisplayAgendaFromCache();

        AgendaTranslateTransform.X = enterX;
        AgendaContentHost.Opacity = 0;
        await AnimateAgendaContentAsync(enterX, 0, 0, 1, 120);
        QueueAdjacentAgendaMonthPrecache();
    }

    private void SetSelectedDateWithoutScrolling(DateTime target)
    {
        DateTime previous = _selectedDate.Date;
        _selectedDate = target.Date;
        _agendaAutoScrollRequested = true;

        try
        {
            _suppressCalendarSelectionChanged = true;
            MonthCalendar.SelectedDates.Clear();
            MonthCalendar.SelectedDates.Add(new DateTimeOffset(_selectedDate));
        }
        finally
        {
            _suppressCalendarSelectionChanged = false;
        }

        RefreshRealizedDayItem(previous);
        RefreshRealizedDayItem(_selectedDate);

        var targetMonth = MonthKey(_selectedDate);
        if (_lastDisplayedMonth != targetMonth)
        {
            _selectionRequestedMonth = targetMonth;
            _selectionRequestedHeaderBaseline = _lastDisplayedMonth;
            CommitDisplayedMonth(targetMonth);
        }
    }

    private async Task AnimateAgendaContentAsync(
        double fromX,
        double toX,
        double fromOpacity,
        double toOpacity,
        int durationMs)
    {
        AgendaTranslateTransform.X = fromX;
        AgendaContentHost.Opacity = fromOpacity;

        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        var storyboard = new Storyboard();

        var movement = new DoubleAnimation
        {
            From = fromX,
            To = toX,
            Duration = TimeSpan.FromMilliseconds(durationMs),
            EasingFunction = easing,
            EnableDependentAnimation = false
        };
        Storyboard.SetTarget(movement, AgendaTranslateTransform);
        Storyboard.SetTargetProperty(movement, "X");
        storyboard.Children.Add(movement);

        var fade = new DoubleAnimation
        {
            From = fromOpacity,
            To = toOpacity,
            Duration = TimeSpan.FromMilliseconds(durationMs),
            EasingFunction = easing,
            EnableDependentAnimation = false
        };
        Storyboard.SetTarget(fade, AgendaContentHost);
        Storyboard.SetTargetProperty(fade, "Opacity");
        storyboard.Children.Add(fade);

        storyboard.Completed += (_, _) => completion.TrySetResult(true);
        storyboard.Begin();
        await completion.Task;
    }

    private void QueueAdjacentAgendaMonthPrecache()
    {
        if (!_calendarService.IsSignedIn) return;

        DateTime selected = _selectedDate.Date;
        DateTime previousDay = selected.AddDays(-1);
        DateTime nextDay = selected.AddDays(1);
        var selectedMonth = MonthKey(selected);

        foreach (DateTime neighbor in new[] { previousDay, nextDay })
        {
            var neighborMonth = MonthKey(neighbor);
            if (neighborMonth == selectedMonth) continue;
            if (_monthCache.ContainsKey(neighborMonth) || _monthLoads.ContainsKey(neighborMonth)) continue;
            _ = EnsureMonthLoadedAsync(neighbor, force: false, showProgress: false);
        }
    }

    private void AddEventButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            DateTime day = _selectedDate.Date;
            string start = day.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
            string end = day.AddDays(1).ToString("yyyyMMdd", CultureInfo.InvariantCulture);
            string url = $"https://calendar.google.com/calendar/render?action=TEMPLATE&dates={start}/{end}";

            Process.Start(new ProcessStartInfo(url)
            {
                UseShellExecute = true
            });
            _ = HideFlyoutAnimatedAsync(returnFocusToTray: false);
        }
        catch
        {
            StatusText.Text = "Could not open Google Calendar";
        }
    }

    private async void SignInButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_calendarService.HasCredentialsFile)
        {
            await ShowErrorAsync(
                "One-time Google setup",
                "Create a Google OAuth Desktop-app credential, download its JSON, rename it to client_secret.json, and place it here:\n\n" +
                _calendarService.CredentialsPath +
                "\n\nThen click Sign in to Google again. Full steps are in README.md.");
            return;
        }

        SignInButton.IsEnabled = false;
        SignInButton.Visibility = Visibility.Collapsed;
        LoadingRing.IsActive = true;
        LoadingRing.Visibility = Visibility.Visible;
        StatusText.Text = "Opening Google sign-in...";

        try
        {
            await _calendarService.SignInAsync();
            SetConnectedUi();
            await EnsureMonthLoadedAsync(_selectedDate, force: true, showProgress: false);
            StartBackgroundRefresh();
            DisplayAgendaFromCache();
        }
        catch (Exception ex)
        {
            SetSignedOutUi();
            StatusText.Text = "Sign-in failed";
            await ShowErrorAsync("Google sign-in failed", ex.Message);
        }
        finally
        {
            UpdateLoadingIndicator();
        }
    }

    private void AgendaList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not AgendaEvent item || string.IsNullOrWhiteSpace(item.HtmlLink)) return;

        try
        {
            Process.Start(new ProcessStartInfo(item.HtmlLink)
            {
                UseShellExecute = true
            });
            _ = HideFlyoutAnimatedAsync(returnFocusToTray: false);
        }
        catch
        {
            StatusText.Text = "Could not open the event";
        }
    }

    private async Task ShowErrorAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = title,
            Content = new TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 460
            },
            CloseButtonText = "OK"
        };

        await dialog.ShowAsync();
    }

    private static (int Year, int Month) MonthKey(DateTime date) => (date.Year, date.Month);

    private enum TaskbarEdge
    {
        Left,
        Top,
        Right,
        Bottom
    }

    private sealed class DayCellDecoration
    {
        public string DayText { get; init; } = string.Empty;
        public Brush DayForeground { get; init; } = new SolidColorBrush(Microsoft.UI.Colors.White);
        public Brush HoverFill { get; init; } = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        public Brush TodayFill { get; init; } = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        public Brush SelectionStroke { get; init; } = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        public double SelectionStrokeThickness { get; init; }
        public Brush? Dot1 { get; init; }
        public Brush? Dot2 { get; init; }
        public Brush? Dot3 { get; init; }
        public Brush? Dot4 { get; init; }
        public Visibility Dot1Visibility { get; init; } = Visibility.Collapsed;
        public Visibility Dot2Visibility { get; init; } = Visibility.Collapsed;
        public Visibility Dot3Visibility { get; init; } = Visibility.Collapsed;
        public Visibility Dot4Visibility { get; init; } = Visibility.Collapsed;
    }

    private sealed class MonthAgendaCache
    {
        public MonthAgendaCache(DateTime fetchedAtUtc, Dictionary<DateTime, List<AgendaEvent>> byDay)
        {
            FetchedAtUtc = fetchedAtUtc;
            ByDay = byDay;
        }

        public DateTime FetchedAtUtc { get; }
        public Dictionary<DateTime, List<AgendaEvent>> ByDay { get; }
    }
}
