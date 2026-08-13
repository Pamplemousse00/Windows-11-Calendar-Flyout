using System.Reflection;
using System.Globalization;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Calendar.v3;
using Google.Apis.Calendar.v3.Data;
using Google.Apis.Services;
using Google.Apis.Util.Store;
using Microsoft.UI;
using Microsoft.UI.Xaml.Media;
using Win10CalendarFlyout.Models;

namespace Win10CalendarFlyout.Services;

public sealed class GoogleCalendarService
{
    private const string EmbeddedCredentialsResourceName = "Win10CalendarFlyout.EmbeddedGoogleOAuthCredentials";
    private readonly string _credentialsPath;
    private readonly string _tokenPath;
    private CalendarService? _service;
    private readonly Dictionary<string, Brush> _calendarBrushCache = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, string> _calendarColorOverrides = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<CalendarDescriptor> _cachedSelectedCalendars = Array.Empty<CalendarDescriptor>();

    public GoogleCalendarService()
    {
        string localData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Win10CalendarFlyout");

        Directory.CreateDirectory(localData);
        _credentialsPath = Path.Combine(localData, "client_secret.json");
        _tokenPath = Path.Combine(localData, "google-token");
    }

    public string CredentialsPath => _credentialsPath;
    public bool HasEmbeddedCredentials => Assembly.GetExecutingAssembly().GetManifestResourceInfo(EmbeddedCredentialsResourceName) is not null;
    public bool HasCredentialsFile => File.Exists(_credentialsPath) || HasEmbeddedCredentials;
    public bool HasStoredToken => Directory.Exists(_tokenPath) && Directory.EnumerateFiles(_tokenPath).Any();
    public bool IsSignedIn => _service is not null;
    public IReadOnlyList<CalendarDescriptor> CachedSelectedCalendars => _cachedSelectedCalendars;

    public void ApplyColorOverrides(IReadOnlyDictionary<string, string> overrides)
    {
        _calendarColorOverrides = new Dictionary<string, string>(overrides, StringComparer.OrdinalIgnoreCase);
        _calendarBrushCache.Clear();
    }

    public Brush ResolveCalendarBrush(string calendarId, string? defaultHex)
        => GetCalendarBrush(calendarId, defaultHex);

    public async Task<IReadOnlyList<CalendarDescriptor>> GetSelectedCalendarsAsync(
        CancellationToken cancellationToken = default)
    {
        if (_service is null) return Array.Empty<CalendarDescriptor>();

        await GetSelectedCalendarEntriesAsync(cancellationToken);
        return _cachedSelectedCalendars;
    }

    public async Task SignInAsync(CancellationToken cancellationToken = default)
    {
        if (!HasCredentialsFile)
        {
            throw new FileNotFoundException(
                "Google OAuth credentials were not found. Copy your Desktop-app OAuth JSON to this path:\n" + _credentialsPath,
                _credentialsPath);
        }

        await using Stream stream = OpenCredentialsStream();
        var secrets = GoogleClientSecrets.FromStream(stream).Secrets;

        UserCredential credential = await GoogleWebAuthorizationBroker.AuthorizeAsync(
            secrets,
            new[] { CalendarService.Scope.CalendarReadonly },
            "calendar-flyout-user",
            cancellationToken,
            new FileDataStore(_tokenPath, true));

        _service = new CalendarService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = "WinUI Calendar Flyout"
        });
    }


    private Stream OpenCredentialsStream()
    {
        if (File.Exists(_credentialsPath))
        {
            return File.OpenRead(_credentialsPath);
        }

        Stream? embedded = Assembly.GetExecutingAssembly().GetManifestResourceStream(EmbeddedCredentialsResourceName);
        if (embedded is not null)
        {
            return embedded;
        }

        throw new FileNotFoundException(
            "Google OAuth credentials were not found. Copy your Desktop-app OAuth JSON to this path:\n" + _credentialsPath,
            _credentialsPath);
    }

    public async Task<IReadOnlyList<AgendaEvent>> GetEventsForRangeAsync(
        DateTime startDate,
        DateTime endDateExclusive,
        CancellationToken cancellationToken = default)
    {
        if (_service is null || endDateExclusive <= startDate)
        {
            return Array.Empty<AgendaEvent>();
        }

        DateTime startDay = DateTime.SpecifyKind(startDate.Date, DateTimeKind.Unspecified);
        DateTime endDay = DateTime.SpecifyKind(endDateExclusive.Date, DateTimeKind.Unspecified);
        var rangeStart = new DateTimeOffset(startDay, TimeZoneInfo.Local.GetUtcOffset(startDay));
        var rangeEnd = new DateTimeOffset(endDay, TimeZoneInfo.Local.GetUtcOffset(endDay));

        List<CalendarListEntry> selectedCalendars = await GetSelectedCalendarEntriesAsync(cancellationToken);

        var results = new List<AgendaEvent>();

        foreach (CalendarListEntry calendar in selectedCalendars)
        {
            string? pageToken = null;
            do
            {
                var request = _service.Events.List(calendar.Id);
                request.TimeMinDateTimeOffset = rangeStart;
                request.TimeMaxDateTimeOffset = rangeEnd;
                request.SingleEvents = true;
                request.OrderBy = EventsResource.ListRequest.OrderByEnum.StartTime;
                request.ShowDeleted = false;
                request.MaxResults = 2500;
                request.PageToken = pageToken;

                Events events = await request.ExecuteAsync(cancellationToken);
                foreach (Event item in events.Items ?? Array.Empty<Event>())
                {
                    if (string.IsNullOrWhiteSpace(item.HtmlLink)) continue;

                    bool allDay = !string.IsNullOrWhiteSpace(item.Start?.Date);
                    DateTimeOffset? startTime = item.Start?.DateTimeDateTimeOffset;
                    DateTimeOffset? endTime = item.End?.DateTimeDateTimeOffset;
                    DateTime? allDayStart = ParseGoogleDate(item.Start?.Date);
                    DateTime? allDayEndExclusive = ParseGoogleDate(item.End?.Date);

                    results.Add(new AgendaEvent
                    {
                        Title = string.IsNullOrWhiteSpace(item.Summary) ? "(No title)" : item.Summary,
                        CalendarName = calendar.SummaryOverride ?? calendar.Summary ?? "Google Calendar",
                        TimeText = allDay
                            ? "All day"
                            : startTime?.ToLocalTime().ToString("h:mm tt", CultureInfo.CurrentCulture) ?? string.Empty,
                        HtmlLink = item.HtmlLink,
                        GoogleEventId = item.Id ?? item.ICalUID ?? item.HtmlLink,
                        CalendarId = calendar.Id ?? string.Empty,
                        CalendarDefaultColorHex = string.IsNullOrWhiteSpace(calendar.BackgroundColor) ? "#606AFF" : calendar.BackgroundColor,
                        ReminderMinutes = ResolvePopupReminderMinutes(item, calendar),
                        Start = startTime,
                        End = endTime,
                        AllDayStartDate = allDayStart,
                        AllDayEndDateExclusive = allDayEndExclusive ?? allDayStart?.AddDays(1),
                        IsAllDay = allDay,
                        CalendarBrush = GetCalendarBrush(calendar.Id ?? string.Empty, calendar.BackgroundColor)
                    });
                }

                pageToken = events.NextPageToken;
            }
            while (!string.IsNullOrWhiteSpace(pageToken));
        }

        return results
            .OrderBy(e => e.IsAllDay ? 0 : 1)
            .ThenBy(e => e.Start ?? DateTimeOffset.MinValue)
            .ThenBy(e => e.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }


    private async Task<List<CalendarListEntry>> GetSelectedCalendarEntriesAsync(CancellationToken cancellationToken)
    {
        if (_service is null) return new List<CalendarListEntry>();

        CalendarList calendars = await _service.CalendarList.List().ExecuteAsync(cancellationToken);
        List<CalendarListEntry> selected = (calendars.Items ?? Array.Empty<CalendarListEntry>())
            .Where(c => c.Deleted != true)
            .Where(c => c.Hidden != true)
            .Where(c => c.Selected == true || c.Primary == true)
            .Where(c => !string.Equals(c.AccessRole, "freeBusyReader", StringComparison.OrdinalIgnoreCase))
            .ToList();

        _cachedSelectedCalendars = selected
            .Select(calendar => new CalendarDescriptor(
                calendar.Id ?? string.Empty,
                calendar.SummaryOverride ?? calendar.Summary ?? "Google Calendar",
                string.IsNullOrWhiteSpace(calendar.BackgroundColor) ? "#606AFF" : calendar.BackgroundColor))
            .Where(calendar => !string.IsNullOrWhiteSpace(calendar.Id))
            .OrderBy(calendar => calendar.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        return selected;
    }

    private static List<int> ResolvePopupReminderMinutes(Event item, CalendarListEntry calendar)
    {
        IEnumerable<EventReminder> reminders;

        // Google events either use the calendar's private default reminders or
        // replace them with event-specific overrides. Mirror popup reminders only;
        // email reminders remain Google's responsibility and should not be duplicated
        // as Windows notifications.
        if (item.Reminders?.UseDefault != false)
        {
            reminders = calendar.DefaultReminders ?? Array.Empty<EventReminder>();
        }
        else
        {
            reminders = item.Reminders?.Overrides ?? Array.Empty<EventReminder>();
        }

        return reminders
            .Where(r => string.Equals(r.Method, "popup", StringComparison.OrdinalIgnoreCase))
            .Where(r => r.Minutes.HasValue && r.Minutes.Value >= 0)
            .Select(r => r.Minutes!.Value)
            .Distinct()
            .OrderBy(m => m)
            .ToList();
    }

    private static DateTime? ParseGoogleDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        return DateTime.TryParseExact(
            value,
            "yyyy-MM-dd",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out DateTime parsed)
            ? parsed.Date
            : null;
    }

    private Brush GetCalendarBrush(string calendarId, string? hex)
    {
        string chosen = _calendarColorOverrides.TryGetValue(calendarId, out string? overridden)
            ? overridden
            : (string.IsNullOrWhiteSpace(hex) ? "#606AFF" : hex.Trim());
        string key = $"{calendarId}|{chosen}";
        string colorHex = chosen;
        if (_calendarBrushCache.TryGetValue(key, out Brush? cached)) return cached;

        Brush brush = new SolidColorBrush(ColorHelper.FromArgb(255, 96, 106, 255));
        if (colorHex.StartsWith('#'))
        {
            try
            {
                string raw = colorHex[1..];
                if (raw.Length == 6)
                {
                    byte r = Convert.ToByte(raw[0..2], 16);
                    byte g = Convert.ToByte(raw[2..4], 16);
                    byte b = Convert.ToByte(raw[4..6], 16);
                    brush = new SolidColorBrush(ColorHelper.FromArgb(255, r, g, b));
                }
            }
            catch
            {
                // Keep the accent-like fallback.
            }
        }

        _calendarBrushCache[key] = brush;
        return brush;
    }
}
