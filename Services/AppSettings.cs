using System.Text.Json;

namespace Win10CalendarFlyout.Services;

internal sealed class UserPreferences
{
    public bool SwitchToTodayOnOpen { get; set; }
    public int MaxEventDots { get; set; } = 4;
    public Dictionary<string, string> CalendarColorOverrides { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    // Periodic/background activity. These default to on so existing installs retain
    // the behavior they had before the controls were exposed in Settings.
    public bool EnableGoogleAutoRefresh { get; set; } = true;
    public bool EnableEventReminderChecks { get; set; } = true;
    public bool EnableTrayDateIconUpdates { get; set; } = true;
    public bool EnableCurrentEventUpdates { get; set; } = true;
    public bool EnableClickAwayDismiss { get; set; } = true;
}

internal static class AppSettings
{
    private static readonly string SettingsDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Win10CalendarFlyout");

    private static readonly string FirstRunMarker = Path.Combine(SettingsDirectory, "welcome-complete-v3");
    private static readonly string PreferencesPath = Path.Combine(SettingsDirectory, "settings.json");
    private static readonly object SyncRoot = new();
    private static UserPreferences _current = LoadPreferences();

    public static event EventHandler? Changed;

    public static bool WelcomeCompleted => File.Exists(FirstRunMarker);
    public static UserPreferences Current => _current;

    public static void MarkWelcomeCompleted()
    {
        Directory.CreateDirectory(SettingsDirectory);
        File.WriteAllText(FirstRunMarker, DateTimeOffset.UtcNow.ToString("O"));
    }

    public static void SetSwitchToTodayOnOpen(bool value)
    {
        if (_current.SwitchToTodayOnOpen == value) return;
        _current.SwitchToTodayOnOpen = value;
        SaveAndNotify();
    }

    public static void SetMaxEventDots(int value)
    {
        value = Math.Clamp(value, 0, 4);
        if (_current.MaxEventDots == value) return;
        _current.MaxEventDots = value;
        SaveAndNotify();
    }

    public static void SetGoogleAutoRefreshEnabled(bool value)
    {
        if (_current.EnableGoogleAutoRefresh == value) return;
        _current.EnableGoogleAutoRefresh = value;
        SaveAndNotify();
    }

    public static void SetEventReminderChecksEnabled(bool value)
    {
        if (_current.EnableEventReminderChecks == value) return;
        _current.EnableEventReminderChecks = value;
        SaveAndNotify();
    }

    public static void SetTrayDateIconUpdatesEnabled(bool value)
    {
        if (_current.EnableTrayDateIconUpdates == value) return;
        _current.EnableTrayDateIconUpdates = value;
        SaveAndNotify();
    }

    public static void SetCurrentEventUpdatesEnabled(bool value)
    {
        if (_current.EnableCurrentEventUpdates == value) return;
        _current.EnableCurrentEventUpdates = value;
        SaveAndNotify();
    }

    public static void SetClickAwayDismissEnabled(bool value)
    {
        if (_current.EnableClickAwayDismiss == value) return;
        _current.EnableClickAwayDismiss = value;
        SaveAndNotify();
    }

    public static void SetCalendarColor(string calendarId, string hex)
    {
        if (string.IsNullOrWhiteSpace(calendarId) || string.IsNullOrWhiteSpace(hex)) return;
        string normalized = NormalizeHex(hex);
        if (_current.CalendarColorOverrides.TryGetValue(calendarId, out string? existing) &&
            string.Equals(existing, normalized, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _current.CalendarColorOverrides[calendarId] = normalized;
        SaveAndNotify();
    }

    public static void ResetCalendarColor(string calendarId)
    {
        if (string.IsNullOrWhiteSpace(calendarId)) return;
        if (!_current.CalendarColorOverrides.Remove(calendarId)) return;
        SaveAndNotify();
    }

    public static void ReloadFromDiskAndNotify()
    {
        lock (SyncRoot)
        {
            _current = LoadPreferences();
        }

        Changed?.Invoke(null, EventArgs.Empty);
    }

    private static UserPreferences LoadPreferences()
    {
        try
        {
            if (!File.Exists(PreferencesPath)) return new UserPreferences();
            string json = File.ReadAllText(PreferencesPath);
            UserPreferences? loaded = JsonSerializer.Deserialize<UserPreferences>(json);
            if (loaded is null) return new UserPreferences();

            loaded.MaxEventDots = Math.Clamp(loaded.MaxEventDots, 0, 4);
            loaded.CalendarColorOverrides = new Dictionary<string, string>(
                loaded.CalendarColorOverrides ?? new Dictionary<string, string>(),
                StringComparer.OrdinalIgnoreCase);
            return loaded;
        }
        catch
        {
            return new UserPreferences();
        }
    }

    private static void SaveAndNotify()
    {
        lock (SyncRoot)
        {
            Directory.CreateDirectory(SettingsDirectory);
            string json = JsonSerializer.Serialize(_current, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(PreferencesPath, json);
        }

        Changed?.Invoke(null, EventArgs.Empty);
    }

    private static string NormalizeHex(string value)
    {
        string raw = value.Trim();
        if (!raw.StartsWith('#')) raw = "#" + raw;
        if (raw.Length != 7) return "#606AFF";
        return raw.ToUpperInvariant();
    }
}
