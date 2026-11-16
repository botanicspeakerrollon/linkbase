using System.Text.Json;
using Linkbase.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using NpgsqlTypes;

namespace Linkbase.Core.History;

// Why a change is being made, as a person typed it. The note is a brief paraphrase, never text copied from a source.
// The source says where it came from: an id (a web address, a ticket number, a document name), the kind of place that is, and
// a brief remark about it.
public sealed record ChangeReason(string Note, string? SourceId = null, string? SourceKind = null, string? SourceComment = null);

// The model's history settings. With no row saved both are on and Internet is the one kind of source.
public sealed record HistorySettings(bool Record, bool RequireReason, IReadOnlyList<string>? DeclaredSourceKinds = null)
{
    public const string Internet = "Internet";

    public static readonly HistorySettings Default = new(true, true);

    // None declared is kept as null, so settings with the same values are equal.
    public static HistorySettings From(bool record, bool requireReason, string[] declaredSourceKinds) =>
        new(record, requireReason, declaredSourceKinds.Length == 0 ? null : declaredSourceKinds);

    // Whether a person has to give a reason before a change is saved.
    public bool NeedsReason => Record && RequireReason;

    public IReadOnlyList<string> SourceKinds => DeclaredSourceKinds is { Count: > 0 } ? DeclaredSourceKinds : [Internet];
}

// One thing that changed, before it's saved with its change set.
public sealed record PendingChange(
    ChangeEntityKind Kind, string EntityId, ChangeOp Op, string? FieldKey = null, string? RelType = null, string? RelatedId = null,
    string? OldValue = null, string? NewValue = null);

// Writes the audit trail, in the same transaction as the change it describes, so the two can't disagree. Every code path
// that changes records or links calls it (ChangeRecorderCoverageTests checks that). See docs/history/audit-history-plan.md.
public static class ChangeRecorder
{
    public const int NoteLimit = 500;
    public const int SourceIdLimit = 500;

    // How many things one change event lists; its entity count says how many there were.
    public const int EventEntityLimit = 1000;

    // The advisory lock that puts change events in commit order, and the channel told of each new one.
    public const long EventLock = 7_317_146_215_001;
    public const string EventChannel = "linkbase_change_event";

    public static async Task<HistorySettings> SettingsAsync(LinkbaseDbContext db, CancellationToken cancellationToken) =>
        await db.ModelSettings.AsNoTracking()
            .Select(s => HistorySettings.From(s.RecordHistory, s.RequireReason, s.SourceKinds))
            .SingleOrDefaultAsync(cancellationToken) ?? HistorySettings.Default;

    // Checks the reason a person gave, before any work is done. Returns it trimmed, or null where none is kept. A reason
    // that was given is checked even when it isn't needed, so a bad link doesn't get through.
    public static ChangeReason? Check(HistorySettings settings, ChangeReason? reason)
    {
        var note = Clean(reason?.Note);
        var sourceId = Clean(reason?.SourceId);
        var kind = Clean(reason?.SourceKind);
        var comment = Clean(reason?.SourceComment);
        if (!settings.Record)
        {
            return null;
        }

        if (string.IsNullOrEmpty(note) && settings.RequireReason)
        {
            throw new LinkbaseValidationException("Say why you're making this change.");
        }

        if (note is { Length: > NoteLimit })
        {
            throw new LinkbaseValidationException($"The reason is too long. Keep it under {NoteLimit} characters, in your own words.");
        }

        if (sourceId is { Length: > SourceIdLimit })
        {
            throw new LinkbaseValidationException($"The source id is too long. Keep it under {SourceIdLimit} characters.");
        }

        if (comment is { Length: > NoteLimit })
        {
            throw new LinkbaseValidationException($"The source comment is too long. Keep it under {NoteLimit} characters, in your own words.");
        }

        if (kind is not null)
        {
            // Match the model's spelling of the kind, so Internet and internet don't both end up in the history.
            var declared = settings.SourceKinds.FirstOrDefault(k => string.Equals(k, kind, StringComparison.OrdinalIgnoreCase));
            kind = declared ?? throw new LinkbaseValidationException($"'{kind}' isn't one of this model's kinds of source: {string.Join(", ", settings.SourceKinds)}.");
        }

        // A web address is checked, so an unsafe one such as javascript: can't be kept and later shown as a link.
        if (sourceId is not null && sourceId.Contains("://", StringComparison.Ordinal)
            && !(Uri.TryCreate(sourceId, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"))
        {
            throw new LinkbaseValidationException("A source that is a web address needs to start with http:// or https://.");
        }

        return note is null && sourceId is null && kind is null && comment is null ? null : new ChangeReason(note ?? "", sourceId, kind, comment);
    }

    private static string? Clean(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    // Saves what changed, in the transaction the context is in: the change set with its justification and changes when the
    // model records history, and the change event for other systems in all cases. Does nothing, and returns null, when there
    // are no changes. Returns the change set's id, or null when history is off. Callers pass the settings rather than
    // checking them, so a model without history still raises events (ChangeRecorderCoverageTests checks that).
    public static Task<long?> WriteAsync(
        LinkbaseDbContext db, HistorySettings settings, JustificationKind kind, ChangeReason? reason, IReadOnlyList<PendingChange> changes,
        long? importBatchId = null, long? causeChangeSetId = null, CancellationToken cancellationToken = default) =>
        WriteAsync(Connection(db), Transaction(db), settings.Record,
            kind, reason?.Note, reason?.SourceId, changes, importBatchId, causeChangeSetId, reason?.SourceKind, reason?.SourceComment, cancellationToken);

    public static async Task<long?> WriteAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, bool recordHistory, JustificationKind kind, string? note, string? sourceId,
        IReadOnlyList<PendingChange> changes, long? importBatchId = null, long? causeChangeSetId = null,
        string? sourceKind = null, string? sourceComment = null, CancellationToken cancellationToken = default)
    {
        if (changes.Count == 0)
        {
            return null;
        }

        long? changeSetId = recordHistory
            ? await WriteHistoryAsync(connection, transaction, kind, note, sourceId, changes, importBatchId, causeChangeSetId, sourceKind, sourceComment, cancellationToken)
            : null;
        await WriteEventAsync(connection, transaction, kind, changeSetId, importBatchId, causeChangeSetId, changes, cancellationToken);
        return changeSetId;
    }

    // Raises a change event alone, for a change that the history doesn't log because it follows from one it does, such as an
    // ascent taking its route's new name. The cause is that change's change set, when history is on.
    public static Task WriteEventAsync(
        LinkbaseDbContext db, JustificationKind kind, IReadOnlyList<PendingChange> changes, long? causeChangeSetId, CancellationToken cancellationToken) =>
        changes.Count == 0
            ? Task.CompletedTask
            : WriteEventAsync(Connection(db), Transaction(db), kind, null, null, causeChangeSetId, changes, cancellationToken);

    private static NpgsqlConnection Connection(LinkbaseDbContext db) => (NpgsqlConnection)db.Database.GetDbConnection();

    private static NpgsqlTransaction Transaction(LinkbaseDbContext db) => (NpgsqlTransaction)db.Database.CurrentTransaction!.GetDbTransaction();

    private static async Task<long> WriteHistoryAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, JustificationKind kind, string? note, string? sourceId,
        IReadOnlyList<PendingChange> changes, long? importBatchId, long? causeChangeSetId, string? sourceKind, string? sourceComment,
        CancellationToken cancellationToken)
    {
        long changeSetId;
        await using (var command = new NpgsqlCommand(
            """
            WITH j AS (
                INSERT INTO justification (kind, note, source_id, source_kind, source_comment, import_batch_id, user_id)
                VALUES (@kind, nullif(@note, ''), @sourceId, @sourceKind, @sourceComment, @batch, @user) RETURNING id
            )
            INSERT INTO change_set (justification_id, cause_change_set_id) SELECT id, @cause FROM j RETURNING id
            """, connection, transaction))
        {
            command.Parameters.Add(new NpgsqlParameter("kind", NpgsqlDbType.Text) { Value = SnakeCaseEnumConverter<JustificationKind>.ToStore(kind) });
            command.Parameters.Add(new NpgsqlParameter("note", NpgsqlDbType.Text) { Value = (object?)note ?? DBNull.Value });
            command.Parameters.Add(new NpgsqlParameter("sourceId", NpgsqlDbType.Text) { Value = (object?)sourceId ?? DBNull.Value });
            command.Parameters.Add(new NpgsqlParameter("sourceKind", NpgsqlDbType.Text) { Value = (object?)sourceKind ?? DBNull.Value });
            command.Parameters.Add(new NpgsqlParameter("sourceComment", NpgsqlDbType.Text) { Value = (object?)sourceComment ?? DBNull.Value });
            command.Parameters.Add(new NpgsqlParameter("batch", NpgsqlDbType.Bigint) { Value = (object?)importBatchId ?? DBNull.Value });
            command.Parameters.Add(new NpgsqlParameter("user", NpgsqlDbType.Bigint) { Value = AppUser.DefaultId });
            command.Parameters.Add(new NpgsqlParameter("cause", NpgsqlDbType.Bigint) { Value = (object?)causeChangeSetId ?? DBNull.Value });
            changeSetId = (long)(await command.ExecuteScalarAsync(cancellationToken))!;
        }

        await using var insert = new NpgsqlCommand(
            """
            INSERT INTO change (change_set_id, entity_kind, entity_id, op, field_key, rel_type, related_id, old_value, new_value)
            SELECT @set, k, e, o, f, r, x, ov, nv FROM unnest(@kinds, @entities, @ops, @fields, @rels, @related, @olds, @news) AS t(k, e, o, f, r, x, ov, nv)
            """, connection, transaction)
        { CommandTimeout = 0 };
        insert.Parameters.Add(new NpgsqlParameter("set", NpgsqlDbType.Bigint) { Value = changeSetId });
        Add(insert, "kinds", changes.Select(c => SnakeCaseEnumConverter<ChangeEntityKind>.ToStore(c.Kind)));
        Add(insert, "entities", changes.Select(c => c.EntityId));
        Add(insert, "ops", changes.Select(c => SnakeCaseEnumConverter<ChangeOp>.ToStore(c.Op)));
        Add(insert, "fields", changes.Select(c => c.FieldKey));
        Add(insert, "rels", changes.Select(c => c.RelType));
        Add(insert, "related", changes.Select(c => c.RelatedId));
        Add(insert, "olds", changes.Select(c => c.OldValue));
        Add(insert, "news", changes.Select(c => c.NewValue));
        await insert.ExecuteNonQueryAsync(cancellationToken);
        return changeSetId;
    }

    // The event takes a lock held until commit before it gets its id, so the next writer's event both has a higher id and
    // commits later: a reader that takes events in id order never skips one that committed late. Advisory locks belong to one
    // database, and each model has its own, so this is one queue per model. NOTIFY is only delivered on commit.
    private static async Task WriteEventAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, JustificationKind kind, long? changeSetId, long? importBatchId,
        long? causeChangeSetId, IReadOnlyList<PendingChange> changes, CancellationToken cancellationToken)
    {
        var (entities, count) = EventEntities(changes);
        await using var command = new NpgsqlCommand(
            $"""
            SELECT pg_advisory_xact_lock({EventLock});
            WITH e AS (
                INSERT INTO change_event (change_set_id, kind, import_batch_id, cause_change_set_id, entities, entity_count)
                VALUES (@set, @kind, @batch, @cause, @entities, @count) RETURNING id
            )
            SELECT pg_notify('{EventChannel}', id::text) FROM e;
            """, connection, transaction);
        command.Parameters.Add(new NpgsqlParameter("set", NpgsqlDbType.Bigint) { Value = (object?)changeSetId ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("kind", NpgsqlDbType.Text) { Value = SnakeCaseEnumConverter<JustificationKind>.ToStore(kind) });
        command.Parameters.Add(new NpgsqlParameter("batch", NpgsqlDbType.Bigint) { Value = (object?)importBatchId ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("cause", NpgsqlDbType.Bigint) { Value = (object?)causeChangeSetId ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("entities", NpgsqlDbType.Jsonb) { Value = entities });
        command.Parameters.Add(new NpgsqlParameter("count", NpgsqlDbType.Integer) { Value = count });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    // One entry per thing changed, in the order first seen: a record's field edits are one entry listing the keys, with no
    // values. A link is named by its source, relationship and target. Only the first EventEntityLimit are kept.
    public static (string Json, int Count) EventEntities(IReadOnlyList<PendingChange> changes)
    {
        var groups = changes
            .GroupBy(c => (c.Kind, c.EntityId, c.RelType, c.RelatedId))
            .Select(g =>
            {
                var ops = g.Select(c => c.Op).ToHashSet();
                var op = ops.Contains(ChangeOp.Create) && !ops.Contains(ChangeOp.Delete) ? ChangeOp.Create
                    : ops.Contains(ChangeOp.Delete) && !ops.Contains(ChangeOp.Create) ? ChangeOp.Delete
                    : ChangeOp.Update;
                var fields = g.Where(c => c.Op == ChangeOp.Update && c.FieldKey is not null).Select(c => c.FieldKey!).Distinct().ToArray();
                var link = g.Key.RelType is not null;
                return new EventEntity(
                    SnakeCaseEnumConverter<ChangeEntityKind>.ToStore(g.Key.Kind),
                    link ? null : g.Key.EntityId,
                    link ? g.Key.EntityId : null,
                    g.Key.RelType,
                    link ? g.Key.RelatedId : null,
                    SnakeCaseEnumConverter<ChangeOp>.ToStore(op),
                    fields.Length > 0 ? fields : null);
            })
            .ToList();
        return (JsonSerializer.Serialize(groups.Take(EventEntityLimit), EventJson), groups.Count);
    }

    private sealed record EventEntity(string Kind, string? Id, string? Source, string? Rel, string? Target, string Op, string[]? Fields);

    private static readonly JsonSerializerOptions EventJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private static void Add(NpgsqlCommand command, string name, IEnumerable<string?> values) =>
        command.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = values.ToArray() });

    // The changes that describe a record or link being created, edited or deleted. Values are as RecordRepository.Values
    // gives them: one text per key, a list's values one per line.

    public static PendingChange NodeCreated(string id, string name, IReadOnlyDictionary<string, string> values) =>
        new(ChangeEntityKind.Node, id, ChangeOp.Create, NewValue: Snapshot(name, values));

    public static PendingChange NodeDeleted(string id, string name, IReadOnlyDictionary<string, string> values) =>
        new(ChangeEntityKind.Node, id, ChangeOp.Delete, OldValue: Snapshot(name, values));

    public static IEnumerable<PendingChange> NodeUpdated(
        string id, string nameBefore, IReadOnlyDictionary<string, string> before, string nameAfter, IReadOnlyDictionary<string, string> after)
    {
        if (nameBefore != nameAfter)
        {
            yield return new PendingChange(ChangeEntityKind.Node, id, ChangeOp.Update, "name", OldValue: nameBefore, NewValue: nameAfter);
        }

        foreach (var key in Differing(before, after))
        {
            yield return new PendingChange(ChangeEntityKind.Node, id, ChangeOp.Update, key, OldValue: before.GetValueOrDefault(key), NewValue: after.GetValueOrDefault(key));
        }
    }

    public static PendingChange LinkCreated(string sourceId, string relType, string targetId, IReadOnlyDictionary<string, string> values) =>
        new(ChangeEntityKind.Link, sourceId, ChangeOp.Create, RelType: relType, RelatedId: targetId, NewValue: Json(values));

    public static PendingChange LinkDeleted(string sourceId, string relType, string targetId, IReadOnlyDictionary<string, string> values) =>
        new(ChangeEntityKind.Link, sourceId, ChangeOp.Delete, RelType: relType, RelatedId: targetId, OldValue: Json(values));

    public static IEnumerable<PendingChange> LinkUpdated(
        string sourceId, string relType, string targetId, IReadOnlyDictionary<string, string> before, IReadOnlyDictionary<string, string> after) =>
        Differing(before, after).Select(key => new PendingChange(
            ChangeEntityKind.Link, sourceId, ChangeOp.Update, key, relType, targetId, before.GetValueOrDefault(key), after.GetValueOrDefault(key)));

    private static IEnumerable<string> Differing(IReadOnlyDictionary<string, string> before, IReadOnlyDictionary<string, string> after) =>
        before.Keys.Union(after.Keys).Order(StringComparer.Ordinal).Where(k => before.GetValueOrDefault(k) != after.GetValueOrDefault(k));

    internal static string Snapshot(string name, IReadOnlyDictionary<string, string> values) =>
        JsonSerializer.Serialize(new { name, values = values.OrderBy(v => v.Key, StringComparer.Ordinal).ToDictionary(v => v.Key, v => v.Value) });

    internal static string Json(IReadOnlyDictionary<string, string> values) =>
        JsonSerializer.Serialize(values.OrderBy(v => v.Key, StringComparer.Ordinal).ToDictionary(v => v.Key, v => v.Value));
}
