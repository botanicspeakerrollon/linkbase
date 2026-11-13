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
// that changes records or links calls it (ChangeRecorderCoverageTests checks that). See docs/audit-history-plan.md.
public static class ChangeRecorder
{
    public const int NoteLimit = 500;
    public const int SourceIdLimit = 500;

    // The change set whose hooks are running, so a module's rebuild can say what caused it.
    private static readonly AsyncLocal<long?> CurrentCause = new();

    public static long? Cause => CurrentCause.Value;

    public static IDisposable Causing(long? changeSetId)
    {
        var previous = CurrentCause.Value;
        CurrentCause.Value = changeSetId;
        return new Restore(previous);
    }

    private sealed class Restore(long? previous) : IDisposable
    {
        public void Dispose() => CurrentCause.Value = previous;
    }

    public static async Task<HistorySettings> SettingsAsync(LinkbaseDbContext db, CancellationToken cancellationToken) =>
        await db.ModelSettings.AsNoTracking()
            .Select(s => HistorySettings.From(s.RecordHistory, s.RequireReason, s.SourceKinds))
            .SingleOrDefaultAsync(cancellationToken) ?? HistorySettings.Default;

    public static async Task<HistorySettings> SettingsAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("SELECT record_history, require_reason, source_kinds FROM model_setting WHERE id = 1", connection, transaction);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? HistorySettings.From(reader.GetBoolean(0), reader.GetBoolean(1), reader.GetFieldValue<string[]>(2))
            : HistorySettings.Default;
    }

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

    // Saves a change set with its justification and changes, in the transaction the context is in. Does nothing, and
    // returns null, when there are no changes. Callers have already checked that history is being recorded.
    public static Task<long?> WriteAsync(
        LinkbaseDbContext db, JustificationKind kind, ChangeReason? reason, IReadOnlyList<PendingChange> changes,
        long? importBatchId = null, long? causeChangeSetId = null, CancellationToken cancellationToken = default) =>
        WriteAsync((NpgsqlConnection)db.Database.GetDbConnection(), (NpgsqlTransaction)db.Database.CurrentTransaction!.GetDbTransaction(),
            kind, reason?.Note, reason?.SourceId, changes, importBatchId, causeChangeSetId, reason?.SourceKind, reason?.SourceComment, cancellationToken);

    public static async Task<long?> WriteAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, JustificationKind kind, string? note, string? sourceId,
        IReadOnlyList<PendingChange> changes, long? importBatchId = null, long? causeChangeSetId = null,
        string? sourceKind = null, string? sourceComment = null, CancellationToken cancellationToken = default)
    {
        if (changes.Count == 0)
        {
            return null;
        }

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
