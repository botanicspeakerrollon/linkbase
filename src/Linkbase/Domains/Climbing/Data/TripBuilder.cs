using Linkbase.Data;

using Linkbase.Domains.Climbing.Import;

namespace Linkbase.Domains.Climbing.Data;

public sealed record TripAscent(long Id, string Country, DateOnly Date, DatePrecision Precision);

public sealed record PlannedTrip
{
    public required string Key { get; init; }
    public required string Name { get; init; }
    public required string Country { get; init; }
    public DateOnly? StartDate { get; init; }
    public DateOnly? EndDate { get; init; }
    public required DateOnly SortDate { get; init; }
    public string? Notes { get; init; }
    public bool Abroad { get; init; }
    public bool Home { get; init; }
    public bool Approximate { get; init; }
    public bool DatesPartlyUnknown { get; init; }
    public bool Manual { get; init; }
    public List<long> AscentIds { get; init; } = [];
    public List<long> InferredAscentIds { get; init; } = [];
}

// A trip is consecutive climbing days in one country. Ported from rebuild.cypher, section 5.
public static class TripBuilder
{
    private const int UkGapDays = 3;
    private const int AbroadGapDays = 7;
    private const string HomeCountry = "England";
    private static readonly string[] UkCountries = ["England", "Wales", "Scotland", "Northern Ireland"];
    private static readonly string[] MonthNames = ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"];

    public static List<PlannedTrip> Build(IReadOnlyCollection<TripAscent> ascents, IReadOnlyList<TripMerge> merges)
    {
        var trips = TripsFromDatedAscents(ascents);
        JoinOverlappingTrip(ascents, trips);
        trips.AddRange(TripsForUndatedAscents(ascents, trips));
        foreach (var merge in merges)
        {
            trips = ApplyMerge(trips, merge);
        }

        return trips;
    }

    private static List<PlannedTrip> TripsFromDatedAscents(IReadOnlyCollection<TripAscent> ascents)
    {
        var dated = ascents.Where(a => a.Precision == DatePrecision.Day).ToList();
        var days = dated.Select(a => (a.Country, a.Date)).Distinct().OrderBy(d => d.Date).ThenBy(d => d.Country, StringComparer.Ordinal);

        var runs = new List<List<(string Country, DateOnly Date)>>();
        foreach (var day in days)
        {
            var current = runs.LastOrDefault();
            if (current is not null
                && current[^1].Country == day.Country
                && day.Date.DayNumber - current[^1].Date.DayNumber <= GapDays(day.Country))
            {
                current.Add(day);
            }
            else
            {
                runs.Add([day]);
            }
        }

        var byCountryAndDate = dated.ToLookup(a => (a.Country, a.Date));
        var trips = new List<PlannedTrip>();
        foreach (var run in runs.Where(r => r.Count >= 2 || r[0].Country != HomeCountry))
        {
            var (country, start) = run[0];
            trips.Add(new PlannedTrip
            {
                Key = $"{country} {start:yyyy-MM-dd}",
                Name = $"{country} · {MonthNames[start.Month - 1]} {start.Year}",
                Country = country,
                StartDate = start,
                EndDate = run[^1].Date,
                SortDate = start,
                Home = country == HomeCountry,
                Abroad = !UkCountries.Contains(country),
                AscentIds = run.SelectMany(day => byCountryAndDate[day]).Select(a => a.Id).ToList(),
            });
        }

        return trips;
    }

    // An ascent with no day joins a trip only when exactly one in its country overlaps its month.
    private static void JoinOverlappingTrip(IReadOnlyCollection<TripAscent> ascents, List<PlannedTrip> trips)
    {
        foreach (var ascent in ascents.Where(a => a.Precision == DatePrecision.Month))
        {
            var monthEnd = ascent.Date.AddMonths(1).AddDays(-1);
            var overlapping = trips
                .Where(t => t.Country == ascent.Country && t.StartDate <= monthEnd && t.EndDate >= ascent.Date)
                .ToList();
            if (overlapping.Count == 1)
            {
                overlapping[0].AscentIds.Add(ascent.Id);
                overlapping[0].InferredAscentIds.Add(ascent.Id);
            }
        }
    }

    // Other month-only ascents outside England get one trip per country and month, with unknown dates.
    private static IEnumerable<PlannedTrip> TripsForUndatedAscents(IReadOnlyCollection<TripAscent> ascents, List<PlannedTrip> trips)
    {
        var placed = trips.SelectMany(t => t.AscentIds).ToHashSet();
        return ascents
            .Where(a => a.Precision == DatePrecision.Month && a.Country != HomeCountry && !placed.Contains(a.Id))
            .GroupBy(a => (a.Country, a.Date))
            .Select(group =>
            {
                var (country, month) = group.Key;
                var ids = group.Select(a => a.Id).ToList();
                return new PlannedTrip
                {
                    Key = $"{country} {month:yyyy-MM-dd} (dates unknown)",
                    Name = $"{country} · {MonthNames[month.Month - 1]} {month.Year} (dates unknown)",
                    Country = country,
                    SortDate = month,
                    Abroad = !UkCountries.Contains(country),
                    Approximate = true,
                    AscentIds = ids,
                    InferredAscentIds = ids.ToList(),
                };
            });
    }

    private static List<PlannedTrip> ApplyMerge(List<PlannedTrip> trips, TripMerge merge)
    {
        var parts = trips
            .Where(t => !t.Manual && merge.Countries.Contains(t.Country) && t.SortDate >= merge.From && t.SortDate <= merge.To)
            .ToList();
        if (parts.Count <= 1)
        {
            return trips;
        }

        var start = parts.Min(p => p.StartDate);
        var merged = new PlannedTrip
        {
            Key = $"manual: {merge.Name}",
            Name = merge.Name,
            Country = string.Join(", ", merge.Countries),
            StartDate = start,
            EndDate = parts.Max(p => p.EndDate),
            SortDate = start ?? merge.From,
            Notes = merge.Notes,
            Abroad = true,
            DatesPartlyUnknown = parts.Any(p => p.Approximate),
            Manual = true,
            AscentIds = parts.SelectMany(p => p.AscentIds).ToList(),
            InferredAscentIds = parts.SelectMany(p => p.InferredAscentIds).ToList(),
        };

        return [.. trips.Except(parts), merged];
    }

    private static int GapDays(string country) => UkCountries.Contains(country) ? UkGapDays : AbroadGapDays;
}
