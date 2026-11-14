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
        var reach = rank.Top == 1 ? "reaches" : "reach";
        var have = rank.Top == 1 ? "has" : "have";

        var (from, to) = (Math.Min(rank.Group, rank.Count), Math.Max(rank.Group, rank.Count));
        var between = Enumerable.Range(from + 1, Math.Max(0, to - from - 1)).Select(i => Name(pattern, i)).ToList();
        var way = between.Count == 0 ? "directly" : $"by way of {Join(between)}";
        var order = rank.Fewest ? "fewest" : "most";

        var what = rank.Measure switch
        {
            RankMeasure.Matches => $"{order} matches with {counted} records, every match counted even when a record is reached more than once",
            RankMeasure.Sum => $"{(rank.Fewest ? "smallest" : "biggest")} total {rank.Key} across the {counted} records they reach",
            RankMeasure.Average => $"{(rank.Fewest ? "lowest" : "highest")} average {rank.Key} across the {counted} records they reach",
            RankMeasure.Highest => $"{(rank.Fewest ? "lowest" : "highest")} single {rank.Key} among the {counted} records they reach",
            _ => $"{order} different {counted} records",
        };
        var counting = rank.Measure switch
        {
            RankMeasure.Matches => "Only links that fit every condition of the search are counted",
            RankMeasure.Distinct => "Only links that fit every condition of the search are counted",
            _ => $"Each {counted} record adds its {rank.Key} once, and ones with no {rank.Key} are left out",
        };
        var verb = rank.Measure == RankMeasure.Distinct ? $"that {reach}" : $"that {have}";
        return $"{subject} {verb} the {what}, {way}. {counting}, and records with none aren't listed.";
    }

    // The heading over the figures of a ranking.
    public static string Column(GraphPattern pattern)
    {
        if (pattern.Rank is not { } rank || rank.Count < 0 || rank.Count >= pattern.Nodes.Count)
        {
            return "";
        }

        var counted = Name(pattern, rank.Count);
        return rank.Measure switch
        {
            RankMeasure.Matches => $"Matches with {counted} records",
            RankMeasure.Sum => $"Total {rank.Key}",
            RankMeasure.Average => $"Average {rank.Key}",
            RankMeasure.Highest => $"Highest {rank.Key}",
            _ => $"Different {counted} records",
        };
    }

    private static string Name(GraphPattern pattern, int index) => pattern.Nodes[index].Label ?? "linked";

    private static string Join(List<string> names) =>
        names.Count == 1 ? names[0] : string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1];
}
