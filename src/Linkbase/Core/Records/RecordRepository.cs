using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Linkbase.Core.Models;
using Linkbase.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace Linkbase.Core.Records;

// Lists, shows and edits the generic store's data, guided by the model's definitions. See docs/models.md.
// Domain modules' hooks run on every change made through it, except ReplaceDerivedAsync, which is the modules' own.
public sealed partial class RecordRepository(IDbContextFactory<LinkbaseDbContext> contextFactory, RecordHooks hooks, Linkbase.Core.Security.FieldPolicy fieldPolicy)
{
    public const int LinksShown = 25;

    // The properties this viewer may not see. Nothing is hidden without a policy, or with row security off.
    private async ValueTask<Linkbase.Core.Security.HiddenFields> HiddenAsync(LinkbaseDbContext db, CancellationToken cancellationToken) =>
        await fieldPolicy.HiddenAsync(db, cancellationToken);

    // A type's definition without the fields the viewer can't see, so a save neither reads, requires nor overwrites them.
    // With a record id, also without the properties that record's own labels hide from this viewer (the hidden set must have
    // been scoped to the record, HiddenFields.ForRecordsAsync).
    private static RecordType Visible(RecordType type, Linkbase.Core.Security.HiddenFields hidden, string? nodeId = null) =>
        (nodeId is null ? hidden.For(type.Name) : hidden.For(type.Name, nodeId)) is { Count: > 0 } keys ? type with { Fields = type.Fields.Where(f => !keys.Contains(f.Key)).ToList() } : type;
}
