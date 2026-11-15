using Linkbase.Core.Models;
using Linkbase.Data;

namespace Linkbase.Core.Records;

public sealed partial class RecordRepository
{
    // Reading is RecordReader's job. These hand over to it, so the pages have one repository to ask.
    private readonly RecordReader reader = new(contextFactory, fieldPolicy);

    public Task<IReadOnlyList<RecordType>> TypesAsync(CancellationToken cancellationToken = default) =>
        reader.TypesAsync(cancellationToken);

    public Task<RecordType?> TypeAsync(string name, CancellationToken cancellationToken = default) =>
        reader.TypeAsync(name, cancellationToken);

    // In name order, or only those whose name or any value contains the search text.
    public Task<RecordRows> ListAsync(string type, string? search, int page, int pageSize, CancellationToken cancellationToken = default) =>
        reader.ListAsync(type, search, page, pageSize, cancellationToken);

    // Sorted by a field and narrowed by filters on fields, as well as the search. Values are JSON, so a field is a lookup in
    // props; a field filtered often can have an expression index (docs/history/climbing-to-generic-plan.md).
    public Task<RecordRows> ListAsync(string type, string? search, RecordSort? sort, IReadOnlyList<RecordFilter> filters, int page, int pageSize, CancellationToken cancellationToken = default) =>
        reader.ListAsync(type, search, sort, filters, page, pageSize, cancellationToken);

    public Task<RecordDetail?> GetAsync(string type, string key, CancellationToken cancellationToken = default) =>
        reader.GetAsync(type, key, cancellationToken);

    // Every kind of link the record has, each with its first few links in name order and the total.
    public Task<IReadOnlyList<RecordLinkGroup>> LinksAsync(string id, CancellationToken cancellationToken = default) =>
        reader.LinksAsync(id, cancellationToken);

    // The values a link of this kind can carry, in the order the model gives them.
    public Task<IReadOnlyList<RecordField>> LinkAttributesAsync(string sourceType, string relType, string targetType, CancellationToken cancellationToken = default) =>
        reader.LinkAttributesAsync(sourceType, relType, targetType, cancellationToken);

    // The kinds of link a record of this type can have, either way round. Links to a module's derived records are its own,
    // so they're left out.
    public Task<IReadOnlyList<LinkOption>> LinkOptionsAsync(string type, CancellationToken cancellationToken = default) =>
        reader.LinkOptionsAsync(type, cancellationToken);

    public Task<IReadOnlyList<RelationshipDefinition>> RelationshipsAsync(CancellationToken cancellationToken = default) =>
        reader.RelationshipsAsync(cancellationToken);

    // Records of a type whose name contains the text, for choosing the other end of a link.
    public Task<IReadOnlyList<RecordOption>> FindAsync(string type, string? text, int limit, CancellationToken cancellationToken = default) =>
        reader.FindAsync(type, text, limit, cancellationToken);

    internal static Task<List<RecordField>> AttributesAsync(
        LinkbaseDbContext db, string sourceType, string relType, string targetType, CancellationToken cancellationToken) =>
        RecordReader.AttributesAsync(db, sourceType, relType, targetType, cancellationToken);
}
