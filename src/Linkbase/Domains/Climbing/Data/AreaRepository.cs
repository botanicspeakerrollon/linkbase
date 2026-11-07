using Linkbase.Data;
using Microsoft.EntityFrameworkCore;

namespace Linkbase.Domains.Climbing.Data;

public sealed record AreaOption(long Id, string Name, string Path);

public sealed record AreaSummary(long Id, string Name, AreaKind Kind, int Crags, int Ascents);

public sealed record CragSummary(long Id, string Name, int Routes, int Ascents);

public sealed record AreaCrumb(long Id, string Name);

public sealed record AreaContents(
    long? Id,
    string? Name,
    IReadOnlyList<AreaCrumb> Path,
    IReadOnlyList<AreaSummary> SubAreas,
    IReadOnlyList<CragSummary> Crags);

public sealed class AreaRepository(IDbContextFactory<LinkbaseDbContext> contextFactory)
{
    public async Task<AreaContents?> GetContentsAsync(long? areaId, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        var areas = await LoadAreasAsync(db, cancellationToken);
        if (areaId is { } id && !areas.ContainsKey(id))
        {
            return null;
        }

        var crags = await db.Crags.AsNoTracking()
            .Select(c => new
            {
                c.Id,
                c.Name,
                c.AreaId,
                Routes = c.Routes.Count,
                Ascents = c.Routes.SelectMany(r => r.Ascents).Count(),
            })
            .ToListAsync(cancellationToken);

        // Roll counts up the tree, because a Mountain Project crag can sit many levels below its country.
        var cragCounts = new Dictionary<long, int>();
        var ascentCounts = new Dictionary<long, int>();
        foreach (var crag in crags)
        {
            for (long? current = crag.AreaId; current is { } a; current = areas[a].ParentId)
            {
                cragCounts[a] = cragCounts.GetValueOrDefault(a) + 1;
                ascentCounts[a] = ascentCounts.GetValueOrDefault(a) + crag.Ascents;
            }
        }

        var subAreas = areas.Values
            .Where(a => a.ParentId == areaId)
            .Select(a => new AreaSummary(a.Id, a.Name, a.Kind, cragCounts.GetValueOrDefault(a.Id), ascentCounts.GetValueOrDefault(a.Id)))
            .OrderByDescending(a => a.Ascents)
            .ThenBy(a => a.Name)
            .ToList();

        var directCrags = crags
            .Where(c => c.AreaId == areaId)
            .Select(c => new CragSummary(c.Id, c.Name, c.Routes, c.Ascents))
            .OrderBy(c => c.Name)
            .ToList();

        return new AreaContents(
            areaId,
            areaId is { } current2 ? areas[current2].Name : null,
            areaId is { } current3 ? PathTo(areas, current3) : [],
            subAreas,
            directCrags);
    }

    public async Task<IReadOnlyList<AreaCrumb>> PathAsync(long areaId, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return PathTo(await LoadAreasAsync(db, cancellationToken), areaId);
    }

    public async Task<IReadOnlyList<AreaOption>> SearchAsync(string? text, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var areas = await LoadAreasAsync(db, cancellationToken);
        return areas.Values
            .Where(a => string.IsNullOrWhiteSpace(text) || a.Name.Contains(text.Trim(), StringComparison.OrdinalIgnoreCase))
            .Select(a => new AreaOption(a.Id, a.Name, string.Join(" > ", PathTo(areas, a.Id).Select(p => p.Name))))
            .OrderBy(a => a.Path.Length - a.Name.Length)
            .ThenBy(a => a.Name)
            .Take(20)
            .ToList();
    }

    public async Task<List<long>> DescendantIdsAsync(long areaId, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.Database.SqlQuery<long>(
            $"""
            WITH RECURSIVE tree AS (
                SELECT id FROM area WHERE id = {areaId}
                UNION ALL
                SELECT a.id FROM area a JOIN tree ON a.parent_id = tree.id
            )
            SELECT id AS "Value" FROM tree
            """).ToListAsync(cancellationToken);
    }

    private static async Task<Dictionary<long, AreaRow>> LoadAreasAsync(LinkbaseDbContext db, CancellationToken cancellationToken) =>
        await db.Areas.AsNoTracking()
            .Select(a => new AreaRow(a.Id, a.ParentId, a.Name, a.Kind))
            .ToDictionaryAsync(a => a.Id, cancellationToken);

    private static List<AreaCrumb> PathTo(Dictionary<long, AreaRow> areas, long areaId)
    {
        var path = new List<AreaCrumb>();
        for (long? current = areaId; current is { } id; current = areas[id].ParentId)
        {
            path.Insert(0, new AreaCrumb(id, areas[id].Name));
        }

        return path;
    }

    private sealed record AreaRow(long Id, long? ParentId, string Name, AreaKind Kind);
}
