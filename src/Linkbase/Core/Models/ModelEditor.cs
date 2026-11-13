using System.Text.Json;
using Linkbase.Core.History;
using Linkbase.Core.Records;
using Linkbase.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using NpgsqlTypes;

namespace Linkbase.Core.Models;

// Changes a model's types, fields, relationships, link values and hierarchies from the app, so a model needs no developer
// once it is started. Every change is checked against the records and links already there, and is refused with the reason
// when they wouldn't fit. Each is one transaction that also writes the audit trail (a Definition entry), so it can be read
// back in History. A type or field that a domain module owns can't be touched, and one it reads or writes (Watched) can only
// be added to. See docs/models.md.
public sealed partial class ModelEditor(IDbContextFactory<LinkbaseDbContext> contextFactory, RecordHooks? hooks = null)
{
    private readonly RecordHooks _hooks = hooks ?? RecordHooks.None;

    public const int MaxDescription = 300;
    public const int MaxGroup = 40;
    private const int Examples = 5;

    public async Task<ModelOutline> OutlineAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var types = await db.NodeTypes.AsNoTracking().OrderBy(t => t.SortOrder).ThenBy(t => t.Name)
            .Select(t => new { t.Name, t.Color, t.Icon, t.NameTemplate, t.Owner, t.HierarchyField, t.HierarchyRel }).ToListAsync(cancellationToken);
        var fields = (await db.FieldDefinitions.AsNoTracking().OrderBy(f => f.Id)
                .Select(f => new { f.NodeTypeName, f.Key, f.Type, f.Options, f.Required, f.Multiple, f.Description, f.GroupName, f.Owner })
                .ToListAsync(cancellationToken))
            .ToLookup(f => f.NodeTypeName, f => Spec(f.Key, f.Type, f.Options, f.Required, f.Multiple, f.Description, f.GroupName, f.Owner));
        var levels = (await db.HierarchyLevels.AsNoTracking().OrderBy(l => l.Id).Select(l => new { l.NodeTypeName, l.Kind, l.IsRoot, l.Under }).ToListAsync(cancellationToken))
            .ToLookup(l => l.NodeTypeName, l => new LevelSpec(l.Kind, l.IsRoot, l.Under));
        var attributes = (await db.LinkAttributes.AsNoTracking().OrderBy(a => a.Id)
                .Select(a => new { a.LinkDefinitionId, a.Key, a.Type, a.Options, a.Required, a.Multiple, a.Description })
                .ToListAsync(cancellationToken))
            .ToLookup(a => a.LinkDefinitionId, a => Spec(a.Key, a.Type, a.Options, a.Required, a.Multiple, a.Description, null, null));
        var relationships = await db.LinkDefinitions.AsNoTracking().OrderBy(l => l.SourceType).ThenBy(l => l.RelType).ThenBy(l => l.TargetType)
            .Select(l => new { l.Id, l.SourceType, l.RelType, l.TargetType, l.Required, l.MaxLinks, l.Links }).ToListAsync(cancellationToken);

        return new ModelOutline(
            types.Select(t => new TypeOutline(
                t.Name, t.Color, t.Icon, t.NameTemplate, t.Owner, _hooks.Watch(t.Name), fields[t.Name].ToList(),
                t.HierarchyField is { } by && t.HierarchyRel is { } via ? new HierarchySpec(by, via, levels[t.Name].ToList()) : null)).ToList(),
            relationships.Select(r => new RelationshipOutline(
                r.SourceType, r.RelType, r.TargetType, r.Required, r.MaxLinks, r.Links, _hooks.Watch(r.SourceType) || _hooks.Watch(r.TargetType), attributes[r.Id].ToList())).ToList());
    }

    // How many records a type has. Counted on its own, because counting every type at once is slow in a very large model.
    public async Task<long> CountRecordsAsync(string type, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.Nodes.AsNoTracking().LongCountAsync(n => n.Type == type, cancellationToken);
    }

    // A text field with several values is stored as the List type, and the editor shows it as Text with Multiple.
    internal static FieldSpec Spec(string key, FieldType type, List<string>? options, bool required, bool multiple, string? description, string? group, string? owner) =>
        new(key, type == FieldType.List ? FieldType.Text : type, options is { Count: > 0 } ? options : null, required, multiple || type == FieldType.List, description, group) { Owner = owner };

    internal static FieldType Stored(FieldSpec spec) => spec.Multiple && spec.Type == FieldType.Text ? FieldType.List : spec.Type;

    // Runs the edit in one transaction with the audit trail written in it. The reason is checked first, before any work.
    private async Task<T> WriteAsync<T>(ChangeReason? reason, Func<LinkbaseDbContext, List<PendingChange>, Task<T>> work, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var settings = await ChangeRecorder.SettingsAsync(db, cancellationToken);
        var kept = ChangeRecorder.Check(settings, reason);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var changes = new List<PendingChange>();
        var result = await work(db, changes);
        if (settings.Record)
        {
            await ChangeRecorder.WriteAsync(db, JustificationKind.Manual, kept, changes, cancellationToken: cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    private async Task WriteAsync(ChangeReason? reason, Func<LinkbaseDbContext, List<PendingChange>, Task> work, CancellationToken cancellationToken) =>
        await WriteAsync<bool>(reason, async (db, changes) =>
        {
            await work(db, changes);
            return true;
        }, cancellationToken);

    private static string Json(object value) => JsonSerializer.Serialize(value, JsonSerializerOptions.Web);

    private static PendingChange Defined(string entity, ChangeOp op, string? field = null, string? from = null, string? to = null) =>
        new(ChangeEntityKind.Definition, entity, op, field, OldValue: from, NewValue: to);

    private static object Describe(FieldSpec spec) => new
    {
        key = spec.Key,
        type = spec.Type.ToString().ToLowerInvariant(),
        options = spec.Options,
        required = spec.Required,
        multiple = spec.Multiple,
        description = spec.Description,
        group = spec.Group,
    };

    // ---- SQL helpers. Values always go in as parameters; only keys that matched the naming rules reach the text.

    internal readonly record struct Param(string Name, object? Value, NpgsqlDbType Type);

    internal static Param Text(string name, string? value) => new(name, value, NpgsqlDbType.Text);

    internal static Param TextArray(string name, IEnumerable<string> values) => new(name, values.ToArray(), NpgsqlDbType.Array | NpgsqlDbType.Text);

    private static NpgsqlCommand Command(LinkbaseDbContext db, string sql, IEnumerable<Param> parameters)
    {
        var command = new NpgsqlCommand(sql, (NpgsqlConnection)db.Database.GetDbConnection(), (NpgsqlTransaction)db.Database.CurrentTransaction!.GetDbTransaction())
        {
            CommandTimeout = 0,
        };
        foreach (var (name, value, type) in parameters)
        {
            command.Parameters.Add(new NpgsqlParameter(name, type) { Value = value ?? DBNull.Value });
        }

        return command;
    }

    internal static async Task<long> ExecuteAsync(LinkbaseDbContext db, string sql, IEnumerable<Param> parameters, CancellationToken cancellationToken)
    {
        await using var command = Command(db, sql, parameters);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    internal static async Task<List<string>> StringsAsync(LinkbaseDbContext db, string sql, IEnumerable<Param> parameters, CancellationToken cancellationToken)
    {
        await using var command = Command(db, sql, parameters);
        var found = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            found.Add(reader.GetString(0));
        }

        return found;
    }

    internal static async Task<long> CountAsync(LinkbaseDbContext db, string sql, IEnumerable<Param> parameters, CancellationToken cancellationToken)
    {
        await using var command = Command(db, sql, parameters);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture);
    }

    // The records of a type, or the links of a relationship, whose values (props) a change rewrites. P is how SQL names the
    // props column, and the conditions and expressions given to it are written in terms of P and the parameters they bring.
    private sealed class Scope(FieldHome home)
    {
        public string P => home.IsLink ? "l.props" : "n.props";

        private IEnumerable<Param> Own => home.IsLink
            ? [Text("sc_from", home.Type), Text("sc_rel", home.Rel), Text("sc_to", home.To)]
            : [Text("sc_type", home.Type)];

        private string From => home.IsLink
            ? "node_link l JOIN node s ON s.id = l.source_id JOIN node t ON t.id = l.target_id"
            : "node n";

        private string Where => home.IsLink
            ? "s.type = @sc_from AND t.type = @sc_to AND l.rel_type = @sc_rel"
            : "n.type = @sc_type";

        public Task<long> CountAsync(LinkbaseDbContext db, string condition, CancellationToken cancellationToken, params Param[] parameters) =>
            ModelEditor.CountAsync(db, $"SELECT count(*) FROM {From} WHERE {Where} AND ({condition})", [.. Own, .. parameters], cancellationToken);

        public Task<List<string>> ExamplesAsync(LinkbaseDbContext db, string expression, string condition, int limit, CancellationToken cancellationToken, params Param[] parameters) =>
            StringsAsync(db, $"SELECT DISTINCT {expression} FROM {From} WHERE {Where} AND ({condition}) ORDER BY 1 LIMIT {limit}", [.. Own, .. parameters], cancellationToken);

        // Rewrites the props of the rows the condition picks. A record is then edited as far as undo is concerned, so its
        // updated_at moves.
        public Task<long> UpdateAsync(LinkbaseDbContext db, string expression, string condition, CancellationToken cancellationToken, params Param[] parameters) =>
            ExecuteAsync(
                db,
                home.IsLink
                    ? $"UPDATE node_link l SET props = {expression} FROM node s, node t WHERE s.id = l.source_id AND t.id = l.target_id AND {Where} AND ({condition})"
                    : $"UPDATE node n SET props = {expression}, updated_at = now() WHERE {Where} AND ({condition})",
                [.. Own, .. parameters], cancellationToken);
    }
}
