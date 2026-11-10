using System.Text.Json;
using System.Text.Json.Nodes;
using Linkbase.Core.Models;
using Linkbase.Core.Records;
using Linkbase.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace Linkbase.Core.Staging;


// Stage, review, commit and undo, for any import that works out records and links to add: a CSV file, text read by AI
// or a model's own importer. An import stages what it would add and nothing else changes until someone commits it.
// Staged items are kept in the database, so a review survives a reload. Committing is one transaction, tagging everything
// with the batch, which is how undoing finds it. See docs/models.md.
public sealed partial class ImportStagingService(IDbContextFactory<LinkbaseDbContext> contextFactory, RecordHooks? hooks = null)
{
    private readonly RecordHooks _hooks = hooks ?? RecordHooks.None;

    // Sources whose batches this service commits and undoes. UKC and Mountain Project batches arrive already committed, from
    // climbing's importer. So do web imports from before text was read by the generic extractor, which can only be undone.
    private static readonly DataSource[] GenericSources = [DataSource.Csv, DataSource.Ai, DataSource.Ukc, DataSource.MountainProject, DataSource.WebImport];

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
        if (request.Refs.Count > 0)
        {
            summary["refs"] = JsonSerializer.SerializeToNode(request.Refs, Json);
        }

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
                await import.WriteAsync(link.Props, NpgsqlDbType.Jsonb, cancellationToken);
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
}
