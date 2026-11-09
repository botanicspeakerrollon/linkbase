using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Linkbase.Data;
using Linkbase.Domains.Climbing.Import;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

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

// Loads parsed UKC and Mountain Project rows as Area, Crag, Route, Ascent and Partner records with their links and external
// ids, as models/climbing/model.json describes them. One batch, one transaction, tagged so ImportStagingService.UndoAsync can
// take it back. A row is matched by an external id first, then by an identical name in the same place; similar names are
// never merged. The trips and the ascents' names follow
// from GenericTripService and the records themselves. See docs/history/climbing-to-generic-plan.md.
public sealed class GenericAscentImportService(IDbContextFactory<LinkbaseDbContext> contextFactory)
{
    public async Task<ImportSummary> ImportAsync(
        DataSource source, string? sourceUrl, IReadOnlyList<ParsedAscent> rows, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        long batch;
        await using (var create = new NpgsqlCommand(
            "INSERT INTO import_batch (user_id, source, source_url, status, raw_payload, created_at, updated_at) VALUES (@user, @source, @url, 'committed', @payload, now(), now()) RETURNING id",
            connection, transaction))
        {
            create.Parameters.Add(new NpgsqlParameter("user", NpgsqlDbType.Bigint) { Value = AppUser.DefaultId });
            create.Parameters.Add(new NpgsqlParameter("source", NpgsqlDbType.Text) { Value = SnakeCaseEnumConverter<DataSource>.ToStore(source) });
            create.Parameters.Add(new NpgsqlParameter("url", NpgsqlDbType.Text) { Value = (object?)sourceUrl ?? DBNull.Value });
            create.Parameters.Add(new NpgsqlParameter("payload", NpgsqlDbType.Jsonb) { Value = JsonSerializer.Serialize(rows.Select(r => r.Raw)) });
            batch = (long)(await create.ExecuteScalarAsync(cancellationToken))!;
        }

        var run = new Run(source);
        await run.LoadAsync(db, cancellationToken);
        foreach (var row in rows)
        {
            run.ImportRow(row);
        }

        await run.WriteAsync(connection, transaction, batch, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new ImportSummary(
            batch,
            rows.Count,
            new ImportCounts(run.Created["Area"], run.Matched["Area"].Count),
            new ImportCounts(run.Created["Crag"], run.Matched["Crag"].Count),
            new ImportCounts(run.Created["Route"], run.Matched["Route"].Count),
            new ImportCounts(run.Created["Ascent"], run.AscentsMatched),
            run.Created["Partner"]);
    }

    private sealed record NewNode(string Id, string Type, string Key, string Name, JsonObject Props);

    private sealed record NewLink(string SourceId, string SourceType, string RelType, string TargetId, string TargetType);

    private sealed record NewRef(string NodeId, string NodeType, string ExternalId, string? Url);

    // Everything in memory: a personal logbook is thousands of rows, and the checks are the same ones in the same order.
    private sealed class Run(DataSource source)
    {
        private readonly Dictionary<(string NodeType, string ExternalId), string> _refs = [];
        private readonly Dictionary<(string? ParentId, string Name), string> _areas = [];
        private readonly Dictionary<(string AreaId, string Normalised), List<string>> _crags = [];
        private readonly Dictionary<(string CragId, string Normalised), List<string>> _routes = [];
        private readonly Dictionary<string, string> _partners = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _routeNames = [];
        private readonly Dictionary<string, long> _lastKey = [];
        private readonly List<NewNode> _nodes = [];
        private readonly List<NewLink> _links = [];
        private readonly List<NewRef> _newRefs = [];

        public Dictionary<string, int> Created { get; } = new[] { "Area", "Crag", "Route", "Ascent", "Partner" }.ToDictionary(t => t, _ => 0);

        public Dictionary<string, HashSet<string>> Matched { get; } =
            new[] { "Area", "Crag", "Route" }.ToDictionary(t => t, _ => new HashSet<string>());

        public int AscentsMatched { get; private set; }

        public async Task LoadAsync(LinkbaseDbContext db, CancellationToken cancellationToken)
        {
            foreach (var r in await db.ExternalRefs.AsNoTracking()
                         .Where(r => r.Source == source && r.NodeId != null)
                         .Select(r => new { NodeType = r.NodeType!, r.ExternalId, NodeId = r.NodeId! })
                         .ToListAsync(cancellationToken))
            {
                _refs[(r.NodeType, r.ExternalId)] = r.NodeId;
            }

            foreach (var group in await db.Nodes.AsNoTracking().GroupBy(n => n.Type).Select(g => new { Type = g.Key, Keys = g.Select(n => n.Key).ToList() }).ToListAsync(cancellationToken))
            {
                _lastKey[group.Type] = group.Keys.Select(k => long.TryParse(k, CultureInfo.InvariantCulture, out var number) ? number : 0).DefaultIfEmpty(0).Max();
            }

            async Task<Dictionary<string, string>> ParentsAsync(string type, string rel) => await db.NodeLinks.AsNoTracking()
                .Where(l => l.RelType == rel && l.Source.Type == type)
                .Select(l => new { l.SourceId, l.TargetId })
                .ToDictionaryAsync(l => l.SourceId, l => l.TargetId, cancellationToken);

            var areaParents = await ParentsAsync("Area", "IN");
            foreach (var a in await db.Nodes.AsNoTracking().Where(n => n.Type == "Area").Select(n => new { n.Id, n.Name }).ToListAsync(cancellationToken))
            {
                _areas[(areaParents.GetValueOrDefault(a.Id), a.Name)] = a.Id;
            }

            var cragAreas = await ParentsAsync("Crag", "IN");
            foreach (var c in await db.Nodes.AsNoTracking().Where(n => n.Type == "Crag").Select(n => new { n.Id, n.Name }).ToListAsync(cancellationToken))
            {
                if (cragAreas.TryGetValue(c.Id, out var area))
                {
                    Add(_crags, (area, NameMatching.Normalise(c.Name)), c.Id);
                }
            }

            var routeCrags = await ParentsAsync("Route", "AT");
            foreach (var r in await db.Nodes.AsNoTracking().Where(n => n.Type == "Route").Select(n => new { n.Id, n.Name }).ToListAsync(cancellationToken))
            {
                _routeNames[r.Id] = r.Name;
                if (routeCrags.TryGetValue(r.Id, out var crag))
                {
                    Add(_routes, (crag, NameMatching.Normalise(r.Name)), r.Id);
                }
            }

            foreach (var p in await db.Nodes.AsNoTracking().Where(n => n.Type == "Partner").Select(n => new { n.Id, n.Name }).ToListAsync(cancellationToken))
            {
                _partners[p.Name] = p.Id;
            }
        }

        public void ImportRow(ParsedAscent row)
        {
            var area = ResolveArea(row.AreaPath);
            var crag = ResolveCrag(row, area);
            var route = ResolveRoute(row, crag);
            ImportAscent(row, route);
        }

        private string ResolveArea(IReadOnlyList<string> path)
        {
            string? parent = null;
            for (var level = 0; level < path.Count; level++)
            {
                var name = path[level];
                if (_areas.TryGetValue((parent, name), out var existing))
                {
                    Matched["Area"].Add(existing);
                    parent = existing;
                    continue;
                }

                var kind = level switch { 0 => AreaKind.Country, 1 => AreaKind.Region, _ => AreaKind.Area };
                var area = NewNodeOf("Area", name, ("kind", Choice(kind)));
                if (parent is not null)
                {
                    _links.Add(new NewLink(area, "Area", "IN", parent, "Area"));
                }

                _newRefs.Add(new NewRef(area, "Area", ExportText.ExternalKey(string.Join(" > ", path.Take(level + 1))), null));
                _refs[("Area", _newRefs[^1].ExternalId)] = area;
                _areas[(parent, name)] = area;
                parent = area;
            }

            return parent ?? throw new InvalidOperationException("A row has no area.");
        }

        private string ResolveCrag(ParsedAscent row, string area)
        {
            var externalId = ExportText.ExternalKey($"{string.Join(" > ", row.AreaPath)} > {row.CragName}");
            if (_refs.TryGetValue(("Crag", externalId), out var known))
            {
                Matched["Crag"].Add(known);
                return known;
            }

            var normalised = NameMatching.Normalise(row.CragName);
            if (_crags.TryGetValue((area, normalised), out var exact) && exact.Count == 1)
            {
                Matched["Crag"].Add(exact[0]);
                AddRef(exact[0], "Crag", externalId, null);
                return exact[0];
            }

            var crag = NewNodeOf("Crag", row.CragName);
            _links.Add(new NewLink(crag, "Crag", "IN", area, "Area"));
            AddRef(crag, "Crag", externalId, null);
            Add(_crags, (area, normalised), crag);
            return crag;
        }

        private string ResolveRoute(ParsedAscent row, string crag)
        {
            if (_refs.TryGetValue(("Route", row.RouteExternalId), out var known))
            {
                Matched["Route"].Add(known);
                return known;
            }

            var normalised = NameMatching.Normalise(row.RouteName);
            if (_routes.TryGetValue((crag, normalised), out var exact) && exact.Count == 1)
            {
                Matched["Route"].Add(exact[0]);
                AddRef(exact[0], "Route", row.RouteExternalId, row.RouteUrl);
                return exact[0];
            }

            var route = NewNodeOf("Route", row.RouteName,
                ("grade", Text(row.Grade)),
                ("grade_system", row.GradeSystem is { } system ? Choice(system) : null),
                ("discipline", row.Discipline is { } discipline ? Choice(discipline) : null),
                ("length_m", row.LengthM is { } length ? JsonValue.Create(length) : null),
                ("pitches", row.Pitches is { } pitches ? JsonValue.Create(pitches) : null),
                ("grade_rank", GradeRanking.Rank(row.GradeSystem, row.Grade) is { } rank ? JsonValue.Create(rank) : null));
            _routeNames[route] = row.RouteName;
            _links.Add(new NewLink(route, "Route", "AT", crag, "Crag"));
            AddRef(route, "Route", row.RouteExternalId, row.RouteUrl);
            Add(_routes, (crag, normalised), route);
            return route;
        }

        private void ImportAscent(ParsedAscent row, string route)
        {
            if (_refs.ContainsKey(("Ascent", row.AscentExternalId)))
            {
                AscentsMatched++;
                return;
            }

            // An ascent's name is its route's.
            var ascent = NewNodeOf("Ascent", _routeNames[route],
                ("date", JsonValue.Create(row.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))),
                ("date_precision", Choice(row.DatePrecision)),
                ("style", Choice(row.Style)),
                ("style_raw", Text(row.StyleRaw)),
                ("notes", Text(row.Notes)),
                ("source", Choice(source)));
            _links.Add(new NewLink(ascent, "Ascent", "OF", route, "Route"));
            foreach (var name in row.Partners)
            {
                _links.Add(new NewLink(ascent, "Ascent", "WITH", ResolvePartner(name), "Partner"));
            }

            AddRef(ascent, "Ascent", row.AscentExternalId, null);
        }

        private string ResolvePartner(string name)
        {
            if (_partners.TryGetValue(name, out var id))
            {
                return id;
            }

            id = NewNodeOf("Partner", name);
            _partners[name] = id;
            return id;
        }

        private void AddRef(string nodeId, string nodeType, string externalId, string? url)
        {
            _newRefs.Add(new NewRef(nodeId, nodeType, externalId, url));
            _refs[(nodeType, externalId)] = nodeId;
        }

        // A key is the next number for its type, so ids look like crag:13 and carry on from what the store holds.
        private string NewNodeOf(string type, string name, params (string Key, JsonNode? Value)[] values)
        {
            var key = (_lastKey.GetValueOrDefault(type) + 1).ToString(CultureInfo.InvariantCulture);
            _lastKey[type] = long.Parse(key, CultureInfo.InvariantCulture);
            var props = new JsonObject();
            foreach (var (field, value) in values)
            {
                if (value is not null)
                {
                    props[field] = value;
                }
            }

            var node = new NewNode($"{type.ToLowerInvariant()}:{key}", type, key, name, props);
            _nodes.Add(node);
            Created[type]++;
            return node.Id;
        }

        public async Task WriteAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long batch, CancellationToken cancellationToken)
        {
            await using (var import = await connection.BeginBinaryImportAsync(
                "COPY node (id, user_id, type, key, name, props, import_batch_id) FROM STDIN (FORMAT BINARY)", cancellationToken))
            {
                foreach (var node in _nodes)
                {
                    await import.StartRowAsync(cancellationToken);
                    await import.WriteAsync(node.Id, NpgsqlDbType.Text, cancellationToken);
                    await import.WriteAsync(AppUser.DefaultId, NpgsqlDbType.Bigint, cancellationToken);
                    await import.WriteAsync(node.Type, NpgsqlDbType.Text, cancellationToken);
                    await import.WriteAsync(node.Key, NpgsqlDbType.Text, cancellationToken);
                    await import.WriteAsync(node.Name, NpgsqlDbType.Text, cancellationToken);
                    await import.WriteAsync(node.Props.ToJsonString(), NpgsqlDbType.Jsonb, cancellationToken);
                    await import.WriteAsync(batch, NpgsqlDbType.Bigint, cancellationToken);
                }

                await import.CompleteAsync(cancellationToken);
            }

            await using (var import = await connection.BeginBinaryImportAsync(
                "COPY node_link (source_id, rel_type, target_id, import_batch_id) FROM STDIN (FORMAT BINARY)", cancellationToken))
            {
                foreach (var link in _links)
                {
                    await import.StartRowAsync(cancellationToken);
                    await import.WriteAsync(link.SourceId, NpgsqlDbType.Text, cancellationToken);
                    await import.WriteAsync(link.RelType, NpgsqlDbType.Text, cancellationToken);
                    await import.WriteAsync(link.TargetId, NpgsqlDbType.Text, cancellationToken);
                    await import.WriteAsync(batch, NpgsqlDbType.Bigint, cancellationToken);
                }

                await import.CompleteAsync(cancellationToken);
            }

            await using (var import = await connection.BeginBinaryImportAsync(
                "COPY external_ref (node_id, node_type, source, external_id, url, import_batch_id, created_at, updated_at) FROM STDIN (FORMAT BINARY)", cancellationToken))
            {
                var now = DateTime.UtcNow;
                foreach (var r in _newRefs)
                {
                    await import.StartRowAsync(cancellationToken);
                    await import.WriteAsync(r.NodeId, NpgsqlDbType.Text, cancellationToken);
                    await import.WriteAsync(r.NodeType, NpgsqlDbType.Text, cancellationToken);
                    await import.WriteAsync(SnakeCaseEnumConverter<DataSource>.ToStore(source), NpgsqlDbType.Text, cancellationToken);
                    await import.WriteAsync(r.ExternalId, NpgsqlDbType.Text, cancellationToken);
                    if (r.Url is null)
                    {
                        await import.WriteNullAsync(cancellationToken);
                    }
                    else
                    {
                        await import.WriteAsync(r.Url, NpgsqlDbType.Text, cancellationToken);
                    }

                    await import.WriteAsync(batch, NpgsqlDbType.Bigint, cancellationToken);
                    await import.WriteAsync(now, NpgsqlDbType.TimestampTz, cancellationToken);
                    await import.WriteAsync(now, NpgsqlDbType.TimestampTz, cancellationToken);
                }

                await import.CompleteAsync(cancellationToken);
            }

            // Each relationship's count is kept with its definition, because counting live is too slow on large models.
            foreach (var group in _links.GroupBy(l => (l.SourceType, l.RelType, l.TargetType)))
            {
                await using var command = new NpgsqlCommand(
                    "UPDATE link_definition SET links = links + @added WHERE source_type = @from AND rel_type = @rel AND target_type = @to", connection, transaction);
                command.Parameters.Add(new NpgsqlParameter("added", NpgsqlDbType.Bigint) { Value = (long)group.Count() });
                command.Parameters.Add(new NpgsqlParameter("from", NpgsqlDbType.Text) { Value = group.Key.SourceType });
                command.Parameters.Add(new NpgsqlParameter("rel", NpgsqlDbType.Text) { Value = group.Key.RelType });
                command.Parameters.Add(new NpgsqlParameter("to", NpgsqlDbType.Text) { Value = group.Key.TargetType });
                if (await command.ExecuteNonQueryAsync(cancellationToken) == 0)
                {
                    throw new LinkbaseValidationException($"The model has no relationship {group.Key.SourceType} {group.Key.RelType} {group.Key.TargetType}. Load models/climbing/model.json first.");
                }
            }
        }

        private static void Add<TKey>(Dictionary<TKey, List<string>> map, TKey key, string id) where TKey : notnull
        {
            if (!map.TryGetValue(key, out var list))
            {
                map[key] = list = [];
            }

            list.Add(id);
        }

        private static JsonNode? Text(string? value) => string.IsNullOrEmpty(value) ? null : JsonValue.Create(value);

        private static JsonNode Choice<T>(T value) where T : struct, Enum => JsonValue.Create(SnakeCaseEnumConverter<T>.ToStore(value))!;
    }
}
