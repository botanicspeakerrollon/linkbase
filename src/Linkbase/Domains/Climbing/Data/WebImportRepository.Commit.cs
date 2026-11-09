using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Linkbase.Core.Records;
using Linkbase.Data;
using Linkbase.Domains.Climbing.Import;
using Linkbase.Domains.Climbing.Import.Web;
using Microsoft.EntityFrameworkCore;

namespace Linkbase.Domains.Climbing.Data;
public sealed partial class WebImportRepository
{
    public async Task<CommitSummary> CommitAsync(long batchId, IReadOnlyList<StagedCrag> crags, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var batch = await db.ImportBatches.SingleOrDefaultAsync(b => b.Id == batchId, cancellationToken)
            ?? throw new LinkbaseValidationException("This import no longer exists.");
        if (batch.Status != ImportStatus.Staged)
        {
            throw new LinkbaseValidationException("This import has already been committed or undone.");
        }

        var included = crags.Where(c => c.Include).ToList();
        Validate(included);

        var (areasCreated, cragsCreated, cragsMatched, routesCreated, routesMatched) = (0, 0, 0, 0, 0);
        var refBase = RefBase(batch.SourceUrl, batchId);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var areas = await LoadAreasAsync(db, cancellationToken);
        await CheckChosenRecordsExistAsync(db, included, areas, cancellationToken);
        var knownRefs = (await db.ExternalRefs.AsNoTracking()
                .Where(r => r.Source == DataSource.WebImport)
                .Select(r => new { r.NodeType, r.ExternalId })
                .ToListAsync(cancellationToken))
            .Select(r => (r.NodeType, r.ExternalId))
            .ToHashSet();
        var nextKey = new Dictionary<string, long>();
        foreach (var type in new[] { "Area", "Crag", "Route" })
        {
            var keys = await db.Nodes.AsNoTracking().Where(n => n.Type == type).Select(n => n.Key).ToListAsync(cancellationToken);
            nextKey[type] = keys.Select(k => long.TryParse(k, CultureInfo.InvariantCulture, out var number) ? number : 0).DefaultIfEmpty(0).Max();
        }

        var links = new List<(string Source, string SourceType, string Rel, string Target, string TargetType)>();

        string NewNode(string type, string name, JsonObject props)
        {
            var key = (++nextKey[type]).ToString(CultureInfo.InvariantCulture);
            var node = new Node { Id = $"{type.ToLowerInvariant()}:{key}", UserId = AppUser.DefaultId, Type = type, Key = key, Name = name, Props = props.ToJsonString(), ImportBatchId = batchId };
            db.Nodes.Add(node);
            return node.Id;
        }

        string ResolveArea(StagedCrag staged)
        {
            if (staged.AreaId is { } areaId)
            {
                return areaId;
            }

            var name = staged.NewAreaName!.Trim();
            var parentId = staged.NewAreaParentId;
            var existing = areas.Values.FirstOrDefault(a => a.ParentId == parentId && a.Name == name);
            if (existing is not null)
            {
                return existing.Id;
            }

            var kind = parentId is null ? AreaKind.Country : areas[parentId].Kind == AreaKind.Country ? AreaKind.Region : AreaKind.Area;
            var id = NewNode("Area", name, new JsonObject { ["kind"] = SnakeCaseEnumConverter<AreaKind>.ToStore(kind) });
            if (parentId is not null)
            {
                links.Add((id, "Area", "IN", parentId, "Area"));
            }

            areas[id] = new AreaRow(id, parentId, name, kind);
            areasCreated++;
            return id;
        }

        foreach (var staged in included)
        {
            string cragId;
            if (staged.UsesExisting)
            {
                cragId = staged.Choice;
                cragsMatched++;
            }
            else
            {
                var areaId = ResolveArea(staged);
                var props = new JsonObject();
                SetText(props, "rock_type", staged.RockType);
                if (staged.Lat is { } lat)
                {
                    props["lat"] = lat;
                }

                if (staged.Lon is { } lon)
                {
                    props["lon"] = lon;
                }

                cragId = NewNode("Crag", staged.Name.Trim(), props);
                links.Add((cragId, "Crag", "IN", areaId, "Area"));
                cragsCreated++;
            }

            AddRef(db, knownRefs, cragId, "Crag", CragKey(refBase, staged.Name), batch.SourceUrl, batchId);

            foreach (var stagedRoute in staged.Routes.Where(r => r.Include))
            {
                string routeId;
                if (stagedRoute.UsesExisting)
                {
                    routeId = stagedRoute.Choice;
                    routesMatched++;
                }
                else
                {
                    var props = new JsonObject();
                    SetText(props, "grade", stagedRoute.Grade);
                    if (stagedRoute.GradeSystem is { } system)
                    {
                        props["grade_system"] = SnakeCaseEnumConverter<GradeSystem>.ToStore(system);
                    }

                    if (stagedRoute.Discipline is { } discipline)
                    {
                        props["discipline"] = SnakeCaseEnumConverter<Discipline>.ToStore(discipline);
                    }

                    if (stagedRoute.LengthM is { } length)
                    {
                        props["length_m"] = length;
                    }

                    if (stagedRoute.Pitches is { } pitches)
                    {
                        props["pitches"] = pitches;
                    }

                    if (GradeRanking.Rank(stagedRoute.GradeSystem, Clean(stagedRoute.Grade)) is { } rank)
                    {
                        props["grade_rank"] = rank;
                    }

                    routeId = NewNode("Route", stagedRoute.Name.Trim(), props);
                    links.Add((routeId, "Route", "AT", cragId, "Crag"));
                    routesCreated++;
                }

                AddRef(db, knownRefs, routeId, "Route", RouteKey(refBase, staged.Name, stagedRoute.Name), batch.SourceUrl, batchId);
            }
        }

        // Nodes first, so the links' ends exist.
        await db.SaveChangesAsync(cancellationToken);
        foreach (var link in links)
        {
            db.NodeLinks.Add(new NodeLink { SourceId = link.Source, RelType = link.Rel, TargetId = link.Target, ImportBatchId = batchId });
        }

        await db.SaveChangesAsync(cancellationToken);

        // Each relationship's count is kept with its definition.
        foreach (var group in links.GroupBy(l => (l.SourceType, l.Rel, l.TargetType)))
        {
            var added = (long)group.Count();
            await db.LinkDefinitions
                .Where(d => d.SourceType == group.Key.SourceType && d.RelType == group.Key.Rel && d.TargetType == group.Key.TargetType)
                .ExecuteUpdateAsync(s => s.SetProperty(d => d.Links, d => d.Links + added), cancellationToken);
        }

        batch.Status = ImportStatus.Committed;
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new CommitSummary(areasCreated, cragsCreated, cragsMatched, routesCreated, routesMatched);
    }

    private static void Validate(IReadOnlyList<StagedCrag> crags)
    {
        foreach (var crag in crags)
        {
            if (string.IsNullOrWhiteSpace(crag.Name))
            {
                throw new LinkbaseValidationException("A crag has no name.");
            }

            if (crag.Choice == StagedRow.Undecided)
            {
                throw new LinkbaseValidationException($"Choose a match for crag '{crag.Name}', or untick it.");
            }

            if (crag.Choice == StagedRow.CreateNew)
            {
                var newArea = !string.IsNullOrWhiteSpace(crag.NewAreaName);
                if (crag.AreaId is null && !newArea)
                {
                    throw new LinkbaseValidationException($"Choose an area for the new crag '{crag.Name}'.");
                }

                if (crag.AreaId is null && crag.NewAreaParentId is null && crag.NewAreaKind != AreaKind.Country)
                {
                    throw new LinkbaseValidationException($"Choose which area '{crag.NewAreaName}' is in, or mark it as a country, for the new crag '{crag.Name}'.");
                }
            }

            foreach (var route in crag.Routes.Where(r => r.Include))
            {
                if (string.IsNullOrWhiteSpace(route.Name))
                {
                    throw new LinkbaseValidationException($"A route at '{crag.Name}' has no name.");
                }

                if (route.Choice == StagedRow.Undecided)
                {
                    throw new LinkbaseValidationException($"Choose a match for route '{route.Name}', or untick it.");
                }

                if (route.LengthM < 0 || route.Pitches < 0)
                {
                    throw new LinkbaseValidationException($"Length and pitches of '{route.Name}' can't be negative.");
                }
            }
        }
    }

    // A source's id is added once, whichever batch brought it first.
    private static void AddRef(
        LinkbaseDbContext db, HashSet<(string NodeType, string ExternalId)> known, string nodeId, string nodeType, string externalId, string? url, long batchId)
    {
        if (!known.Add((nodeType, externalId)))
        {
            return;
        }

        db.ExternalRefs.Add(new ExternalRef
        {
            NodeId = nodeId,
            NodeType = nodeType,
            Source = DataSource.WebImport,
            ExternalId = externalId,
            Url = url,
            ImportBatchId = batchId,
        });
    }

    // What the review chose may have gone, or never been a crag, route or area, since the review was loaded. Better a clear
    // message before anything is written than a foreign key failing part-way.
    private static async Task CheckChosenRecordsExistAsync(
        LinkbaseDbContext db, IReadOnlyList<StagedCrag> crags, Dictionary<string, AreaRow> areas, CancellationToken cancellationToken)
    {
        var chosen = new Dictionary<string, (string Type, string Name)>();
        foreach (var crag in crags)
        {
            if (crag.UsesExisting)
            {
                chosen[crag.Choice] = ("Crag", crag.Name);
            }

            foreach (var route in crag.Routes.Where(r => r.Include && r.UsesExisting))
            {
                chosen[route.Choice] = ("Route", route.Name);
            }

            if (crag.Choice == StagedRow.CreateNew)
            {
                foreach (var areaId in new[] { crag.AreaId, crag.NewAreaParentId }.OfType<string>())
                {
                    if (!areas.ContainsKey(areaId))
                    {
                        throw new LinkbaseValidationException($"The area chosen for '{crag.Name}' is no longer there. Choose another.");
                    }
                }
            }
        }

        var ids = chosen.Keys.ToList();
        var found = await db.Nodes.AsNoTracking().Where(n => ids.Contains(n.Id)).Select(n => new { n.Id, n.Type }).ToDictionaryAsync(n => n.Id, n => n.Type, cancellationToken);
        foreach (var (id, (type, name)) in chosen)
        {
            if (!found.TryGetValue(id, out var actual) || actual != type)
            {
                throw new LinkbaseValidationException($"The {type.ToLowerInvariant()} chosen for '{name}' is no longer there. Review the import again.");
            }
        }
    }

    private static async Task<Dictionary<(string NodeType, string ExternalId), string>> LoadRefsAsync(LinkbaseDbContext db, CancellationToken cancellationToken)
    {
        var refs = await db.ExternalRefs.AsNoTracking()
            .Where(r => r.Source == DataSource.WebImport && r.NodeId != null)
            .Select(r => new { NodeType = r.NodeType!, r.ExternalId, NodeId = r.NodeId! })
            .ToListAsync(cancellationToken);
        return refs.ToDictionary(r => (r.NodeType, r.ExternalId), r => r.NodeId);
    }
}
