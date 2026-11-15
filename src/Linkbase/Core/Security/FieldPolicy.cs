using Linkbase.Data;
using Microsoft.EntityFrameworkCore;

namespace Linkbase.Core.Security;

// The property keys of each type that the viewer may not see, even on records they can. Empty when row security is off, which
// costs the callers nothing.
public sealed class HiddenFields
{
    public static HiddenFields None { get; } = new(new Dictionary<string, IReadOnlySet<string>>());

    // Row security is on but this viewer has no hidden fields. Readers that must also filter by what the viewer can see
    // (history) check Secured; None means security is off and they change nothing.
    public static HiddenFields Open { get; } = new(new Dictionary<string, IReadOnlySet<string>>(), secured: true);

    private static readonly IReadOnlySet<string> NoKeys = new HashSet<string>();
    private readonly IReadOnlyDictionary<string, IReadOnlySet<string>> _byType;

    // Per-record overrides (node_property_label), which only ever hide. `_overrides` holds, for the records read so far, the keys
    // whose override this viewer fails (see ForRecordsAsync); `_judge` is how to load more, and is null when no override exists.
    private Dictionary<string, IReadOnlySet<string>> _overrides = new Dictionary<string, IReadOnlySet<string>>();
    private (ModelSecurity Security, Viewer Viewer)? _judge;

    public HiddenFields(IReadOnlyDictionary<string, IReadOnlySet<string>> byType, bool secured = true)
    {
        _byType = byType;
        Secured = secured;
    }

    private HiddenFields(IReadOnlyDictionary<string, IReadOnlySet<string>> byType) : this(byType, secured: false)
    {
    }

    public bool Secured { get; }

    // Some record has a label of its own on a property, so readers must ask about the records they return (ForRecordsAsync).
    // False for nearly every model, which then pays nothing.
    public bool HasOverrides => _judge is not null;

    public bool Any => _byType.Count > 0;

    public IReadOnlySet<string> For(string type) => _byType.GetValueOrDefault(type) ?? NoKeys;

    // The keys hidden on one record: its type's, and any it has an override on that this viewer fails. Only knows the overrides
    // loaded by ForRecordsAsync.
    public IReadOnlySet<string> For(string type, string nodeId)
    {
        var typeKeys = For(type);
        if (!_overrides.TryGetValue(nodeId, out var own))
        {
            return typeKeys;
        }

        return typeKeys.Count == 0 ? own : typeKeys.Union(own).ToHashSet();
    }

    public bool IsHidden(string type, string nodeId, string key) => IsHidden(type, key) || (_overrides.TryGetValue(nodeId, out var own) && own.Contains(key));

    // A copy that also knows which of these records' properties the viewer can't see through an override. One primary-key lookup,
    // and none at all when no override exists. Run it on the records a read returns; a record row security hides has no rows here.
    public async Task<HiddenFields> ForRecordsAsync(LinkbaseDbContext db, IEnumerable<string> nodeIds, CancellationToken cancellationToken = default)
    {
        if (_judge is not { } judge)
        {
            return this;
        }

        var ids = nodeIds.Distinct().ToArray();
        if (ids.Length == 0)
        {
            return this;
        }

        var rows = await db.NodePropertyLabels.AsNoTracking()
            .Where(l => ids.Contains(l.NodeId))
            .Select(l => new { l.NodeId, l.Key, l.LabelLevel, l.LabelAny, l.LabelAll })
            .ToListAsync(cancellationToken);
        var failed = rows
            .Where(l => !VisibilityRule.CanSee(judge.Security, judge.Viewer, LabelCodec.ToLabel(l.LabelLevel, l.LabelAny, l.LabelAll)))
            .GroupBy(l => l.NodeId)
            .ToDictionary(g => g.Key, g => (IReadOnlySet<string>)g.Select(l => l.Key).ToHashSet());
        return new HiddenFields(_byType, Secured) { _overrides = failed, _judge = judge };
    }

    internal HiddenFields WithJudge(ModelSecurity security, Viewer viewer)
    {
        _judge = (security, viewer);
        return this;
    }

    public bool IsHidden(string type, string key) => For(type).Contains(key);

    // Every key hidden on any type, for readers that don't know a node's type.
    public IReadOnlySet<string> AnyType => _byType.Values.SelectMany(k => k).ToHashSet();

    // Each hidden key with its type in front, for SQL to test a row's label and a key together.
    public string[] Pairs(char separator) => _byType.SelectMany(t => t.Value.Select(k => t.Key + separator + k)).ToArray();

    // The values without the hidden keys.
    public IReadOnlyDictionary<string, string> Strip(string type, IReadOnlyDictionary<string, string> values, string? nodeId = null)
    {
        var hidden = nodeId is null ? For(type) : For(type, nodeId);
        return hidden.Count == 0 || !values.Keys.Any(hidden.Contains)
            ? values
            : values.Where(v => !hidden.Contains(v.Key)).ToDictionary(v => v.Key, v => v.Value);
    }
}

// Decides which properties a viewer sees masked. Row security already hides records the viewer may not see, so this checks only
// each field definition's own label, with the same rule as everything else (VisibilityRule). The values stay in `props`, so this
// is application code, not the database: every reader of props has to ask (docs/history/security-design.md, "Properties").
public sealed class FieldPolicy(RowSecurityState? state, IViewerResolver? resolver)
{
    // For code that deliberately reads or writes everything: the module's own writes, imports that show nothing to a viewer,
    // and tests. A repository that is meant to hide fields is given the real policy, and can't be built without one.
    public static readonly FieldPolicy Unrestricted = new(null, null);

    // Uses the caller's context, so a read with security off adds no connection and no query.
    public async ValueTask<HiddenFields> HiddenAsync(LinkbaseDbContext db, CancellationToken cancellationToken = default)
    {
        if (state is null || resolver is null)
        {
            return HiddenFields.None;
        }

        await db.Database.OpenConnectionAsync(cancellationToken);
        var connection = (Npgsql.NpgsqlConnection)db.Database.GetDbConnection();
        if (await state.SecurityAsync(connection, cancellationToken) is not { Enabled: true } security)
        {
            return HiddenFields.None;
        }

        var overrides = await db.NodePropertyLabels.AsNoTracking().AnyAsync(cancellationToken);
        var labelled = await db.FieldDefinitions.AsNoTracking()
            .Where(f => f.LabelLevel > 0 || f.LabelAny.Length != 1 || f.LabelAny[0] != LabelCodec.Everyone || f.LabelAll.Length > 0)
            .Select(f => new { f.NodeTypeName, f.Key, f.LabelLevel, f.LabelAny, f.LabelAll })
            .ToListAsync(cancellationToken);
        if (labelled.Count == 0 && !overrides)
        {
            return HiddenFields.Open;
        }

        var viewer = await resolver.ResolveAsync(security, cancellationToken);
        var hidden = labelled
            .Where(f => !VisibilityRule.CanSee(security, viewer, LabelCodec.ToLabel(f.LabelLevel, f.LabelAny, f.LabelAll)))
            .GroupBy(f => f.NodeTypeName)
            .ToDictionary(g => g.Key, g => (IReadOnlySet<string>)g.Select(f => f.Key).ToHashSet());
        // A new object whenever overrides exist, because the shared Open must stay without a judge.
        if (overrides)
        {
            return new HiddenFields(hidden).WithJudge(security, viewer);
        }

        return hidden.Count == 0 ? HiddenFields.Open : new HiddenFields(hidden);
    }
}
