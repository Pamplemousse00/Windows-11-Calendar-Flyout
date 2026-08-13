using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Win10CalendarFlyout.Models;

public sealed class AgendaEvent : INotifyPropertyChanged
{
    private Visibility _nowIndicatorVisibility = Visibility.Collapsed;
    private Brush? _calendarBrush;

    public string Title { get; set; } = string.Empty;
    public string CalendarName { get; set; } = string.Empty;
    public string TimeText { get; set; } = string.Empty;
    public string HtmlLink { get; set; } = string.Empty;
    public string GoogleEventId { get; set; } = string.Empty;
    public string CalendarId { get; set; } = string.Empty;
    public string CalendarDefaultColorHex { get; set; } = "#606AFF";

    public Brush? CalendarBrush
    {
        get => _calendarBrush;
        set
        {
            if (ReferenceEquals(_calendarBrush, value)) return;
            _calendarBrush = value;
            OnPropertyChanged();
        }
    }
    public List<int> ReminderMinutes { get; set; } = new();

    // Timed event bounds. Google can return events that cross midnight, so both
    // are kept in order to place an event into every day it overlaps.
    public DateTimeOffset? Start { get; set; }
    public DateTimeOffset? End { get; set; }

    // All-day Google events use date-only, end-exclusive bounds.
    public DateTime? AllDayStartDate { get; set; }
    public DateTime? AllDayEndDateExclusive { get; set; }
    public bool IsAllDay { get; set; }

    public Visibility NowIndicatorVisibility
    {
        get => _nowIndicatorVisibility;
        set
        {
            if (_nowIndicatorVisibility == value) return;
            _nowIndicatorVisibility = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
