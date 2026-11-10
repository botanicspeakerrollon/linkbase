using System.Text.Json;
using System.Text.Json.Nodes;
using Linkbase.Core.Models;
using Linkbase.Core.Records;
using Linkbase.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace Linkbase.Core.Staging;

public sealed partial class ImportStagingService
{
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
                  AND NOT EXISTS (SELECT 1 FROM node_link l WHERE l.source_id = s.source_id AND l.rel_type = s.rel_type AND l.target_id = s.target_id AND l.props = '{}'::jsonb)
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

        // Where the records came from. A source's id belongs to one record, so one that's there already is left.
        var refs = summary["refs"] is { } refsNode ? refsNode.Deserialize<List<StagedRef>>(Json) ?? [] : [];
        foreach (var reference in refs)
        {
            await ExecuteAsync(connection, transaction,
                """
                INSERT INTO external_ref (node_id, node_type, source, external_id, url, import_batch_id)
                SELECT @node, @type, @source, @external, @url, @batch WHERE EXISTS (SELECT 1 FROM node WHERE id = @node)
                ON CONFLICT DO NOTHING
                """,
                ("node", reference.NodeId, NpgsqlDbType.Text),
                ("type", reference.Type, NpgsqlDbType.Text),
                ("source", SnakeCaseEnumConverter<DataSource>.ToStore(batch.Source), NpgsqlDbType.Text),
                ("external", reference.ExternalId, NpgsqlDbType.Text),
                ("url", reference.Url, NpgsqlDbType.Text),
                ("batch", batchId, NpgsqlDbType.Bigint));
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
        await _hooks.AfterChangeAsync(await ChangesAsync(db, batchId, removing: false, [], cancellationToken), cancellationToken);
        return new StagedCommit(batchId, records, links);
    }
}
