using System.Security.Cryptography;
using System.Text;
using Microsoft.UI.Dispatching;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using Win10CalendarFlyout.Models;

namespace Win10CalendarFlyout.Services;

/// <summary>
/// Mirrors Google Calendar popup reminders while the tray app is running.
/// The first-run setup enables startup by default, so this service normally
/// remains alive for the entire Windows session without a background task.
/// </summary>
internal sealed class NotificationSchedulerService : IDisposable
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan LateGrace = TimeSpan.FromMinutes(5);

    private readonly DispatcherQueueTimer _timer;
    private readonly Dictionary<string, PendingReminder> _pending = new(StringComparer.Ordinal);
    private readonly HashSet<string> _delivered = new(StringComparer.Ordinal);
    private bool _disposed;
    private bool _enabled = true;

    public NotificationSchedulerService(DispatcherQueue dispatcher)
    {
        _timer = dispatcher.CreateTimer();
        _timer.Interval = TickInterval;
        _timer.IsRepeating = true;
        _timer.Tick += (_, _) => DeliverDueReminders();
        // Start only when at least one Google popup reminder is actually pending.
    }

    public void SetEnabled(bool enabled)
    {
        if (_disposed || _enabled == enabled) return;
        _enabled = enabled;

        if (!_enabled)
        {
            _timer.Stop();
            return;
        }

        // Catch up immediately when re-enabled, subject to the normal late-reminder grace.
        DeliverDueReminders();
        UpdateTimerState();
    }

    public void ReplaceMonthSchedule(int year, int month, IEnumerable<AgendaEvent> events)
    {
        string monthKey = $"{year:0000}-{month:00}";

        foreach (string key in _pending
                     .Where(pair => pair.Value.MonthKey == monthKey)
                     .Select(pair => pair.Key)
                     .ToList())
        {
            _pending.Remove(key);
        }

        foreach (AgendaEvent item in events)
        {
            DateTimeOffset? start = GetLocalStart(item);
            if (!start.HasValue || item.ReminderMinutes.Count == 0) continue;

            foreach (int minutes in item.ReminderMinutes.Distinct().Where(m => m >= 0 && m <= 40320))
            {
                DateTimeOffset delivery = start.Value.AddMinutes(-minutes);
                string key = MakeKey(item, minutes, delivery);
                _pending[key] = new PendingReminder(monthKey, key, delivery, item);
            }
        }

        DeliverDueReminders();
        UpdateTimerState();
    }

    public void RemoveMonthSchedule(int year, int month)
    {
        string monthKey = $"{year:0000}-{month:00}";
        foreach (string key in _pending
                     .Where(pair => pair.Value.MonthKey == monthKey)
                     .Select(pair => pair.Key)
                     .ToList())
        {
            _pending.Remove(key);
        }
        UpdateTimerState();
    }

    private void DeliverDueReminders()
    {
        if (_disposed || !_enabled || _pending.Count == 0) return;

        DateTimeOffset now = DateTimeOffset.Now;
        foreach (PendingReminder reminder in _pending.Values.ToList())
        {
            if (reminder.DeliveryTime > now) continue;

            if (_delivered.Contains(reminder.Key))
            {
                _pending.Remove(reminder.Key);
                continue;
            }

            // Do not suddenly surface a very stale reminder after a long suspend/sleep.
            if (now - reminder.DeliveryTime > LateGrace)
            {
                _pending.Remove(reminder.Key);
                _delivered.Add(reminder.Key);
                continue;
            }

            try
            {
                AgendaEvent item = reminder.Event;
                DateTimeOffset start = GetLocalStart(item) ?? reminder.DeliveryTime;
                var notification = new AppNotificationBuilder()
                    .SetScenario(AppNotificationScenario.Reminder)
                    .AddArgument("action", "open")
                    .AddArgument("url", item.HtmlLink)
                    .AddText(item.Title)
                    .AddText(BuildDetailLine(item, start))
                    .AddButton(new AppNotificationButton("Reschedule")
                        .AddArgument("action", "reschedule")
                        .AddArgument("url", item.HtmlLink))
                    .AddButton(new AppNotificationButton("Dismiss")
                        .AddArgument("action", "dismiss"))
                    .BuildNotification();

                AppNotificationManager.Default.Show(notification);
            }
            catch
            {
                // Notifications can be blocked by Windows policy/user settings. The
                // calendar itself should keep running even when a banner can't be shown.
            }
            finally
            {
                _delivered.Add(reminder.Key);
                _pending.Remove(reminder.Key);
            }
        }

        // Prevent this process-lifetime de-duplication set from growing forever.
        if (_delivered.Count > 4096)
        {
            _delivered.Clear();
        }

        UpdateTimerState();
    }

    private void UpdateTimerState()
    {
        if (_disposed) return;
        if (_enabled && _pending.Count > 0)
        {
            if (!_timer.IsRunning) _timer.Start();
        }
        else if (_timer.IsRunning)
        {
            _timer.Stop();
        }
    }

    private static DateTimeOffset? GetLocalStart(AgendaEvent item)
    {
        if (item.Start.HasValue) return item.Start.Value.ToLocalTime();
        if (!item.AllDayStartDate.HasValue) return null;

        DateTime localMidnight = DateTime.SpecifyKind(item.AllDayStartDate.Value.Date, DateTimeKind.Unspecified);
        return new DateTimeOffset(localMidnight, TimeZoneInfo.Local.GetUtcOffset(localMidnight));
    }

    private static string BuildDetailLine(AgendaEvent item, DateTimeOffset start)
    {
        string when = item.IsAllDay ? "All day" : start.ToString("h:mm tt");
        return string.IsNullOrWhiteSpace(item.CalendarName)
            ? when
            : $"{when} - {item.CalendarName}";
    }

    private static string MakeKey(AgendaEvent item, int minutes, DateTimeOffset delivery)
    {
        string seed = $"{item.GoogleEventId}|{item.Start:O}|{item.AllDayStartDate:yyyy-MM-dd}|{minutes}|{delivery:O}";
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(seed));
        return Convert.ToHexString(hash.AsSpan(0, 12));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        _pending.Clear();
    }

    private sealed record PendingReminder(
        string MonthKey,
        string Key,
        DateTimeOffset DeliveryTime,
        AgendaEvent Event);
}
