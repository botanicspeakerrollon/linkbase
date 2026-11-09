using Linkbase.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Linkbase.Core.Records;

public sealed record NodeRef(long Id, DataSource Source, string ExternalId, string? Url);

// Ids that a record has in other systems, such as a crag's UKC id, so an import can find the record again.
public sealed class ExternalRefRepository(IDbContextFactory<LinkbaseDbContext> contextFactory)
{
    public async Task<IReadOnlyList<NodeRef>> RefsAsync(string nodeId, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.ExternalRefs.AsNoTracking()
            .Where(r => r.NodeId == nodeId)
            .OrderBy(r => r.Source).ThenBy(r => r.ExternalId)
            .Select(r => new NodeRef(r.Id, r.Source, r.ExternalId, r.Url))
            .ToListAsync(cancellationToken);
    }

    // The record of a type that has this id in a source, if there is one.
    public async Task<string?> FindNodeAsync(DataSource source, string type, string externalId, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.ExternalRefs.AsNoTracking()
            .Where(r => r.Source == source && r.NodeType == type && r.ExternalId == externalId)
            .Select(r => r.NodeId)
            .SingleOrDefaultAsync(cancellationToken);
    }

    // A source's id belongs to one record of a type. Adding it again to the same record changes nothing.
    public async Task AddAsync(string nodeId, DataSource source, string externalId, string? url = null, long? importBatchId = null, CancellationToken cancellationToken = default)
    {
        externalId = string.IsNullOrWhiteSpace(externalId) ? throw new LinkbaseValidationException("An external id can't be empty.") : externalId.Trim();
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var type = await db.Nodes.AsNoTracking()
            .Where(n => n.Id == nodeId && n.UserId == AppUser.DefaultId)
            .Select(n => n.Type)
            .SingleOrDefaultAsync(cancellationToken)
            ?? throw new LinkbaseValidationException("This record no longer exists.");

        var owner = await db.ExternalRefs.AsNoTracking()
            .Where(r => r.Source == source && r.NodeType == type && r.ExternalId == externalId)
            .Select(r => r.NodeId)
            .SingleOrDefaultAsync(cancellationToken);
        if (owner == nodeId)
        {
            return;
        }

        if (owner is not null)
        {
            throw new LinkbaseValidationException($"Another {type} already has the {SourceName(source)} id {externalId}.");
        }

        db.ExternalRefs.Add(new ExternalRef
        {
            NodeId = nodeId,
            NodeType = type,
            Source = source,
            ExternalId = externalId,
            Url = string.IsNullOrWhiteSpace(url) ? null : url.Trim(),
            ImportBatchId = importBatchId,
        });
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            throw new LinkbaseValidationException($"Another {type} already has the {SourceName(source)} id {externalId}.");
        }
    }

    public async Task RemoveAsync(long id, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await db.ExternalRefs.Where(r => r.Id == id && r.NodeId != null).ExecuteDeleteAsync(cancellationToken);
    }

    // external_ref has no foreign key, so whatever deletes nodes deletes their refs in the same transaction.
    internal static Task RemoveForNodesAsync(LinkbaseDbContext db, IQueryable<string> nodeIds, CancellationToken cancellationToken) =>
        db.ExternalRefs.Where(r => r.NodeId != null && nodeIds.Contains(r.NodeId)).ExecuteDeleteAsync(cancellationToken);

    public static string SourceName(DataSource source) => source switch
    {
        DataSource.Ukc => "UKC",
        DataSource.MountainProject => "Mountain Project",
        DataSource.WebImport => "Web import",
        DataSource.Csv => "CSV",
        DataSource.Ai => "AI extraction",
        _ => "Manual",
    };
}
