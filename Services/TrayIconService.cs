using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Win10CalendarFlyout.Interop;

namespace Win10CalendarFlyout.Services;

public sealed class TrayIconService : IDisposable
{
    private const uint CallbackMessage = NativeMethods.WM_APP + 42;
    private const uint MenuOpen = 1001;
    private const uint MenuRefresh = 1002;
    private const uint MenuExit = 1003;

    private static readonly Guid IconGuid = new("40CF8646-A029-4F41-900B-C81302E1B974");

    private readonly DispatcherQueue _dispatcher;
    private readonly NativeMethods.WndProc _wndProc;
    private readonly string _windowClassName = $"Win10CalendarFlyout.Tray.{Guid.NewGuid():N}";
    private readonly DispatcherQueueTimer _dateIconTimer;
    private NativeMethods.NOTIFYICONDATA _iconData;
    private IntPtr _messageWindow;
    private IntPtr _iconHandle;
    private bool _ownsIcon;
    private bool _usingNotifyIconVersion4;
    private bool _disposed;
    private bool _hasActivationPoint;
    private NativeMethods.POINT _lastActivationPoint;
    private DateTime _lastPrimaryInvokeUtc;
    private int _displayedDay;
    private bool _dateAutoUpdateEnabled = true;

    public event EventHandler? Invoked;
    public event EventHandler? RefreshRequested;
    public event EventHandler? ExitRequested;

    public TrayIconService(DispatcherQueue dispatcher)
    {
        _dispatcher = dispatcher;
        _wndProc = WindowProcedure;
        CreateMessageWindow();
        AddTrayIcon();

        // Keep the date in the tray icon correct across midnight without restarting.
        _dateIconTimer = _dispatcher.CreateTimer();
        _dateIconTimer.Interval = TimeSpan.FromMinutes(1);
        _dateIconTimer.IsRepeating = true;
        _dateIconTimer.Tick += (_, _) => RefreshDateIconIfNeeded();
        _dateIconTimer.Start();
    }

    public void SetDateAutoUpdateEnabled(bool enabled)
    {
        if (_disposed || _dateAutoUpdateEnabled == enabled) return;
        _dateAutoUpdateEnabled = enabled;

        if (enabled)
        {
            RefreshDateIconIfNeeded();
            if (!_dateIconTimer.IsRunning) _dateIconTimer.Start();
        }
        else
        {
            _dateIconTimer.Stop();
        }
    }

    internal bool TryGetLastActivationPoint(out NativeMethods.POINT point)
    {
        point = _lastActivationPoint;
        return _hasActivationPoint;
    }

    internal bool TryGetIconRect(out NativeMethods.RECT rect)
    {
        var identifier = new NativeMethods.NOTIFYICONIDENTIFIER
        {
            cbSize = (uint)Marshal.SizeOf<NativeMethods.NOTIFYICONIDENTIFIER>(),
            hWnd = _messageWindow,
            uID = 1,
            guidItem = IconGuid
        };

        return NativeMethods.Shell_NotifyIconGetRect(ref identifier, out rect) == 0;
    }

    public void ReturnFocusToTray()
    {
        if (_messageWindow == IntPtr.Zero) return;
        NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_SETFOCUS, ref _iconData);
    }

    private void CreateMessageWindow()
    {
        IntPtr hInstance = NativeMethods.GetModuleHandle(null);
        var wc = new NativeMethods.WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<NativeMethods.WNDCLASSEX>(),
            lpfnWndProc = _wndProc,
            hInstance = hInstance,
            lpszClassName = _windowClassName
        };

        if (NativeMethods.RegisterClassEx(ref wc) == 0)
        {
            throw new InvalidOperationException("Could not register the tray message window class.");
        }

        _messageWindow = NativeMethods.CreateWindowEx(
            0,
            _windowClassName,
            "WinUI Calendar Flyout Tray",
            0,
            0, 0, 0, 0,
            NativeMethods.HWND_MESSAGE,
            IntPtr.Zero,
            hInstance,
            IntPtr.Zero);

        if (_messageWindow == IntPtr.Zero)
        {
            throw new InvalidOperationException("Could not create the tray message window.");
        }
    }

    private void AddTrayIcon()
    {
        _displayedDay = DateTime.Now.Day;
        _iconHandle = LoadDateIcon(_displayedDay, out _ownsIcon);

        _iconData = new NativeMethods.NOTIFYICONDATA
        {
            cbSize = (uint)Marshal.SizeOf<NativeMethods.NOTIFYICONDATA>(),
            hWnd = _messageWindow,
            uID = 1,
            uFlags = NativeMethods.NIF_MESSAGE | NativeMethods.NIF_ICON | NativeMethods.NIF_TIP |
                     NativeMethods.NIF_GUID | NativeMethods.NIF_SHOWTIP,
            uCallbackMessage = CallbackMessage,
            hIcon = _iconHandle,
            szTip = $"Calendar - {DateTime.Now:dddd, MMMM d}",
            guidItem = IconGuid
        };

        if (!NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_ADD, ref _iconData))
        {
            throw new InvalidOperationException("Windows did not accept the notification-area icon.");
        }

        _iconData.uVersion = NativeMethods.NOTIFYICON_VERSION_4;
        _usingNotifyIconVersion4 = NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_SETVERSION, ref _iconData);
    }

    private IntPtr LoadDateIcon(int day, out bool ownsIcon)
    {
        string iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", $"calendar-tray-{day}.ico");
        if (!File.Exists(iconPath))
        {
            iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "calendar-tray.ico");
        }

        uint systemDpi = Math.Max(96u, NativeMethods.GetDpiForSystem());
        // Request a slightly larger source than the old 16px asset. The shell still
        // places it in the normal notification-area slot, but the artwork fills more
        // of that slot and therefore matches the visual weight of Wi-Fi/volume icons.
        int iconSize = Math.Clamp((int)Math.Round(20.0 * systemDpi / 96.0), 20, 40);
        IntPtr handle = NativeMethods.LoadImage(
            IntPtr.Zero,
            iconPath,
            NativeMethods.IMAGE_ICON,
            iconSize,
            iconSize,
            NativeMethods.LR_LOADFROMFILE);

        if (handle != IntPtr.Zero)
        {
            ownsIcon = true;
            return handle;
        }

        ownsIcon = false;
        return NativeMethods.LoadIcon(IntPtr.Zero, new IntPtr((int)NativeMethods.IDI_APPLICATION));
    }

    private void RefreshDateIconIfNeeded()
    {
        int day = DateTime.Now.Day;
        if (day == _displayedDay || _messageWindow == IntPtr.Zero) return;

        IntPtr newHandle = LoadDateIcon(day, out bool ownsNewIcon);
        if (newHandle == IntPtr.Zero) return;

        IntPtr oldHandle = _iconHandle;
        bool ownedOldIcon = _ownsIcon;

        _iconHandle = newHandle;
        _ownsIcon = ownsNewIcon;
        _displayedDay = day;
        _iconData.hIcon = newHandle;
        _iconData.szTip = $"Calendar - {DateTime.Now:dddd, MMMM d}";
        _iconData.uFlags = NativeMethods.NIF_ICON | NativeMethods.NIF_TIP | NativeMethods.NIF_GUID;
        NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_MODIFY, ref _iconData);

        if (ownedOldIcon && oldHandle != IntPtr.Zero)
        {
            NativeMethods.DestroyIcon(oldHandle);
        }
    }

    private IntPtr WindowProcedure(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == CallbackMessage)
        {
            uint notification = unchecked((uint)lParam.ToInt64()) & 0xFFFF;

            bool primaryInvoke = _usingNotifyIconVersion4
                ? notification is NativeMethods.NIN_SELECT or NativeMethods.NIN_KEYSELECT
                : notification == NativeMethods.WM_LBUTTONUP;

            if (primaryInvoke)
            {
                if (_usingNotifyIconVersion4 && notification == NativeMethods.NIN_SELECT)
                {
                    int packed = unchecked((int)wParam.ToInt64());
                    _lastActivationPoint = new NativeMethods.POINT
                    {
                        X = unchecked((short)(packed & 0xFFFF)),
                        Y = unchecked((short)((packed >> 16) & 0xFFFF))
                    };
                    _hasActivationPoint = true;
                }
                else if (notification == NativeMethods.NIN_KEYSELECT)
                {
                    _hasActivationPoint = false;
                }

                DateTime now = DateTime.UtcNow;
                if (now - _lastPrimaryInvokeUtc < TimeSpan.FromMilliseconds(320))
                {
                    return IntPtr.Zero;
                }
                _lastPrimaryInvokeUtc = now;

                _dispatcher.TryEnqueue(() => Invoked?.Invoke(this, EventArgs.Empty));
                return IntPtr.Zero;
            }

            bool contextInvoke = _usingNotifyIconVersion4
                ? notification == NativeMethods.WM_CONTEXTMENU
                : notification == NativeMethods.WM_RBUTTONUP;

            if (contextInvoke)
            {
                ShowContextMenu();
                return IntPtr.Zero;
            }
        }

        return NativeMethods.DefWindowProc(hWnd, msg, wParam, lParam);
    }

    private void ShowContextMenu()
    {
        if (!NativeMethods.GetCursorPos(out var point)) return;

        IntPtr menu = NativeMethods.CreatePopupMenu();
        if (menu == IntPtr.Zero) return;

        try
        {
            NativeMethods.AppendMenu(menu, NativeMethods.MF_STRING, MenuOpen, "Open calendar");
            NativeMethods.AppendMenu(menu, NativeMethods.MF_STRING, MenuRefresh, "Refresh calendars");
            NativeMethods.AppendMenu(menu, NativeMethods.MF_SEPARATOR, 0, null);
            NativeMethods.AppendMenu(menu, NativeMethods.MF_STRING, MenuExit, "Quit");

            NativeMethods.SetForegroundWindow(_messageWindow);
            uint command = NativeMethods.TrackPopupMenuEx(
                menu,
                NativeMethods.TPM_RIGHTBUTTON | NativeMethods.TPM_RETURNCMD,
                point.X,
                point.Y,
                _messageWindow,
                IntPtr.Zero);

            _dispatcher.TryEnqueue(() =>
            {
                switch (command)
                {
                    case MenuOpen:
                        Invoked?.Invoke(this, EventArgs.Empty);
                        break;
                    case MenuRefresh:
                        RefreshRequested?.Invoke(this, EventArgs.Empty);
                        break;
                    case MenuExit:
                        ExitRequested?.Invoke(this, EventArgs.Empty);
                        break;
                }
            });
        }
        finally
        {
            NativeMethods.DestroyMenu(menu);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _dateIconTimer.Stop();

        if (_messageWindow != IntPtr.Zero)
        {
            NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_DELETE, ref _iconData);
        }

        if (_ownsIcon && _iconHandle != IntPtr.Zero)
        {
            NativeMethods.DestroyIcon(_iconHandle);
        }

        if (_messageWindow != IntPtr.Zero)
        {
            NativeMethods.DestroyWindow(_messageWindow);
            _messageWindow = IntPtr.Zero;
        }
    }
}
