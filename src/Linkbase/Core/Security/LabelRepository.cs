using Linkbase.Core.History;
using Linkbase.Data;
using Microsoft.EntityFrameworkCore;

namespace Linkbase.Core.Security;

// What a node's or link's label says, in the model's own names: a level's value, and the sets' values.
public sealed record LabelValue(string? Level, IReadOnlyList<string> AnyOf, IReadOnlyList<string> AllOf);

public enum SetEditMode { Replace, Add, Remove }

// A change to one of the sets: put these values there, add them to what is there, or take them away.
public sealed record SetEdit(SetEditMode Mode, IReadOnlyList<string> Values);

// A change to labels. Anything left out is left as it is. Replacing a set with nothing removes its restriction.
public sealed record LabelEdit(string? Level = null, SetEdit? AnyOf = null, SetEdit? AllOf = null);

// Reads and changes labels. Every change is validated against the model's own parts and is written to the audit trail in the
// same transaction, a change set for the whole batch. Visibility itself (who may read or change a label) is the database's job
// once row security is on. See docs/history/security-design.md.
public sealed class LabelRepository(IDbContextFactory<LinkbaseDbContext> contextFactory)
{
    private const int Chunk = 1000;

    // The model's security section, or null when the model doesn't use security.
    public async Task<ModelSecurity?> SettingsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var json = await db.ModelSettings.AsNoTracking().Select(s => s.Security).SingleOrDefaultAsync(cancellationToken);
        return LabelCodec.FromJson(json);
    }

    public async Task<LabelValue?> GetNodeAsync(string nodeId, CancellationToken cancellationToken = default)
    {
        var security = await SettingsAsync(cancellationToken);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var row = await db.Nodes.AsNoTracking().Where(n => n.Id == nodeId)
            .Select(n => new { n.LabelLevel, n.LabelAny, n.LabelAll }).SingleOrDefaultAsync(cancellationToken);
        return row is null ? null : Describe(security, row.LabelLevel, row.LabelAny, row.LabelAll);
    }

    public async Task<LabelValue?> GetLinkAsync(long linkId, CancellationToken cancellationToken = default)
    {
        var security = await SettingsAsync(cancellationToken);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var row = await db.NodeLinks.AsNoTracking().Where(l => l.Id == linkId)
            .Select(l => new { l.LabelLevel, l.LabelAny, l.LabelAll }).SingleOrDefaultAsync(cancellationToken);
        return row is null ? null : Describe(security, row.LabelLevel, row.LabelAny, row.LabelAll);
    }

    // Applies the edit to each node. Returns how many changed; a node the edit leaves as it was isn't counted or logged.
    public async Task<int> EditNodesAsync(IReadOnlyCollection<string> nodeIds, LabelEdit edit, ChangeReason? reason, CancellationToken cancellationToken = default)
    {
        try
        {
            return await EditNodesCoreAsync(nodeIds, edit, reason, cancellationToken);
        }
        catch (Exception ex) when (IsRowSecurityRefusal(ex))
        {
            throw new LinkbaseValidationException(RefusedMessage);
        }
    }

    // The database refuses a label the viewer couldn't see, or a record they can't see. Said as a message, not left as a crash.
    private const string RefusedMessage = "You can't set that label: a label may only use values you hold yourself, and you may only change records you can see.";

    private static bool IsRowSecurityRefusal(Exception ex) =>
        (ex as DbUpdateException)?.InnerException is Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.InsufficientPrivilege }
        || ex is Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.InsufficientPrivilege };

    private async Task<int> EditNodesCoreAsync(IReadOnlyCollection<string> nodeIds, LabelEdit edit, ChangeReason? reason, CancellationToken cancellationToken)
    {
        var (security, history, checkedReason) = await PrepareAsync(edit, reason, cancellationToken);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var logged = new List<PendingChange>();
        var changed = 0;
        foreach (var ids in nodeIds.Distinct().Chunk(Chunk))
        {
            var nodes = await db.Nodes.Where(n => ids.Contains(n.Id)).ToListAsync(cancellationToken);
            foreach (var node in nodes)
            {
                var before = Label(node.LabelLevel, node.LabelAny, node.LabelAll);
                var after = Apply(security, before, edit);
                if (Same(before, after))
                {
                    continue;
                }

                node.LabelLevel = (short)after.Level;
                node.LabelAny = LabelCodec.AnyColumn(after);
                node.LabelAll = LabelCodec.AllColumn(after);
                changed++;
                logged.AddRange(Changes(security, node.Id, null, null, before, after));
            }

            await db.SaveChangesAsync(cancellationToken);
            db.ChangeTracker.Clear();
        }

        await ChangeRecorder.WriteAsync(db, history, JustificationKind.Manual, checkedReason, logged, cancellationToken: cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return changed;
    }

    // The labels one record carries on its own properties, by property key. Only the ones this viewer may see (row security).
    public async Task<IReadOnlyDictionary<string, LabelValue>> GetPropertyLabelsAsync(string nodeId, CancellationToken cancellationToken = default)
    {
        var security = await SettingsAsync(cancellationToken);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var rows = await db.NodePropertyLabels.AsNoTracking().Where(l => l.NodeId == nodeId)
            .Select(l => new { l.Key, l.LabelLevel, l.LabelAny, l.LabelAll }).ToListAsync(cancellationToken);
        return rows.ToDictionary(r => r.Key, r => Describe(security, r.LabelLevel, r.LabelAny, r.LabelAll));
    }

    // Sets, changes or clears the label on one property of one record. It can only restrict: the value shows only to a viewer
    // who passes the field's label, the record's and this one. A label that comes out as no restriction removes the row. Returns
    // whether anything changed.
    public async Task<bool> EditPropertyAsync(string nodeId, string key, LabelEdit edit, ChangeReason? reason, CancellationToken cancellationToken = default)
    {
        try
        {
            return await EditPropertyCoreAsync(nodeId, key, edit, reason, cancellationToken);
        }
        catch (Exception ex) when (IsRowSecurityRefusal(ex))
        {
            throw new LinkbaseValidationException(RefusedMessage);
        }
    }

    private async Task<bool> EditPropertyCoreAsync(string nodeId, string key, LabelEdit edit, ChangeReason? reason, CancellationToken cancellationToken)
    {
        var (security, history, checkedReason) = await PrepareAsync(edit, reason, cancellationToken);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(key) || !await db.Nodes.AnyAsync(n => n.Id == nodeId, cancellationToken))
        {
            throw new LinkbaseValidationException("There's no such record to label.");
        }

        var row = await db.NodePropertyLabels.SingleOrDefaultAsync(l => l.NodeId == nodeId && l.Key == key, cancellationToken);
        var before = row is null ? Label(0, [LabelCodec.Everyone], []) : Label(row.LabelLevel, row.LabelAny, row.LabelAll);
        var after = Apply(security, before, edit);
        if (Same(before, after))
        {
            return false;
        }

        var unrestricted = after.Level == 0 && after.AnyOf.Count == 0 && after.AllOf.Count == 0;
        if (unrestricted)
        {
            db.NodePropertyLabels.Remove(row!);
        }
        else if (row is null)
        {
            db.NodePropertyLabels.Add(new NodePropertyLabel
            {
                NodeId = nodeId,
                Key = key,
                LabelLevel = (short)after.Level,
                LabelAny = LabelCodec.AnyColumn(after),
                LabelAll = LabelCodec.AllColumn(after),
            });
        }
        else
        {
            row.LabelLevel = (short)after.Level;
            row.LabelAny = LabelCodec.AnyColumn(after);
            row.LabelAll = LabelCodec.AllColumn(after);
        }

        await db.SaveChangesAsync(cancellationToken);
        var change = new PendingChange(ChangeEntityKind.PropertyLabel, nodeId, ChangeOp.Update, key, null, null,
            Summary(security, before), Summary(security, after));
        await ChangeRecorder.WriteAsync(db, history, JustificationKind.Manual, checkedReason, [change], cancellationToken: cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    // A label as one line of history text, in the model's own names. Nothing for no restriction.
    private static string? Summary(ModelSecurity security, Label label)
    {
        var parts = new List<string>();
        if (security.HasLevel && label.Level > 0)
        {
            parts.Add($"{security.Level!.Name} {LevelName(security, label.Level)}");
        }

        if (security.HasAnyOf && label.AnyOf.Count > 0)
        {
            parts.Add($"{security.AnyOf!.Name} {Text(label.AnyOf)}");
        }

        if (security.HasAllOf && label.AllOf.Count > 0)
        {
            parts.Add($"{security.AllOf!.Name} {Text(label.AllOf)}");
        }

        return parts.Count == 0 ? null : string.Join("; ", parts);
    }

    // The same for links, by their ids.
    public async Task<int> EditLinksAsync(IReadOnlyCollection<long> linkIds, LabelEdit edit, ChangeReason? reason, CancellationToken cancellationToken = default)
    {
        try
        {
            return await EditLinksCoreAsync(linkIds, edit, reason, cancellationToken);
        }
        catch (Exception ex) when (IsRowSecurityRefusal(ex))
        {
            throw new LinkbaseValidationException(RefusedMessage);
        }
    }

    private async Task<int> EditLinksCoreAsync(IReadOnlyCollection<long> linkIds, LabelEdit edit, ChangeReason? reason, CancellationToken cancellationToken)
    {
        var (security, history, checkedReason) = await PrepareAsync(edit, reason, cancellationToken);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var logged = new List<PendingChange>();
        var changed = 0;
        foreach (var ids in linkIds.Distinct().Chunk(Chunk))
        {
            var links = await db.NodeLinks.Where(l => ids.Contains(l.Id)).ToListAsync(cancellationToken);
            foreach (var link in links)
            {
                var before = Label(link.LabelLevel, link.LabelAny, link.LabelAll);
                var after = Apply(security, before, edit);
                if (Same(before, after))
                {
                    continue;
                }

                link.LabelLevel = (short)after.Level;
                link.LabelAny = LabelCodec.AnyColumn(after);
                link.LabelAll = LabelCodec.AllColumn(after);
                changed++;
                logged.AddRange(Changes(security, link.SourceId, link.RelType, link.TargetId, before, after));
            }

            await db.SaveChangesAsync(cancellationToken);
            db.ChangeTracker.Clear();
        }

        await ChangeRecorder.WriteAsync(db, history, JustificationKind.Manual, checkedReason, logged, cancellationToken: cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return changed;
    }

    private async Task<(ModelSecurity Security, HistorySettings History, ChangeReason? Reason)> PrepareAsync(LabelEdit edit, ChangeReason? reason, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var security = LabelCodec.FromJson(await db.ModelSettings.AsNoTracking().Select(s => s.Security).SingleOrDefaultAsync(cancellationToken))
            ?? throw new LinkbaseValidationException("This model doesn't use security, so there are no labels to change.");
        var history = await ChangeRecorder.SettingsAsync(db, cancellationToken);
        var problems = new List<string>();
        Validate(security, edit, problems);
        if (problems.Count > 0)
        {
            throw new LinkbaseValidationException(string.Join(Environment.NewLine, problems));
        }

        return (security, history, ChangeRecorder.Check(history, reason));
    }

    private static void Validate(ModelSecurity security, LabelEdit edit, List<string> problems)
    {
        if (edit.Level is { } level)
        {
            if (!security.HasLevel)
            {
                problems.Add("This model has no level part.");
            }
            else if (security.LevelRank(level) < 0)
            {
                problems.Add($"'{level}' isn't one of {string.Join(", ", security.Level!.Values ?? [])}.");
            }
        }

        ValidateSet(security.AnyOf, "anyOf", edit.AnyOf, problems);
        ValidateSet(security.AllOf, "allOf", edit.AllOf, problems);
    }

    private static void ValidateSet(SecurityPart? part, string key, SetEdit? edit, List<string> problems)
    {
        if (edit is null)
        {
            return;
        }

        if (part is null)
        {
            problems.Add($"This model has no {key} part.");
            return;
        }

        foreach (var value in edit.Values)
        {
            if (!ModelSecurity.IsValidValue(value))
            {
                problems.Add($"'{value}' isn't a valid {part.Name} value.");
            }
            else if (edit.Mode != SetEditMode.Remove && part.Values is { Count: > 0 } listed && !listed.Contains(value, StringComparer.Ordinal))
            {
                problems.Add($"'{value}' isn't one of {string.Join(", ", listed)}.");
            }
        }
    }

    private static Label Label(int level, string[] any, string[] all) => LabelCodec.ToLabel(level, any, all);

    private static Label Apply(ModelSecurity security, Label label, LabelEdit edit) => new(
        edit.Level is { } level ? security.LevelRank(level) : label.Level,
        edit.AnyOf is null ? label.AnyOf : Combine(label.AnyOf, edit.AnyOf),
        edit.AllOf is null ? label.AllOf : Combine(label.AllOf, edit.AllOf));

    private static IEnumerable<string> Combine(IReadOnlySet<string> current, SetEdit edit) => edit.Mode switch
    {
        SetEditMode.Replace => edit.Values.Distinct(),
        SetEditMode.Add => current.Union(edit.Values),
        _ => current.Except(edit.Values),
    };

    private static bool Same(Label a, Label b) => a.Level == b.Level && a.AnyOf.SetEquals(b.AnyOf) && a.AllOf.SetEquals(b.AllOf);

    // One change per part that differs, named for the part as the model calls it. A node's has just its id; a link's also has
    // its relationship and target.
    private static IEnumerable<PendingChange> Changes(ModelSecurity security, string entityId, string? relType, string? relatedId, Label before, Label after)
    {
        if (security.HasLevel && before.Level != after.Level)
        {
            yield return new PendingChange(ChangeEntityKind.Label, entityId, ChangeOp.Update, security.Level!.Name, relType, relatedId,
                LevelName(security, before.Level), LevelName(security, after.Level));
        }

        if (security.HasAnyOf && !before.AnyOf.SetEquals(after.AnyOf))
        {
            yield return new PendingChange(ChangeEntityKind.Label, entityId, ChangeOp.Update, security.AnyOf!.Name, relType, relatedId, Text(before.AnyOf), Text(after.AnyOf));
        }

        if (security.HasAllOf && !before.AllOf.SetEquals(after.AllOf))
        {
            yield return new PendingChange(ChangeEntityKind.Label, entityId, ChangeOp.Update, security.AllOf!.Name, relType, relatedId, Text(before.AllOf), Text(after.AllOf));
        }
    }

    private static string? LevelName(ModelSecurity security, int rank) => security.Level?.Values is { } values && rank >= 0 && rank < values.Count ? values[rank] : null;

    // A set as the history shows it: its values in order, comma separated (values can't contain commas), or nothing.
    private static string? Text(IReadOnlySet<string> values) => values.Count == 0 ? null : string.Join(", ", values.Order(StringComparer.Ordinal));

    private static LabelValue Describe(ModelSecurity? security, int level, string[] any, string[] all) => new(
        security is null ? null : LevelName(security, level),
        [.. any.Where(v => v != LabelCodec.Everyone).Order(StringComparer.Ordinal)],
        [.. all.Order(StringComparer.Ordinal)]);
}
