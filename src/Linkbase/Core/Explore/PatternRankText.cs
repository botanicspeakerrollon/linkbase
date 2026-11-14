using Linkbase.Core.Graph;

namespace Linkbase.Core.Explore;

// Says in plain words what a ranking does, built from the pattern itself so it can't differ from what ran.
public static class PatternRankText
{
    public static string Describe(GraphPattern pattern)
    {
        if (pattern.Rank is not { } rank || rank.Group < 0 || rank.Count < 0
            || rank.Group >= pattern.Nodes.Count || rank.Count >= pattern.Nodes.Count)
        {
            return "";
        }

        var grouped = Name(pattern, rank.Group);
        var counted = Name(pattern, rank.Count);
        var subject = rank.Top == 1 ? $"The {grouped} record" : $"The {rank.Top} {grouped} records";
        var verb = rank.Top == 1 ? "reaches" : "reach";

        var (from, to) = (Math.Min(rank.Group, rank.Count), Math.Max(rank.Group, rank.Count));
        var between = Enumerable.Range(from + 1, Math.Max(0, to - from - 1)).Select(i => Name(pattern, i)).ToList();
        var way = between.Count == 0 ? "directly" : $"by way of {Join(between)}";

        return $"{subject} that {verb} the most different {counted} records, {way}. "
            + "Only links that fit every condition of the search are counted, and records with none aren't listed.";
    }

    private static string Name(GraphPattern pattern, int index) => pattern.Nodes[index].Label ?? "linked";

    private static string Join(List<string> names) =>
        names.Count == 1 ? names[0] : string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1];
}
