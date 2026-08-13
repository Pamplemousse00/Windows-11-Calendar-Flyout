# Calendar Flyout v31

A WinUI 3 notification-area calendar flyout with Google Calendar agenda integration.

## v31 changes

- Tray reopen responsiveness: the window animation now yields between DWM-presented frames instead of monopolizing the UI thread for the full transition. A tray click that lands while click-away dismissal is finishing is queued and honored as soon as the transition completes. The redundant MainWindow tray debounce was removed; the tray service remains the single duplicate-callback filter.
- Settings opens without waiting on Google: calendar descriptors are cached whenever Calendar Flyout talks to Google, Settings uses that cache immediately, and any Calendar List refresh happens asynchronously after the Settings window begins rendering.
- Settings first-show warm-up was reduced to a single compositor frame. If calendar-color metadata is not cached yet, a small loading indicator is shown only in that section rather than delaying the entire Settings window.
- About reports **Version 0.31.0**.

- Flyout date header now uses a darker tint while the calendar/agenda body is slightly lighter, closer to Windows 11 shell flyouts.
- Out-of-scope dates are darker than dates in the focused month.
- Day-cell dots are kept completely inside the real 44x44 `CalendarViewDayItem` bounds so CalendarView cannot clip the bottom of them.
- Today dots use a high-contrast light color so they remain visible on the filled accent circle.
- Month/year picker selection waits for CalendarView's virtualized header/cells to settle, then loads that month's Google cache/dots even before a day is selected.
- Google sign-in is centered in the agenda when disconnected.
- Agenda header now has previous-day / next-day buttons beside Add event.
- Agenda day navigation preloads only an adjacent month when the selected date is on a month boundary, then uses a short horizontal transition without scrolling CalendarView.
- Welcome and Settings windows are rendered off-screen first, then moved into view after their XAML/backdrop is prepared to avoid the black/empty first frame.
- Settings is now a normal resizable/minimizable/maximizable window, so Windows snap layouts work.
- Background activity table uses **State** as the switch-column label.
- About reports **Version 0.31.0**.
- Added `publish-compact.ps1` for a smaller framework-dependent Release folder.

## Google refresh behavior

Automatic refresh remains every 5 minutes when enabled. Manual refresh from the flyout header bypasses the cache immediately.

Month data is cached only as needed. The cache remains capped to keep long-running memory use bounded.

## Settings

- Switch to current date when flyout is opened
- Maximum event dots: 0-4
- Per-calendar color overrides
- Background activity switches
- About / version / author

## Build

Open `Win10CalendarFlyout.sln` in Visual Studio and build **x64**.

Clean before switching versions:

```powershell
Remove-Item .vs, bin, obj -Recurse -Force -ErrorAction SilentlyContinue
```

### Portable one-file build

```powershell
.\publish-single-exe.ps1
```

### Smaller framework-dependent build

```powershell
.\publish-compact.ps1
```

See `PUBLISHING.md` for the runtime requirements and OAuth-embedding options.


## v40 changes

- Settings is a normal disposable top-level window again. It is created on demand, positioned before activation, uses the normal Windows/DWM opening animation, and is genuinely destroyed when closed.
- Settings explicitly releases its Mica backdrop and disconnects its XAML tree on close so repeated open/close cycles do not intentionally retain another full window surface.
- Crossing into an uncached month with the agenda previous/next buttons now shows a larger agenda loading spinner while the target month is fetched.
