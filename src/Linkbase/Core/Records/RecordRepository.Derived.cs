using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Linkbase.Core.History;
using Linkbase.Core.Models;
using Linkbase.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace Linkbase.Core.Records;

public sealed partial class RecordRepository
{
    private readonly DerivedRecordWriter derived = new(contextFactory, new RecordReader(contextFactory, fieldPolicy));

    // Replaces every record of a type a domain module derives, and every link to them, in one transaction. Only the
    // module that owns the type can, and no hooks run. Each link needs one end among the new records, and a relationship
    // the model has. Returns how many records there are now.
    public Task<int> ReplaceDerivedAsync(
        string domain, string type, IReadOnlyList<DerivedRecord> records, IReadOnlyList<DerivedLink> links,
        long? causeChangeSetId = null, CancellationToken cancellationToken = default) =>
        derived.ReplaceDerivedAsync(domain, type, records, links, causeChangeSetId, cancellationToken);

    internal static readonly IReadOnlyDictionary<string, string> NoValues = new Dictionary<string, string>();

    internal static List<RecordChange> LinkChanges(
        RecordChangeKind kind, string sourceType, string sourceId, string relType, string targetType, string targetId,
        IReadOnlyDictionary<string, string>? before = null, IReadOnlyDictionary<string, string>? after = null) =>
    [
        new(kind, sourceType, sourceId, before ?? NoValues, after ?? NoValues, relType, targetId),
        new(kind, targetType, targetId, before ?? NoValues, after ?? NoValues, relType, sourceId),
    ];

    internal static void RefuseDerived(RecordType type)
    {
        if (type.Owner is not null)
        {
            throw Derived(type.Name, type.Owner);
        }
    }

    private static LinkbaseValidationException Derived(string type, string owner) =>
        new($"{type} records are derived by {owner} from other records, so they can't be changed here.");
}
