using System.Text.Json;
using Linkbase.Data;
using Linkbase.Domains.Climbing.Data;

namespace Linkbase.Domains.Climbing.Import;

public sealed record PartnerRename(string From, string To);

// Partners for Mountain Project ticks, which have no partner field. No areas means every tick.
public sealed record MountainProjectPartners(IReadOnlyList<string> Partners, IReadOnlyList<string> Areas);

public sealed record TripMerge(string Name, IReadOnlyList<string> Countries, DateOnly From, DateOnly To, string? Notes);

// Things known about the logbook that aren't in the exports. Loaded from data/corrections.json.
public sealed record Corrections(
    IReadOnlyList<PartnerRename> PartnerRenames,
    IReadOnlyList<MountainProjectPartners> MountainProjectPartners,
    IReadOnlyList<TripMerge> TripMerges)
{
    public static Corrections Load(string path) =>
        JsonSerializer.Deserialize<Corrections>(File.ReadAllText(path), new JsonSerializerOptions(JsonSerializerDefaults.Web))
        ?? throw new FormatException($"{path} is empty.");

    public ParsedAscent Apply(ParsedAscent ascent)
    {
        var partners = ascent.Partners.Select(Rename);
        if (ascent.Source == DataSource.MountainProject)
        {
            partners = partners.Concat(MountainProjectPartners
                .Where(rule => rule.Areas.Count == 0 || rule.Areas.Intersect(ascent.AreaPath).Any())
                .SelectMany(rule => rule.Partners));
        }

        return ascent with { Partners = partners.Distinct(StringComparer.Ordinal).ToList() };
    }

    private string Rename(string name) => PartnerRenames.FirstOrDefault(r => r.From == name)?.To ?? name;
}
