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
public sealed partial class RecordRepository(IDbContextFactory<LinkbaseDbContext> contextFactory, RecordHooks? hooks = null, Linkbase.Core.Security.FieldPolicy? fieldPolicy = null)
{
    public const int LinksShown = 25;

    private readonly RecordHooks _hooks = hooks ?? RecordHooks.None;

    // The properties this viewer may not see. Nothing is hidden without a policy, or with row security off.
    private async ValueTask<Linkbase.Core.Security.HiddenFields> HiddenAsync(LinkbaseDbContext db, CancellationToken cancellationToken) =>
        fieldPolicy is null ? Linkbase.Core.Security.HiddenFields.None : await fieldPolicy.HiddenAsync(db, cancellationToken);

    // A type's definition without the fields the viewer can't see, so a save neither reads, requires nor overwrites them.
    private static RecordType Visible(RecordType type, Linkbase.Core.Security.HiddenFields hidden) =>
        hidden.For(type.Name) is { Count: > 0 } keys ? type with { Fields = type.Fields.Where(f => !keys.Contains(f.Key)).ToList() } : type;
}
