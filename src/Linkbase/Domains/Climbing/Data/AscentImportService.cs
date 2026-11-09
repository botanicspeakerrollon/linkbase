using Linkbase.Data;
using System.Text.Json;
using Linkbase.Domains.Climbing.Import;
using Microsoft.EntityFrameworkCore;

namespace Linkbase.Domains.Climbing.Data;

public sealed record ImportCounts(int Created, int Matched);

public sealed record ImportSummary(
    long BatchId,
    int Rows,
    ImportCounts Areas,
    ImportCounts Crags,
    ImportCounts Routes,
    ImportCounts Ascents,
    int PartnersCreated);

// Loads parsed exports in one transaction. A row is matched by external_ref first, then by an
// identical name in the same place. Similar names are never merged: the new row is created.
public sealed class AscentImportService(IDbContextFactory<LinkbaseDbContext> contextFactory)
{
    private const double SimilarityThreshold = 0.5;

    public async Task<ImportSummary> ImportAsync(
        DataSource source,
        string? sourceUrl,
        IReadOnlyList<ParsedAscent> rows,
        CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var batch = new ImportBatch
        {
            UserId = AppUser.DefaultId,
            Source = source,
            SourceUrl = sourceUrl,
            Status = ImportStatus.Committed,
            RawPayload = JsonSerializer.Serialize(rows.Select(r => r.Raw)),
        };
        db.ImportBatches.Add(batch);
        await db.SaveChangesAsync(cancellationToken);

        var run = new ImportRun(db, source, batch.Id, cancellationToken);
        await run.LoadCachesAsync();
        foreach (var row in rows)
        {
            await run.ImportRowAsync(row);
        }

        await transaction.CommitAsync(cancellationToken);
        return new ImportSummary(
            batch.Id,
            rows.Count,
            new ImportCounts(run.AreasCreated, run.AreasMatched),
            new ImportCounts(run.CragsCreated, run.CragsMatched),
            new ImportCounts(run.RoutesCreated, run.RoutesMatched),
            new ImportCounts(run.AscentsCreated, run.AscentsMatched),
            run.PartnersCreated);
    }

    private sealed class ImportRun(LinkbaseDbContext db, DataSource source, long batchId, CancellationToken cancellationToken)
    {
        private readonly Dictionary<(ExternalEntityType Type, string ExternalId), long> _refs = [];
        private readonly Dictionary<(long? ParentId, string Name), long> _areas = [];
        private readonly Dictionary<long, long?> _areaParents = [];
        private readonly Dictionary<string, long> _partners = new(StringComparer.Ordinal);
        private readonly HashSet<long> _areasMatchedThisRun = [];
        private readonly HashSet<long> _cragsMatchedThisRun = [];
        private readonly HashSet<long> _routesMatchedThisRun = [];

        public int AreasCreated { get; private set; }
        public int AreasMatched => _areasMatchedThisRun.Count;
        public int CragsCreated { get; private set; }
        public int CragsMatched => _cragsMatchedThisRun.Count;
        public int RoutesCreated { get; private set; }
        public int RoutesMatched => _routesMatchedThisRun.Count;
        public int AscentsCreated { get; private set; }
        public int AscentsMatched { get; private set; }
        public int PartnersCreated { get; private set; }

        public async Task LoadCachesAsync()
        {
            var refs = await db.ExternalRefs.AsNoTracking()
                .Where(r => r.Source == source && r.EntityType != null)
                .Select(r => new { EntityType = r.EntityType!.Value, r.ExternalId, EntityId = r.EntityId!.Value })
                .ToListAsync(cancellationToken);
            foreach (var r in refs)
            {
                _refs[(r.EntityType, r.ExternalId)] = r.EntityId;
            }

            var areas = await db.Areas.AsNoTracking()
                .Select(a => new { a.Id, a.ParentId, a.Name })
                .ToListAsync(cancellationToken);
            foreach (var a in areas)
            {
                _areas[(a.ParentId, a.Name)] = a.Id;
                _areaParents[a.Id] = a.ParentId;
            }

            var partners = await db.Partners.AsNoTracking()
                .Select(p => new { p.Id, p.Name })
                .ToListAsync(cancellationToken);
            foreach (var p in partners)
            {
                _partners[p.Name] = p.Id;
            }
        }

        public async Task ImportRowAsync(ParsedAscent row)
        {
            var areaId = await ResolveAreaAsync(row.AreaPath);
            var cragId = await ResolveCragAsync(row, areaId);
            var routeId = await ResolveRouteAsync(row, cragId);
            await ImportAscentAsync(row, routeId);
        }

        private async Task<long> ResolveAreaAsync(IReadOnlyList<string> path)
        {
            long? parentId = null;
            for (var level = 0; level < path.Count; level++)
            {
                var name = path[level];
                if (_areas.TryGetValue((parentId, name), out var existingId))
                {
                    _areasMatchedThisRun.Add(existingId);
                    parentId = existingId;
                    continue;
                }

                var area = new Area
                {
                    UserId = AppUser.DefaultId,
                    ParentId = parentId,
                    Name = name,
                    Kind = level switch { 0 => AreaKind.Country, 1 => AreaKind.Region, _ => AreaKind.Area },
                    ImportBatchId = batchId,
                };
                db.Areas.Add(area);
                await SaveAsync();
                await AddRefAsync(ExternalEntityType.Area, area.Id, ExportText.ExternalKey(string.Join(" > ", path.Take(level + 1))));

                _areas[(parentId, name)] = area.Id;
                _areaParents[area.Id] = parentId;
                AreasCreated++;
                parentId = area.Id;
            }

            return parentId ?? throw new InvalidOperationException("A row has no area.");
        }

        private async Task<long> ResolveCragAsync(ParsedAscent row, long areaId)
        {
            var externalId = ExportText.ExternalKey($"{string.Join(" > ", row.AreaPath)} > {row.CragName}");
            if (_refs.TryGetValue((ExternalEntityType.Crag, externalId), out var refId))
            {
                _cragsMatchedThisRun.Add(refId);
                return refId;
            }

            var scope = AreasInSameCountry(areaId);
            var candidates = await db.Crags.AsNoTracking()
                .Where(c => scope.Contains(c.AreaId) && EF.Functions.TrigramsSimilarity(c.Name, row.CragName) >= SimilarityThreshold)
                .Select(c => new Candidate(c.Id, c.Name, c.AreaId))
                .ToListAsync(cancellationToken);

            var normalised = NameMatching.Normalise(row.CragName);
            var exact = candidates.Where(c => c.ParentId == areaId && NameMatching.Normalise(c.Name) == normalised).ToList();
            if (exact.Count == 1)
            {
                _cragsMatchedThisRun.Add(exact[0].Id);
                await AddRefAsync(ExternalEntityType.Crag, exact[0].Id, externalId);
                return exact[0].Id;
            }

            var crag = new Crag { UserId = AppUser.DefaultId, AreaId = areaId, Name = row.CragName, ImportBatchId = batchId };
            db.Crags.Add(crag);
            await SaveAsync();
            await AddRefAsync(ExternalEntityType.Crag, crag.Id, externalId);
            CragsCreated++;
            return crag.Id;
        }

        private async Task<long> ResolveRouteAsync(ParsedAscent row, long cragId)
        {
            if (_refs.TryGetValue((ExternalEntityType.Route, row.RouteExternalId), out var refId))
            {
                _routesMatchedThisRun.Add(refId);
                return refId;
            }

            var candidates = await db.Routes.AsNoTracking()
                .Where(r => r.CragId == cragId && EF.Functions.TrigramsSimilarity(r.Name, row.RouteName) >= SimilarityThreshold)
                .Select(r => new Candidate(r.Id, r.Name, r.CragId))
                .ToListAsync(cancellationToken);

            var normalised = NameMatching.Normalise(row.RouteName);
            var exact = candidates.Where(c => NameMatching.Normalise(c.Name) == normalised).ToList();
            if (exact.Count == 1)
            {
                _routesMatchedThisRun.Add(exact[0].Id);
                await AddRefAsync(ExternalEntityType.Route, exact[0].Id, row.RouteExternalId, row.RouteUrl);
                return exact[0].Id;
            }

            var route = new Route
            {
                UserId = AppUser.DefaultId,
                CragId = cragId,
                Name = row.RouteName,
                Grade = row.Grade,
                GradeSystem = row.GradeSystem,
                Discipline = row.Discipline,
                LengthM = row.LengthM,
                Pitches = row.Pitches,
                ImportBatchId = batchId,
            };
            db.Routes.Add(route);
            await SaveAsync();
            await AddRefAsync(ExternalEntityType.Route, route.Id, row.RouteExternalId, row.RouteUrl);
            RoutesCreated++;
            return route.Id;
        }

        private async Task ImportAscentAsync(ParsedAscent row, long routeId)
        {
            if (_refs.ContainsKey((ExternalEntityType.Ascent, row.AscentExternalId)))
            {
                AscentsMatched++;
                return;
            }

            var ascent = new Ascent
            {
                UserId = AppUser.DefaultId,
                RouteId = routeId,
                Date = row.Date,
                DatePrecision = row.DatePrecision,
                Style = row.Style,
                StyleRaw = row.StyleRaw,
                Notes = row.Notes,
                Source = source,
                ImportBatchId = batchId,
            };
            foreach (var name in row.Partners)
            {
                ascent.Partners.Add(new AscentPartner { PartnerId = await ResolvePartnerAsync(name) });
            }

            db.Ascents.Add(ascent);
            await SaveAsync();
            await AddRefAsync(ExternalEntityType.Ascent, ascent.Id, row.AscentExternalId);
            AscentsCreated++;
        }

        private async Task<long> ResolvePartnerAsync(string name)
        {
            if (_partners.TryGetValue(name, out var id))
            {
                return id;
            }

            var partner = new Partner { UserId = AppUser.DefaultId, Name = name, ImportBatchId = batchId };
            db.Partners.Add(partner);
            await SaveAsync();
            _partners[name] = partner.Id;
            PartnersCreated++;
            return partner.Id;
        }

        private async Task AddRefAsync(ExternalEntityType type, long entityId, string externalId, string? url = null)
        {
            db.ExternalRefs.Add(new ExternalRef
            {
                EntityType = type,
                EntityId = entityId,
                Source = source,
                ExternalId = externalId,
                Url = url,
                ImportBatchId = batchId,
            });
            await SaveAsync();
            _refs[(type, externalId)] = entityId;
        }

        private List<long> AreasInSameCountry(long areaId)
        {
            var root = RootOf(areaId);
            return _areaParents.Keys.Where(id => RootOf(id) == root).ToList();
        }

        private long RootOf(long areaId)
        {
            while (_areaParents[areaId] is { } parentId)
            {
                areaId = parentId;
            }

            return areaId;
        }

        // Clearing keeps the tracker small; otherwise saving gets slower with every row.
        private async Task SaveAsync()
        {
            await db.SaveChangesAsync(cancellationToken);
            db.ChangeTracker.Clear();
        }

        // Names close enough to be worth comparing after normalising. The trigram filter can use an index.
        private sealed record Candidate(long Id, string Name, long ParentId);
    }
}
