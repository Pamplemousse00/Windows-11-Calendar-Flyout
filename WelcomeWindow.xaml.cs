using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Win10CalendarFlyout.Interop;
using Win10CalendarFlyout.Services;
using WinRT.Interop;

namespace Win10CalendarFlyout;

public sealed partial class WelcomeWindow : Window
{
    private const int LogicalWidth = 520;
    private const int LogicalHeight = 470;

    private readonly AppWindow _appWindow;
    private readonly IntPtr _hwnd;
    private bool _prepared;

    public WelcomeWindow()
    {
        InitializeComponent();
        Title = "Calendar Flyout";
        SystemBackdrop = new MicaBackdrop();

        _hwnd = WindowNative.GetWindowHandle(this);
        WindowId id = Win32Interop.GetWindowIdFromWindow(_hwnd);
        _appWindow = AppWindow.GetFromWindowId(id);
        _appWindow.IsShownInSwitchers = true;

        ConfigureTitleBar();
        SetWindowIcon();
        ResizeForCurrentDpi();

        if (_appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
        }

        StartupCheckBox.IsChecked = true;
        ParkOffscreen();
    }

    public async Task ShowPreparedAsync()
    {
        if (_prepared)
        {
            Activate();
            return;
        }

        ParkOffscreen();
        Activate();
        await WaitForNextRenderAsync();
        await WaitForNextRenderAsync();
        await Task.Delay(24);
        WelcomeRoot.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        await WaitForNextRenderAsync();
        NativeMethods.DwmFlush();
        CenterOnPrimaryDisplay();
        NativeMethods.DwmFlush();
        Activate();
        _prepared = true;
    }

    private void ParkOffscreen()
        => _appWindow.Move(new PointInt32(-32000, -32000));

    private static Task WaitForNextRenderAsync()
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
            if (File.Exists(iconPath))
            {
                _appWindow.SetIcon(iconPath);
            }
        }
        catch
        {
            // Cosmetic only; the inline calendar glyph still identifies the window.
        }
    }

    private void ResizeForCurrentDpi()
    {
        int dpi = (int)Math.Max(96u, NativeMethods.GetDpiForWindow(_hwnd));
        int width = (int)Math.Round(LogicalWidth * dpi / 96.0);
        int height = (int)Math.Round(LogicalHeight * dpi / 96.0);
        _appWindow.Resize(new SizeInt32(width, height));
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

    private void ProceedButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            StartupService.SetEnabled(StartupCheckBox.IsChecked == true);
        }
        catch
        {
            // Setup should still complete even if a managed PC blocks the Run key.
        }

        AppSettings.MarkWelcomeCompleted();
        Close();
    }
}
