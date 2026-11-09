using Linkbase.Core.Models;
using Linkbase.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace Linkbase.Domains.Climbing.Data;

public sealed record ConversionSummary(IReadOnlyDictionary<string, long> Nodes, IReadOnlyDictionary<string, long> Links, long Refs);

// Copies the climbing typed tables into a model's generic store, for `climbing to-generic`. The model file gives the types,
// fields and relationships, and replaces whatever the target's generic store held. The typed tables are only read, so the
// target can be the same database or another one, such as the local "Climbing (generic)". See
// docs/climbing-to-generic-plan.md.
public sealed class GenericConverter(IDbContextFactory<LinkbaseDbContext> source, IDbContextFactory<LinkbaseDbContext> target)
{
    public async Task<ConversionSummary> ConvertAsync(string modelFile, TextWriter output, CancellationToken cancellationToken = default)
    {
        var graph = await ClimbingGraph.ReadAsync(source, cancellationToken);
        await output.WriteLineAsync($"Read {graph.Nodes.Count:N0} records, {graph.Links.Count:N0} links and {graph.Refs.Count:N0} external ids from the typed tables");

        // Types, fields and relationships first. This empties the generic store, in a transaction of its own.
        await new ModelLoader(target).LoadAsync(modelFile, TextWriter.Null, cancellationToken);

        await using var db = await target.CreateDbContextAsync(cancellationToken);
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using (var import = await connection.BeginBinaryImportAsync(
            "COPY node (id, user_id, type, key, name, props, created_at, updated_at) FROM STDIN (FORMAT BINARY)", cancellationToken))
        {
            foreach (var node in graph.Nodes)
            {
                await import.StartRowAsync(cancellationToken);
                await import.WriteAsync(node.Id, NpgsqlDbType.Text, cancellationToken);
                await import.WriteAsync(AppUser.DefaultId, NpgsqlDbType.Bigint, cancellationToken);
                await import.WriteAsync(node.Type, NpgsqlDbType.Text, cancellationToken);
                await import.WriteAsync(node.Key, NpgsqlDbType.Text, cancellationToken);
                await import.WriteAsync(node.Name, NpgsqlDbType.Text, cancellationToken);
                await import.WriteAsync(node.Props.ToJsonString(), NpgsqlDbType.Jsonb, cancellationToken);
                await import.WriteAsync(node.CreatedAt.ToUniversalTime(), NpgsqlDbType.TimestampTz, cancellationToken);
                await import.WriteAsync(node.UpdatedAt.ToUniversalTime(), NpgsqlDbType.TimestampTz, cancellationToken);
            }

            await import.CompleteAsync(cancellationToken);
        }

        await using (var import = await connection.BeginBinaryImportAsync(
            "COPY node_link (source_id, rel_type, target_id, props) FROM STDIN (FORMAT BINARY)", cancellationToken))
        {
            foreach (var link in graph.Links)
            {
                await import.StartRowAsync(cancellationToken);
                await import.WriteAsync(link.SourceId, NpgsqlDbType.Text, cancellationToken);
                await import.WriteAsync(link.RelType, NpgsqlDbType.Text, cancellationToken);
                await import.WriteAsync(link.TargetId, NpgsqlDbType.Text, cancellationToken);
                await import.WriteAsync(link.Props.ToJsonString(), NpgsqlDbType.Jsonb, cancellationToken);
            }

            await import.CompleteAsync(cancellationToken);
        }

        await using (var import = await connection.BeginBinaryImportAsync(
            "COPY external_ref (node_id, node_type, source, external_id, url, created_at, updated_at) FROM STDIN (FORMAT BINARY)", cancellationToken))
        {
            foreach (var r in graph.Refs)
            {
                await import.StartRowAsync(cancellationToken);
                await import.WriteAsync(r.NodeId, NpgsqlDbType.Text, cancellationToken);
                await import.WriteAsync(r.NodeType, NpgsqlDbType.Text, cancellationToken);
                await import.WriteAsync(SnakeCaseEnumConverter<DataSource>.ToStore(r.Source), NpgsqlDbType.Text, cancellationToken);
                await import.WriteAsync(r.ExternalId, NpgsqlDbType.Text, cancellationToken);
                if (r.Url is null)
                {
                    await import.WriteNullAsync(cancellationToken);
                }
                else
                {
                    await import.WriteAsync(r.Url, NpgsqlDbType.Text, cancellationToken);
                }

                await import.WriteAsync(r.CreatedAt.ToUniversalTime(), NpgsqlDbType.TimestampTz, cancellationToken);
                await import.WriteAsync(r.UpdatedAt.ToUniversalTime(), NpgsqlDbType.TimestampTz, cancellationToken);
            }

            await import.CompleteAsync(cancellationToken);
        }

        // Each relationship's count is kept with its definition, because counting live is too slow on large models.
        var nodeTypes = graph.Nodes.ToDictionary(n => n.Id, n => n.Type);
        var linkCounts = graph.Links
            .GroupBy(l => (Source: nodeTypes[l.SourceId], l.RelType, Target: nodeTypes[l.TargetId]))
            .ToDictionary(g => $"{g.Key.Source} {g.Key.RelType} {g.Key.Target}", g => (Key: g.Key, Count: (long)g.Count()));
        foreach (var (_, (key, count)) in linkCounts)
        {
            await using var command = new NpgsqlCommand(
                "UPDATE link_definition SET links = @count WHERE source_type = @from AND rel_type = @rel AND target_type = @to", connection, transaction);
            command.Parameters.Add(new NpgsqlParameter("count", NpgsqlDbType.Bigint) { Value = count });
            command.Parameters.Add(new NpgsqlParameter("from", NpgsqlDbType.Text) { Value = key.Source });
            command.Parameters.Add(new NpgsqlParameter("rel", NpgsqlDbType.Text) { Value = key.RelType });
            command.Parameters.Add(new NpgsqlParameter("to", NpgsqlDbType.Text) { Value = key.Target });
            if (await command.ExecuteNonQueryAsync(cancellationToken) == 0)
            {
                throw new LinkbaseValidationException($"The model file has no relationship {key.Source} {key.RelType} {key.Target}, which the typed tables have.");
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return new ConversionSummary(
            graph.Nodes.GroupBy(n => n.Type).ToDictionary(g => g.Key, g => (long)g.Count()),
            linkCounts.ToDictionary(kv => kv.Key, kv => kv.Value.Count),
            graph.Refs.Count);
    }
}
