using Linkbase.Core.Explore;

namespace Linkbase.Tests;

public class AsOfStateTests
{
    private static readonly DateTime Today = new(2026, 11, 16, 15, 30, 0);

    private static AsOfState Started() => new() { HistoryStart = new DateOnly(2026, 11, 6) };

    [Fact]
    public void A_past_day_is_kept_without_its_time()
    {
        var state = new AsOfState();
        state.Choose(new DateTime(2026, 11, 10, 9, 0, 0), Today);

        Assert.True(state.Active);
        Assert.Equal(new DateTime(2026, 11, 10), state.Day);
    }

    [Theory]
    [InlineData(2026, 11, 16)]
    [InlineData(2026, 12, 1)]
    public void Today_or_later_is_the_same_as_no_day(int year, int month, int day)
    {
        var state = new AsOfState();
        state.Choose(new DateTime(2026, 11, 2), Today);
        state.Choose(new DateTime(year, month, day), Today);

        Assert.False(state.Active);
        Assert.Null(state.Day);
    }

    [Fact]
    public void Choosing_nothing_clears_the_day()
    {
        var state = new AsOfState();
        state.Choose(new DateTime(2026, 11, 2), Today);
        state.Choose(null, Today);

        Assert.Null(state.Day);
    }

    [Fact]
    public void The_slider_spans_the_history_up_to_today()
    {
        var state = Started();

        Assert.Equal(10, state.HistoryDays(Today));
        Assert.Equal(10, state.SliderValue(Today));
        state.Choose(new DateTime(2026, 11, 9), Today);
        Assert.Equal(3, state.SliderValue(Today));
        Assert.Equal(new DateTime(2026, 11, 9), state.DayAt(3));
    }

    [Fact]
    public void With_no_history_there_is_nothing_to_slide()
    {
        var state = new AsOfState();

        Assert.Equal(0, state.HistoryDays(Today));
        Assert.Equal(0, state.SliderValue(Today));
        Assert.Null(state.DayAt(3));
    }

    [Fact]
    public void A_day_before_the_history_starts_sits_at_the_start_of_the_slider()
    {
        var state = Started();
        state.Choose(new DateTime(2026, 1, 1), Today);

        Assert.Equal(0, state.SliderValue(Today));
    }

    [Fact]
    public void History_that_starts_today_still_has_one_day_to_slide()
    {
        var state = new AsOfState { HistoryStart = new DateOnly(2026, 11, 16) };

        Assert.Equal(1, state.HistoryDays(Today));
    }
}
