using Linkbase.Core.Timeline;

namespace Linkbase.Core.Explore;

// The "show the graph as it was on" choice on the Explore page: the day, the start of the history that the slider spans and
// what didn't exist yet on that day. The page loads the view; this holds the rules about days.
public sealed class AsOfState
{
    // Null shows the graph as it is now.
    public DateTime? Day { get; private set; }

    public DateOnly? HistoryStart { get; set; }

    public AsOfView View { get; set; } = AsOfView.None;

    // Today is the same as no day, because the graph as it is now is today's.
    public void Choose(DateTime? day, DateTime today) =>
        Day = day is { } d && d.Date >= today.Date ? null : day?.Date;

    public bool Active => Day is not null;

    // Days from the start of the history to today, for the slider.
    public int HistoryDays(DateTime today) =>
        HistoryStart is { } start ? Math.Max(1, DateOnly.FromDateTime(today).DayNumber - start.DayNumber) : 0;

    public int SliderValue(DateTime today) =>
        HistoryStart is { } start && Day is { } day
            ? Math.Clamp(DateOnly.FromDateTime(day).DayNumber - start.DayNumber, 0, HistoryDays(today))
            : HistoryDays(today);

    public DateTime? DayAt(int days) => HistoryStart is { } start ? start.AddDays(days).ToDateTime(TimeOnly.MinValue) : null;
}
