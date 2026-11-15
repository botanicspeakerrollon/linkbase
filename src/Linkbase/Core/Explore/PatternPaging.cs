namespace Linkbase.Core.Explore;

// How far through a pattern search's matches the canvas has got, when they're added a page at a time.
public sealed class PatternPaging
{
    // Where the next page starts.
    public int Offset { get; private set; }

    public int Shown { get; private set; }

    public int Remaining { get; private set; }

    public bool HasMore => Remaining > 0;

    public void Reset()
    {
        Offset = 0;
        Shown = 0;
        Remaining = 0;
    }

    // Records that a page of this size was added, with this many matches left after it.
    public void Advance(int pageSize, int remaining)
    {
        Offset += pageSize;
        Remaining = remaining;
        Shown = Math.Min(Offset, Offset + remaining);
    }
}
