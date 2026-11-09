using System.Text.Json;
using System.Text.Json.Nodes;
using Linkbase.Core.Models;
using Linkbase.Core.Records;
using Linkbase.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace Linkbase.Core.Staging;

// A field an import adds to a type. Options are a choice field's values.
public sealed record StagedField(string Type, string Key, FieldType FieldType, IReadOnlyList<string> Options);

public sealed record StagedRelationship(string FromType, string RelType, string ToType);

// What an import adds to the model itself, as if the model file had it: types, fields and relationships. Committing adds
// what isn't there yet, and undoing takes away what committing added, once nothing is left of it.
public sealed record ModelChanges(IReadOnlyList<string> Types, IReadOnlyList<StagedField> Fields, IReadOnlyList<StagedRelationship> Relationships)
{
    public static readonly ModelChanges None = new([], [], []);
}

// A record an import would add. Id is the node's id, such as supplier:s1.
public sealed record StagedRecord(string Type, string Key, string Name, string Props)
{
    public string Id => $"{Type.ToLowerInvariant()}:{Key}";
}

// A link an import would add, between two nodes by id.
public sealed record StagedLink(string SourceType, string RelType, string SourceId, string TargetType, string TargetId);

// Who is importing and what, never the file's contents. AlreadyThere and Skipped only count what was left out, so a review
// can say so.
public sealed record StagingRequest(DataSource Source, string File, string What, ModelChanges Model, int AlreadyThere, int Skipped, IReadOnlyList<string> Notes)
{
    public StagingRequest(DataSource source, string file, string what, ModelChanges model)
        : this(source, file, what, model, 0, 0, [])
    {
    }
}

public sealed record StagedBatch(
    long Id, DataSource Source, string File, string What, ImportStatus Status, int Records, int Links, int Included, int AlreadyThere, int Skipped,
    IReadOnlyList<string> Notes, int Added, DateTimeOffset At)
{
    public int Items => Records + Links;
}

public sealed record StagedRow(long Id, StagedKind Kind, string Type, string Title, string Detail, bool Included);

public sealed record StagedPage(int Total, IReadOnlyList<StagedRow> Rows);

public sealed record StagedCommit(long BatchId, int Records, int Links)
{
    public int Added => Records + Links;
}

// Stage, review, commit and undo, for any import that works out records and links to add: a CSV file today, web import
// or a model's own importer later. An import stages what it would add and nothing else changes until someone commits it.
// Staged items are kept in the database, so a review survives a reload. Committing is one transaction, tagging everything
// with the batch, which is how undoing finds it. See docs/models.md.
public sealed class ImportStagingService(IDbContextFactory<LinkbaseDbContext> contextFactory, RecordHooks? hooks = null)
{
    private readonly RecordHooks _hooks = hooks ?? RecordHooks.None;

    // Sources whose batches this service commits and undoes. UKC and Mountain Project batches arrive already committed, from
    // climbing's importer. Others, such as climbing's web import, have their own.
    private static readonly DataSource[] GenericSources = [DataSource.Csv, DataSource.Ukc, DataSource.MountainProject];

    public static bool IsGeneric(DataSource source) => GenericSources.Contains(source);

    public async Task<long> StageAsync(
        StagingRequest request, IEnumerable<StagedRecord> records, IEnumerable<StagedLink> links, CancellationToken cancellationToken = default)
    {
        if (!IsGeneric(request.Source))
        {
            throw new LinkbaseValidationException("That kind of import has its own way of staging.");
        }

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var summary = new JsonObject
        {
            ["file"] = request.File,
            ["what"] = request.What,
            ["staged"] = true,
            ["alreadyThere"] = request.AlreadyThere,
            ["skipped"] = request.Skipped,
            ["notes"] = new JsonArray(request.Notes.Select(n => (JsonNode?)JsonValue.Create(n)).ToArray()),
            ["model"] = JsonSerializer.SerializeToNode(request.Model, Json),
        };
        long batch;
        await using (var create = new NpgsqlCommand(
            "INSERT INTO import_batch (user_id, source, status, raw_payload, created_at, updated_at) VALUES (@user, @source, 'staged', @summary, now(), now()) RETURNING id",
            connection, transaction))
        {
            create.Parameters.Add(new NpgsqlParameter("user", NpgsqlDbType.Bigint) { Value = AppUser.DefaultId });
            create.Parameters.Add(new NpgsqlParameter("source", NpgsqlDbType.Text) { Value = SnakeCaseEnumConverter<DataSource>.ToStore(request.Source) });
            create.Parameters.Add(new NpgsqlParameter("summary", NpgsqlDbType.Jsonb) { Value = summary.ToJsonString() });
            batch = (long)(await create.ExecuteScalarAsync(cancellationToken))!;
        }

        await using (var import = await connection.BeginBinaryImportAsync(
            "COPY staged_item (import_batch_id, kind, type, key, name, props, rel_type, source_id, target_id, target_type, included) FROM STDIN (FORMAT BINARY)", cancellationToken))
        {
            foreach (var record in records)
            {
                await import.StartRowAsync(cancellationToken);
                await import.WriteAsync(batch, NpgsqlDbType.Bigint, cancellationToken);
                await import.WriteAsync("record", NpgsqlDbType.Text, cancellationToken);
                await import.WriteAsync(record.Type, NpgsqlDbType.Text, cancellationToken);
                await import.WriteAsync(record.Key, NpgsqlDbType.Text, cancellationToken);
                await import.WriteAsync(record.Name, NpgsqlDbType.Text, cancellationToken);
                await import.WriteAsync(record.Props, NpgsqlDbType.Jsonb, cancellationToken);
                await import.WriteNullAsync(cancellationToken);
                await import.WriteAsync(record.Id, NpgsqlDbType.Text, cancellationToken);
                await import.WriteNullAsync(cancellationToken);
                await import.WriteNullAsync(cancellationToken);
                await import.WriteAsync(true, NpgsqlDbType.Boolean, cancellationToken);
            }

            foreach (var link in links)
            {
                await import.StartRowAsync(cancellationToken);
                await import.WriteAsync(batch, NpgsqlDbType.Bigint, cancellationToken);
                await import.WriteAsync("link", NpgsqlDbType.Text, cancellationToken);
                await import.WriteAsync(link.SourceType, NpgsqlDbType.Text, cancellationToken);
                await import.WriteNullAsync(cancellationToken);
                await import.WriteNullAsync(cancellationToken);
                await import.WriteNullAsync(cancellationToken);
                await import.WriteAsync(link.RelType, NpgsqlDbType.Text, cancellationToken);
                await import.WriteAsync(link.SourceId, NpgsqlDbType.Text, cancellationToken);
                await import.WriteAsync(link.TargetId, NpgsqlDbType.Text, cancellationToken);
                await import.WriteAsync(link.TargetType, NpgsqlDbType.Text, cancellationToken);
                await import.WriteAsync(true, NpgsqlDbType.Boolean, cancellationToken);
            }

            await import.CompleteAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return batch;
    }

    public async Task<StagedBatch?> GetAsync(long batchId, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var batch = await db.ImportBatches.AsNoTracking()
            .Where(b => b.Id == batchId && b.UserId == AppUser.DefaultId)
            .Select(b => new { b.Id, b.Source, b.RawPayload, b.Status, b.CreatedAt })
            .SingleOrDefaultAsync(cancellationToken);
        if (batch is null || !IsGeneric(batch.Source))
        {
            return null;
        }

        var counts = await db.StagedItems.AsNoTracking()
            .Where(i => i.ImportBatchId == batchId)
            .GroupBy(i => i.Kind)
            .Select(g => new { Kind = g.Key, Total = g.Count(), Included = g.Count(i => i.Included) })
            .ToListAsync(cancellationToken);
        return Summarise(batch.Id, batch.Source, batch.RawPayload, batch.Status, batch.CreatedAt, counts.ToDictionary(c => c.Kind, c => (c.Total, c.Included)));
    }

    public async Task<IReadOnlyList<StagedBatch>> RecentAsync(int count, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var batches = await db.ImportBatches.AsNoTracking()
            .Where(b => b.Source == DataSource.Csv && b.UserId == AppUser.DefaultId)
            .OrderByDescending(b => b.Id)
            .Take(count)
            .Select(b => new { b.Id, b.Source, b.RawPayload, b.Status, b.CreatedAt })
            .ToListAsync(cancellationToken);
        var ids = batches.Where(b => b.Status == ImportStatus.Staged).Select(b => b.Id).ToList();
        var counts = ids.Count == 0
            ? []
            : await db.StagedItems.AsNoTracking()
                .Where(i => ids.Contains(i.ImportBatchId))
                .GroupBy(i => new { i.ImportBatchId, i.Kind })
                .Select(g => new { g.Key.ImportBatchId, g.Key.Kind, Total = g.Count(), Included = g.Count(i => i.Included) })
                .ToListAsync(cancellationToken);
        return batches.Select(b => Summarise(b.Id, b.Source, b.RawPayload, b.Status, b.CreatedAt,
            counts.Where(c => c.ImportBatchId == b.Id).ToDictionary(c => c.Kind, c => (c.Total, c.Included)))).ToList();
    }

    private static StagedBatch Summarise(
        long id, DataSource source, string? payload, ImportStatus status, DateTimeOffset at, Dictionary<StagedKind, (int Total, int Included)> counts)
    {
        var summary = JsonNode.Parse(payload ?? "{}") as JsonObject ?? [];
        var records = counts.GetValueOrDefault(StagedKind.Record);
        var links = counts.GetValueOrDefault(StagedKind.Link);
        var notes = (summary["notes"] as JsonArray)?.Select(n => (string?)n ?? "").ToList() ?? [];
        return new StagedBatch(id, source, (string?)summary["file"] ?? "", (string?)summary["what"] ?? "", status, records.Total, links.Total,
            records.Included + links.Included, (int?)summary["alreadyThere"] ?? 0, (int?)summary["skipped"] ?? 0, notes, (int?)summary["added"] ?? 0, at);
    }

    // A page of a batch's items for review: records in the order they were in the file, then links.
    public async Task<StagedPage> ReviewAsync(long batchId, StagedKind? kind, int page, int pageSize, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var query = db.StagedItems.AsNoTracking().Where(i => i.ImportBatchId == batchId);
        if (kind is { } only)
        {
            query = query.Where(i => i.Kind == only);
        }

        var total = await query.CountAsync(cancellationToken);
        var items = await query.OrderBy(i => i.Kind).ThenBy(i => i.Id).Skip(page * pageSize).Take(pageSize)
            .Select(i => new { i.Id, i.Kind, i.Type, i.Key, i.Name, i.Props, i.RelType, i.SourceId, i.TargetId, i.TargetType, i.Included })
            .ToListAsync(cancellationToken);
        return new StagedPage(total, items.Select(i => i.Kind == StagedKind.Record
            ? new StagedRow(i.Id, i.Kind, i.Type, i.Name ?? i.Key ?? "", Detail(i.Key, i.Props), i.Included)
            : new StagedRow(i.Id, i.Kind, i.Type, $"{i.SourceId} {i.RelType} {i.TargetId}", $"{i.Type} {i.RelType} {i.TargetType}", i.Included)).ToList());
    }

    // The key, then each value, such as "s1 · city: Bern · rating: 4".
    private static string Detail(string? key, string? props)
    {
        var values = JsonNode.Parse(props ?? "{}") as JsonObject ?? [];
        return string.Join(" · ", new[] { key ?? "" }.Concat(values.Select(v => $"{v.Key}: {Shown(v.Value)}")));
    }

    private static string Shown(JsonNode? value) => value switch
    {
        JsonArray list => string.Join(", ", list.Select(Shown)),
        JsonValue text when text.TryGetValue<string>(out var s) => s,
        _ => value?.ToJsonString() ?? "",
    };

    // Items left out are not imported. Only a staged batch can change.
    public async Task SetIncludedAsync(long batchId, IReadOnlyCollection<long> itemIds, bool included, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await EnsureStagedAsync(db, batchId, cancellationToken);
        await db.StagedItems.Where(i => i.ImportBatchId == batchId && itemIds.Contains(i.Id))
            .ExecuteUpdateAsync(s => s.SetProperty(i => i.Included, included), cancellationToken);
    }

    // Every item of a kind, or of both when kind is null.
    public async Task SetAllIncludedAsync(long batchId, StagedKind? kind, bool included, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await EnsureStagedAsync(db, batchId, cancellationToken);
        await db.StagedItems.Where(i => i.ImportBatchId == batchId && (kind == null || i.Kind == kind))
            .ExecuteUpdateAsync(s => s.SetProperty(i => i.Included, included), cancellationToken);
    }

    private static async Task<ImportBatch> EnsureStagedAsync(LinkbaseDbContext db, long batchId, CancellationToken cancellationToken)
    {
        var batch = await db.ImportBatches.AsNoTracking()
            .SingleOrDefaultAsync(b => b.Id == batchId && b.UserId == AppUser.DefaultId && GenericSources.Contains(b.Source), cancellationToken)
            ?? throw new LinkbaseValidationException("There's no such import.");
        return batch.Status == ImportStatus.Staged ? batch : throw new LinkbaseValidationException("This import has already been committed or discarded.");
    }

    // Drops a staged import without adding anything.
    public async Task DiscardAsync(long batchId, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await EnsureStagedAsync(db, batchId, cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.StagedItems.Where(i => i.ImportBatchId == batchId).ExecuteDeleteAsync(cancellationToken);
        await db.ImportBatches.Where(b => b.Id == batchId)
            .ExecuteUpdateAsync(s => s.SetProperty(b => b.Status, ImportStatus.Undone).SetProperty(b => b.UpdatedAt, DateTimeOffset.UtcNow), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    // Adds what's still included, in one transaction. The model's new types, fields and relationships come first. A record
    // whose key is already there, or a link whose ends have gone or that already exists, is skipped, because things may have
    // changed since it was staged. The staged items go once they're in.
    public async Task<StagedCommit> CommitAsync(long batchId, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var batch = await EnsureStagedAsync(db, batchId, cancellationToken);
        var summary = JsonNode.Parse(batch.RawPayload ?? "{}") as JsonObject ?? [];
        var model = summary["model"] is { } node ? node.Deserialize<ModelChanges>(Json) ?? ModelChanges.None : ModelChanges.None;

        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var created = new JsonObject();
        var newTypes = new JsonArray();
        foreach (var type in model.Types)
        {
            if (await ExecuteAsync(connection, transaction,
                "INSERT INTO node_type (name, sort_order) SELECT @type, coalesce(max(sort_order), 0) + 1 FROM node_type ON CONFLICT (name) DO NOTHING",
                ("type", type, NpgsqlDbType.Text)) > 0)
            {
                newTypes.Add(type);
            }
        }

        foreach (var field in model.Fields)
        {
            await ExecuteAsync(connection, transaction,
                "INSERT INTO field_definition (node_type_name, key, type, options) VALUES (@type, @key, @fieldType, @options) ON CONFLICT (node_type_name, key) DO NOTHING",
                ("type", field.Type, NpgsqlDbType.Text),
                ("key", field.Key, NpgsqlDbType.Text),
                ("fieldType", SnakeCaseEnumConverter<FieldType>.ToStore(field.FieldType), NpgsqlDbType.Text),
                ("options", field.Options.Count > 0 ? field.Options.ToArray() : (object)DBNull.Value, NpgsqlDbType.Array | NpgsqlDbType.Text));
        }

        var newRelationships = new JsonArray();
        foreach (var relationship in model.Relationships)
        {
            if (await ExecuteAsync(connection, transaction,
                "INSERT INTO link_definition (source_type, rel_type, target_type) VALUES (@from, @rel, @to) ON CONFLICT DO NOTHING",
                ("from", relationship.FromType, NpgsqlDbType.Text),
                ("rel", relationship.RelType, NpgsqlDbType.Text),
                ("to", relationship.ToType, NpgsqlDbType.Text)) > 0)
            {
                newRelationships.Add(new JsonObject { ["from"] = relationship.FromType, ["rel"] = relationship.RelType, ["to"] = relationship.ToType });
            }
        }

        var records = await ExecuteAsync(connection, transaction,
            """
            INSERT INTO node (id, user_id, type, key, name, props, import_batch_id)
            SELECT source_id, @user, type, key, name, props, @batch FROM staged_item
            WHERE import_batch_id = @batch AND kind = 'record' AND included
            ORDER BY id
            ON CONFLICT DO NOTHING
            """,
            ("user", AppUser.DefaultId, NpgsqlDbType.Bigint),
            ("batch", batchId, NpgsqlDbType.Bigint));

        // One pass: add the links that can be added, and count them by relationship from what was really inserted.
        var counts = new List<(string Source, string Rel, string Target, int Count)>();
        await using (var command = new NpgsqlCommand(
            """
            WITH eligible AS (
                SELECT s.type, s.rel_type, s.source_id, s.target_id, s.target_type FROM staged_item s
                WHERE s.import_batch_id = @batch AND s.kind = 'link' AND s.included
                  AND EXISTS (SELECT 1 FROM node n WHERE n.id = s.source_id)
                  AND EXISTS (SELECT 1 FROM node n WHERE n.id = s.target_id)
                  AND NOT EXISTS (SELECT 1 FROM node_link l WHERE l.source_id = s.source_id AND l.rel_type = s.rel_type AND l.target_id = s.target_id)
            ),
            added AS (
                INSERT INTO node_link (source_id, rel_type, target_id, import_batch_id)
                SELECT DISTINCT source_id, rel_type, target_id, @batch FROM eligible
                RETURNING source_id, rel_type, target_id
            )
            SELECT e.type, e.rel_type, e.target_type, count(*)::int
            FROM added a JOIN eligible e ON e.source_id = a.source_id AND e.rel_type = a.rel_type AND e.target_id = a.target_id
            GROUP BY e.type, e.rel_type, e.target_type
            """, connection, transaction) { CommandTimeout = 0 })
        {
            command.Parameters.Add(new NpgsqlParameter("batch", NpgsqlDbType.Bigint) { Value = batchId });
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                counts.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3)));
            }
        }

        foreach (var (source, rel, target, count) in counts)
        {
            await ExecuteAsync(connection, transaction,
                "UPDATE link_definition SET links = links + @added WHERE source_type = @from AND rel_type = @rel AND target_type = @to",
                ("added", (long)count, NpgsqlDbType.Bigint),
                ("from", source, NpgsqlDbType.Text),
                ("rel", rel, NpgsqlDbType.Text),
                ("to", target, NpgsqlDbType.Text));
        }

        var links = counts.Sum(c => c.Count);
        await ExecuteAsync(connection, transaction, "DELETE FROM staged_item WHERE import_batch_id = @batch", ("batch", batchId, NpgsqlDbType.Bigint));

        created["types"] = newTypes;
        created["relationships"] = newRelationships;
        await ExecuteAsync(connection, transaction,
            "UPDATE import_batch SET status = 'committed', raw_payload = raw_payload || jsonb_build_object('added', @added, 'created', @created::jsonb), updated_at = now() WHERE id = @batch",
            ("added", records + links, NpgsqlDbType.Integer),
            ("created", created.ToJsonString(), NpgsqlDbType.Text),
            ("batch", batchId, NpgsqlDbType.Bigint));
        await transaction.CommitAsync(cancellationToken);
        await _hooks.AfterChangeAsync(await ChangesAsync(db, batchId, removing: false, cancellationToken), cancellationToken);
        return new StagedCommit(batchId, records, links);
    }

    // Removes everything the import added: its links, then its records, including changes made to them since. Types,
    // fields and relationships it added stay in the model, unless nothing is left of them.
    public async Task UndoAsync(long batchId, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var batch = await db.ImportBatches.SingleOrDefaultAsync(b => b.Id == batchId && b.UserId == AppUser.DefaultId && GenericSources.Contains(b.Source), cancellationToken)
            ?? throw new LinkbaseValidationException("There's no such import.");
        if (batch.Status != ImportStatus.Committed)
        {
            throw new LinkbaseValidationException("This import has already been undone.");
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var changes = await ChangesAsync(db, batchId, removing: true, cancellationToken);
        var links = await db.NodeLinks.Where(l => l.ImportBatchId == batchId)
            .GroupBy(l => new { SourceType = l.Source.Type, l.RelType, TargetType = l.Target.Type })
            .Select(g => new { g.Key.SourceType, g.Key.RelType, g.Key.TargetType, Count = g.Count() })
            .ToListAsync(cancellationToken);
        // A record keeps at least one of each link its type requires, so undoing can't take away its last one. Records this
        // import added go with it, so they don't count.
        foreach (var group in links)
        {
            var required = await db.LinkDefinitions.AnyAsync(
                d => d.SourceType == group.SourceType && d.RelType == group.RelType && d.TargetType == group.TargetType && d.Required, cancellationToken);
            var stranded = !required ? 0 : await db.NodeLinks
                .Where(l => l.ImportBatchId == batchId && l.RelType == group.RelType && l.Source.Type == group.SourceType && l.Target.Type == group.TargetType
                    && l.Source.ImportBatchId != batchId)
                .Select(l => l.SourceId)
                .Distinct()
                .CountAsync(source => !db.NodeLinks.Any(o => o.SourceId == source && o.RelType == group.RelType && o.Target.Type == group.TargetType && o.ImportBatchId != batchId), cancellationToken);
            if (stranded > 0)
            {
                throw new LinkbaseValidationException(
                    $"Undoing would leave {stranded:N0} {group.SourceType} record(s) with no {group.SourceType} {group.RelType} {group.TargetType} link, which each needs. Link them another way first.");
            }
        }

        foreach (var group in links)
        {
            await db.LinkDefinitions
                .Where(d => d.SourceType == group.SourceType && d.RelType == group.RelType && d.TargetType == group.TargetType)
                .ExecuteUpdateAsync(s => s.SetProperty(d => d.Links, d => d.Links - group.Count), cancellationToken);
        }

        await db.NodeLinks.Where(l => l.ImportBatchId == batchId).ExecuteDeleteAsync(cancellationToken);

        // The import's own records first, then their links one side at a time, so both lookups use an index. Joining every
        // link to its ends took minutes on the Swiss sample's 45M links.
        var added = db.Nodes.Where(n => n.ImportBatchId == batchId).Select(n => n.Id);

        // Links from a module's derived records are the module's, so they go with the records, and its hooks hear of it.
        if (await db.NodeTypes.AnyAsync(t => t.Owner != null, cancellationToken))
        {
            var derived = db.NodeLinks.Where(l =>
                (added.Contains(l.SourceId) && l.Target.NodeType.Owner != null) || (added.Contains(l.TargetId) && l.Source.NodeType.Owner != null));
            foreach (var group in await derived.GroupBy(l => new { SourceType = l.Source.Type, l.RelType, TargetType = l.Target.Type })
                         .Select(g => new { g.Key.SourceType, g.Key.RelType, g.Key.TargetType, Count = g.Count() })
                         .ToListAsync(cancellationToken))
            {
                await db.LinkDefinitions
                    .Where(d => d.SourceType == group.SourceType && d.RelType == group.RelType && d.TargetType == group.TargetType)
                    .ExecuteUpdateAsync(s => s.SetProperty(d => d.Links, d => d.Links - group.Count), cancellationToken);
            }

            await derived.ExecuteDeleteAsync(cancellationToken);
        }

        var linked = await db.NodeLinks.CountAsync(l => added.Contains(l.SourceId), cancellationToken)
            + await db.NodeLinks.CountAsync(l => added.Contains(l.TargetId), cancellationToken);
        if (linked > 0)
        {
            throw new LinkbaseValidationException($"Records this import added have {linked:N0} link(s) added since. Remove those first.");
        }

        await ExternalRefRepository.RemoveForNodesAsync(db, added, cancellationToken);
        await db.Nodes.Where(n => n.ImportBatchId == batchId).ExecuteDeleteAsync(cancellationToken);
        await RemoveWhatItCreatedAsync(db, batch.RawPayload, cancellationToken);
        batch.Status = ImportStatus.Undone;
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        await _hooks.AfterChangeAsync(changes, cancellationToken);
    }

    // What an import added or is about to remove, for the hooks of the types they watch. Nothing is read when no hook
    // watches anything, as for most models.
    private async Task<List<RecordChange>> ChangesAsync(LinkbaseDbContext db, long batch, bool removing, CancellationToken cancellationToken)
    {
        var watched = _hooks.Watched;
        var changes = new List<RecordChange>();
        if (watched.Count == 0)
        {
            return changes;
        }

        var none = new Dictionary<string, string>();
        var nodes = await db.Nodes.AsNoTracking()
            .Where(n => n.ImportBatchId == batch && watched.Contains(n.Type))
            .Select(n => new { n.Id, n.Type, n.Props })
            .ToListAsync(cancellationToken);
        foreach (var node in nodes)
        {
            var values = RecordRepository.Values(node.Props);
            changes.Add(removing
                ? new RecordChange(RecordChangeKind.Deleted, node.Type, node.Id, values, none)
                : new RecordChange(RecordChangeKind.Created, node.Type, node.Id, none, values));
        }

        var links = await db.NodeLinks.AsNoTracking()
            .Where(l => l.ImportBatchId == batch && (watched.Contains(l.Source.Type) || watched.Contains(l.Target.Type)))
            .Select(l => new { l.SourceId, SourceType = l.Source.Type, l.RelType, l.TargetId, TargetType = l.Target.Type })
            .ToListAsync(cancellationToken);
        var kind = removing ? RecordChangeKind.Unlinked : RecordChangeKind.Linked;
        foreach (var link in links)
        {
            changes.Add(new RecordChange(kind, link.SourceType, link.SourceId, none, none, link.RelType, link.TargetId));
            changes.Add(new RecordChange(kind, link.TargetType, link.TargetId, none, none, link.RelType, link.SourceId));
        }

        return changes;
    }

    // A type the import added goes too, with its fields, once nothing is left of it. So does a relationship it added, once
    // it has no links. Fields it added to a type that was already there stay. Reads the older single type and relationship
    // as well as lists.
    private static async Task RemoveWhatItCreatedAsync(LinkbaseDbContext db, string? summary, CancellationToken cancellationToken)
    {
        var created = (JsonNode.Parse(summary ?? "{}") as JsonObject)?["created"] as JsonObject;
        var types = ((created?["types"] as JsonArray)?.Select(t => (string?)t) ?? []).Append((string?)created?["type"]).OfType<string>().ToList();
        var relationships = ((created?["relationships"] as JsonArray)?.OfType<JsonObject>().Select(r => ((string?)r["from"], (string?)r["rel"], (string?)r["to"])) ?? [])
            .Append(((string?)created?["from"], (string?)created?["rel"], (string?)created?["to"]))
            .Where(r => r.Item1 is not null && r.Item2 is not null && r.Item3 is not null)
            .ToList();

        // Relationships first, so a type's last relationship doesn't keep it.
        foreach (var (from, rel, to) in relationships)
        {
            await db.LinkDefinitions.Where(d => d.SourceType == from && d.RelType == rel && d.TargetType == to && d.Links == 0).ExecuteDeleteAsync(cancellationToken);
        }

        foreach (var type in types)
        {
            if (!await db.Nodes.AnyAsync(n => n.Type == type, cancellationToken)
                && !await db.LinkDefinitions.AnyAsync(d => (d.SourceType == type || d.TargetType == type) && d.Links > 0, cancellationToken))
            {
                await db.LinkDefinitions.Where(d => d.SourceType == type || d.TargetType == type).ExecuteDeleteAsync(cancellationToken);
                await db.FieldDefinitions.Where(f => f.NodeTypeName == type).ExecuteDeleteAsync(cancellationToken);
                await db.NodeTypes.Where(t => t.Name == type).ExecuteDeleteAsync(cancellationToken);
            }
        }
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    private static async Task<int> ExecuteAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, params (string Name, object Value, NpgsqlDbType Type)[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction) { CommandTimeout = 0 };
        foreach (var (name, value, type) in parameters)
        {
            command.Parameters.Add(new NpgsqlParameter(name, type) { Value = value });
        }

        return await command.ExecuteNonQueryAsync();
    }
}
