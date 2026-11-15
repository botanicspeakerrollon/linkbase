using Linkbase.Core.Explore;

namespace Linkbase.Tests;

public class PatternPagingTests
{
    [Fact]
    public void It_starts_with_nothing_shown_and_nothing_more()
    {
        var paging = new PatternPaging();

        Assert.Equal(0, paging.Offset);
        Assert.Equal(0, paging.Shown);
        Assert.False(paging.HasMore);
    }

    [Fact]
    public void Each_page_moves_the_offset_on_and_counts_what_is_left()
    {
        var paging = new PatternPaging();
        paging.Advance(100, 250);
        paging.Advance(100, 150);

        Assert.Equal(200, paging.Offset);
        Assert.Equal(200, paging.Shown);
        Assert.Equal(150, paging.Remaining);
        Assert.True(paging.HasMore);
    }

    [Fact]
    public void The_last_page_leaves_nothing_more()
    {
        var paging = new PatternPaging();
        paging.Advance(100, 0);

        Assert.False(paging.HasMore);
    }

    [Fact]
    public void A_new_search_starts_again()
    {
        var paging = new PatternPaging();
        paging.Advance(100, 40);
        paging.Reset();

        Assert.Equal(0, paging.Offset);
        Assert.Equal(0, paging.Shown);
        Assert.Equal(0, paging.Remaining);
    }
}
