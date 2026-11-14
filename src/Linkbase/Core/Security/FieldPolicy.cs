using Linkbase.Data;
using Microsoft.EntityFrameworkCore;

namespace Linkbase.Core.Security;

// The property keys of each type that the viewer may not see, even on records they can. Empty when row security is off, which
// costs the callers nothing.
public sealed class HiddenFields
{
    public static HiddenFields None { get; } = new(new Dictionary<string, IReadOnlySet<string>>());

    private static readonly IReadOnlySet<string> NoKeys = new HashSet<string>();
    private readonly IReadOnlyDictionary<string, IReadOnlySet<string>> _byType;

    public HiddenFields(IReadOnlyDictionary<string, IReadOnlySet<string>> byType) => _byType = byType;

    public bool Any => _byType.Count > 0;

    public IReadOnlySet<string> For(string type) => _byType.GetValueOrDefault(type) ?? NoKeys;

    public bool IsHidden(string type, string key) => For(type).Contains(key);

    // Every key hidden on any type, for readers that don't know a node's type.
    public IReadOnlySet<string> AnyType => _byType.Values.SelectMany(k => k).ToHashSet();

    // Each hidden key with its type in front, for SQL to test a row's label and a key together.
    public string[] Pairs(char separator) => _byType.SelectMany(t => t.Value.Select(k => t.Key + separator + k)).ToArray();

    // The values without the hidden keys.
    public IReadOnlyDictionary<string, string> Strip(string type, IReadOnlyDictionary<string, string> values)
    {
        var hidden = For(type);
        return hidden.Count == 0 || !values.Keys.Any(hidden.Contains)
            ? values
            : values.Where(v => !hidden.Contains(v.Key)).ToDictionary(v => v.Key, v => v.Value);
    }
}

// Decides which properties a viewer sees masked. Row security already hides records the viewer may not see, so this checks only
// each field definition's own label, with the same rule as everything else (VisibilityRule). The values stay in `props`, so this
// is application code, not the database: every reader of props has to ask (docs/security-design.md, "Properties").
public sealed class FieldPolicy(RowSecurityState state, IViewerResolver resolver)
{
    // Uses the caller's context, so a read with security off adds no connection and no query.
    public async ValueTask<HiddenFields> HiddenAsync(LinkbaseDbContext db, CancellationToken cancellationToken = default)
    {
        await db.Database.OpenConnectionAsync(cancellationToken);
        var connection = (Npgsql.NpgsqlConnection)db.Database.GetDbConnection();
        if (await state.SecurityAsync(connection, cancellationToken) is not { Enabled: true } security)
        {
            return HiddenFields.None;
        }

        var labelled = await db.FieldDefinitions.AsNoTracking()
            .Where(f => f.LabelLevel > 0 || f.LabelAny.Length != 1 || f.LabelAny[0] != LabelCodec.Everyone || f.LabelAll.Length > 0)
            .Select(f => new { f.NodeTypeName, f.Key, f.LabelLevel, f.LabelAny, f.LabelAll })
            .ToListAsync(cancellationToken);
        if (labelled.Count == 0)
        {
            return HiddenFields.None;
        }

        var viewer = await resolver.ResolveAsync(security, cancellationToken);
        var hidden = labelled
            .Where(f => !VisibilityRule.CanSee(security, viewer, LabelCodec.ToLabel(f.LabelLevel, f.LabelAny, f.LabelAll)))
            .GroupBy(f => f.NodeTypeName)
            .ToDictionary(g => g.Key, g => (IReadOnlySet<string>)g.Select(f => f.Key).ToHashSet());
        return hidden.Count == 0 ? HiddenFields.None : new HiddenFields(hidden);
    }
}
