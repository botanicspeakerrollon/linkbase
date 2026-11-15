using Linkbase.Core.History;
using Linkbase.Core.Models;

namespace Linkbase.Core.Records;

// What pages and services use of the record repository, so they can be tested against a stand-in and so the implementation
// can be split without them noticing. RecordRepository is the one implementation.
public interface IRecordRepository
{
    Task<IReadOnlyList<RecordType>> TypesAsync(CancellationToken cancellationToken = default);

    Task<RecordType?> TypeAsync(string name, CancellationToken cancellationToken = default);

    // In name order, or only those whose name or any value contains the search text.
    Task<RecordRows> ListAsync(string type, string? search, int page, int pageSize, CancellationToken cancellationToken = default);

    // Sorted by a field and narrowed by filters on fields, as well as the search. Values are JSON, so a field is a lookup in
    // props; a field filtered often can have an expression index (docs/history/climbing-to-generic-plan.md).
    Task<RecordRows> ListAsync(
        string type, string? search, RecordSort? sort, IReadOnlyList<RecordFilter> filters, int page, int pageSize, CancellationToken cancellationToken = default);

    Task<RecordDetail?> GetAsync(string type, string key, CancellationToken cancellationToken = default);

    // Every kind of link the record has, each with its first few links in name order and the total.
    Task<IReadOnlyList<RecordLinkGroup>> LinksAsync(string id, CancellationToken cancellationToken = default);

    // The values a link of this kind can carry, in the order the model gives them.
    Task<IReadOnlyList<RecordField>> LinkAttributesAsync(string sourceType, string relType, string targetType, CancellationToken cancellationToken = default);

    // The kinds of link a record of this type can have, either way round. Links to a module's derived records are its own,
    // so they're left out.
    Task<IReadOnlyList<LinkOption>> LinkOptionsAsync(string type, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<RelationshipDefinition>> RelationshipsAsync(CancellationToken cancellationToken = default);

    // Records of a type whose name contains the text, for choosing the other end of a link.
    Task<IReadOnlyList<RecordOption>> FindAsync(string type, string? text, int limit, CancellationToken cancellationToken = default);

    // The key is made from the name when none is given, and can't change afterwards, because the record's id is made from it.
    // The record, the records it creates and all their links are saved together, so a record never exists without the links
    // its type requires.
    // The reason is kept in the audit trail, and is needed unless the model says otherwise.
    Task<string> CreateAsync(string type, string? key, RecordInput input, ChangeReason? reason = null, CancellationToken cancellationToken = default);

    Task UpdateAsync(string id, RecordInput input, ChangeReason? reason = null, CancellationToken cancellationToken = default);

    // Like everything else, a record that still has links can't be deleted. Remove its links first. The links its type
    // requires can't be removed on their own, so they go with it. So do links to a module's derived records, which are the
    // module's, and its hooks hear of the delete to derive them again.
    Task DeleteAsync(string id, ChangeReason? reason = null, CancellationToken cancellationToken = default);

    // Links only go where the model has a relationship, either way round, and never to a module's derived records. Values are
    // the link's attributes as a form sends them. The same two records can be linked the same way again only when an
    // attribute's value differs, so a relationship without attributes allows one link between two records.
    Task AddLinkAsync(
        string sourceId, string relType, string targetId, IReadOnlyDictionary<string, string?>? values = null, ChangeReason? reason = null,
        CancellationToken cancellationToken = default);

    // Changes a link's attribute values in place. A required attribute can't be emptied, and the link can't become a repeat
    // of another between the same two records.
    Task UpdateLinkAsync(long linkId, IReadOnlyDictionary<string, string?> values, ChangeReason? reason = null, CancellationToken cancellationToken = default);

    Task RemoveLinkAsync(long linkId, ChangeReason? reason = null, CancellationToken cancellationToken = default);

    // Folds one record into another of the same type: the one kept keeps its name and its own values, gains any value it
    // lacked (and any item of a list it lacked), and takes over every link and external id of the one dropped, which is then
    // deleted. A link the kept record already has, or that would join it to itself, is dropped rather than doubled. All of it
    // is saved together, or none of it, and the audit trail says what went where.
    Task<MergeResult> MergeAsync(string keepId, string dropId, ChangeReason? reason = null, CancellationToken cancellationToken = default);

    // Replaces every record of a type a domain module derives, and every link to them, in one transaction. Only the
    // module that owns the type can, and no hooks run. Each link needs one end among the new records, and a relationship
    // the model has. Returns how many records there are now.
    Task<int> ReplaceDerivedAsync(
        string domain, string type, IReadOnlyList<DerivedRecord> records, IReadOnlyList<DerivedLink> links,
        long? causeChangeSetId = null, CancellationToken cancellationToken = default);

    // Whether this model keeps an audit trail and asks for a reason with each change, for the forms to show.
    Task<HistorySettings> HistorySettingsAsync(CancellationToken cancellationToken = default);

}
