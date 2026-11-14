using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Linkbase.Core.Models;
using Linkbase.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace Linkbase.Core.Records;

public sealed partial class RecordRepository
{
    public async Task<IReadOnlyList<RecordType>> TypesAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var types = await db.NodeTypes.AsNoTracking()
            .OrderBy(t => t.SortOrder).ThenBy(t => t.Name)
            .Select(t => new
            {
                t.Name,
                t.Color,
                t.Icon,
                t.Owner,
                t.HierarchyField,
                t.HierarchyRel,
                t.NameTemplate,
                Fields = t.Fields.OrderBy(f => f.Id).Select(f => new { f.Key, f.Type, f.Options, f.Required, f.Owner, f.Description, f.GroupName, f.Multiple }).ToList(),
                Levels = t.HierarchyLevels.OrderBy(l => l.Id).Select(l => new { l.Kind, l.IsRoot, l.Under }).ToList(),
            })
            .ToListAsync(cancellationToken);
        var required = await db.LinkDefinitions.AsNoTracking()
            .Where(l => l.Required)
            .OrderBy(l => l.RelType).ThenBy(l => l.TargetType)
            .Select(l => new { l.SourceType, l.RelType, l.TargetType })
            .ToListAsync(cancellationToken);
        var limits = await db.LinkDefinitions.AsNoTracking()
            .Where(l => l.MaxLinks != null)
            .OrderBy(l => l.RelType).ThenBy(l => l.TargetType)
            .Select(l => new { l.SourceType, l.RelType, l.TargetType, Max = l.MaxLinks!.Value })
            .ToListAsync(cancellationToken);
        return types.Select(t => new RecordType(t.Name, t.Color, t.Icon,
            t.Fields.Select(f => new RecordField(f.Key, f.Type, f.Options ?? [], f.Owner)
            {
                Required = f.Required,
                Description = f.Description,
                Group = f.GroupName,
                Multiple = f.Multiple,
            }).ToList(), t.Owner)
        {
            NameTemplate = t.NameTemplate,
            LinkLimits = limits.Where(l => l.SourceType == t.Name).Select(l => new LinkLimit(l.RelType, l.TargetType, l.Max)).ToList(),
            RequiredLinks = required.Where(l => l.SourceType == t.Name).Select(l => new RequiredLink(l.RelType, l.TargetType)).ToList(),
            Hierarchy = t.HierarchyField is null || t.HierarchyRel is null
                ? null
                : new HierarchyRules(t.Name, t.HierarchyField, t.HierarchyRel, t.Levels.Select(l => new HierarchyLevelRule(l.Kind, l.IsRoot, l.Under)).ToList()),
        }).ToList();
    }

    public async Task<RecordType?> TypeAsync(string name, CancellationToken cancellationToken = default) =>
        (await TypesAsync(cancellationToken)).FirstOrDefault(t => t.Name == name);

    // In name order, or only those whose name or any value contains the search text.
    public Task<RecordRows> ListAsync(string type, string? search, int page, int pageSize, CancellationToken cancellationToken = default) =>
        ListAsync(type, search, null, [], page, pageSize, cancellationToken);

    // Sorted by a field and narrowed by filters on fields, as well as the search. Values are JSON, so a field is a lookup in
    // props; a field filtered often can have an expression index (docs/history/climbing-to-generic-plan.md).
    public async Task<RecordRows> ListAsync(
        string type, string? search, RecordSort? sort, IReadOnlyList<RecordFilter> filters, int page, int pageSize, CancellationToken cancellationToken = default)
    {
        // A type that isn't there, such as one an undone import took away, has no records.
        var definition = await TypeAsync(type, cancellationToken) ?? new RecordType(type, null, null, []);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var hidden = (await HiddenAsync(db, cancellationToken)).For(type);
        if (filters.Select(f => f.Field).Concat(sort?.Field is { } sorted ? [sorted] : []).FirstOrDefault(hidden.Contains) is { } secret)
        {
            // The same words as for a field that isn't there, so the message doesn't say that it exists.
            throw new LinkbaseValidationException($"{type} has no field called {secret}.");
        }

        var parameters = new List<(string Name, object Value, NpgsqlDbType Type)>();
        string Add(object value, NpgsqlDbType dbType)
        {
            var name = $"p{parameters.Count}";
            parameters.Add((name, value, dbType));
            return $"@{name}";
        }

        var where = new List<string> { $"type = {Add(type, NpgsqlDbType.Text)}", $"user_id = {Add(AppUser.DefaultId, NpgsqlDbType.Bigint)}" };
        if (!string.IsNullOrWhiteSpace(search))
        {
            // search_text holds every value, hidden ones too, so a viewer who can't see some searches the visible ones instead.
            // It's slower than the indexed column, which is why it's only for them.
            var column = hidden.Count == 0 ? "search_text" : $"graph_search_text(name, props - {Add(hidden.ToArray(), NpgsqlDbType.Array | NpgsqlDbType.Text)})";
            where.Add($"{column} ILIKE {Add($"%{EscapeLike(search.Trim())}%", NpgsqlDbType.Text)}");
        }

        foreach (var filter in filters)
        {
            where.Add(FilterSql(definition, filter, Add));
        }

        var order = OrderSql(definition, sort, Add);
        var from = $"FROM node WHERE {string.Join(" AND ", where)}";
        var offset = Add(page * pageSize, NpgsqlDbType.Integer);
        var limit = Add(pageSize, NpgsqlDbType.Integer);

        // Field keys and values only ever go in as parameters, so this is built from fixed pieces.
        var countSql = "SELECT count(*)::int AS \"Value\" " + from;
        var rowsSql = "SELECT id AS \"Id\", key AS \"Key\", name AS \"Name\", props::text AS \"Props\" " + from
            + " ORDER BY " + order + " OFFSET " + offset + " LIMIT " + limit;

        var total = await db.Database.SqlQueryRaw<int>(countSql, Materialize(parameters)).SingleAsync(cancellationToken);
        var rows = await db.Database
            .SqlQueryRaw<RawRecord>(rowsSql, Materialize(parameters))
            .ToListAsync(cancellationToken);
        return new RecordRows(total, rows.Select(r => new RecordRow(r.Id, r.Key, r.Name, Values(r.Props, hidden)) { Masked = Present(r.Props, hidden) }).ToList());
    }

    private sealed record RawRecord(string Id, string Key, string Name, string Props);

    // Each command needs parameter objects of its own.
    private static object[] Materialize(List<(string Name, object Value, NpgsqlDbType Type)> parameters) =>
        parameters.Select(p => (object)new NpgsqlParameter(p.Name, p.Type) { Value = p.Value }).ToArray();

    private static RecordField FieldFor(RecordType type, string key) =>
        type.Fields.FirstOrDefault(f => f.Key == key) ?? throw new LinkbaseValidationException($"{type.Name} has no field called {key}.");

    private static string FilterSql(RecordType type, RecordFilter filter, Func<object, NpgsqlDbType, string> add)
    {
        var field = FieldFor(type, filter.Field);
        var value = filter.Value.Trim();
        var column = $"props ->> {add(field.Key, NpgsqlDbType.Text)}";
        switch (field.Type, filter.Op)
        {
            case (FieldType.Text or FieldType.List, FilterOp.Contains):
                return $"{column} ILIKE {add($"%{EscapeLike(value)}%", NpgsqlDbType.Text)}";
            case (FieldType.Choice, FilterOp.Is) when field.Multiple:
                var chosen = FieldValues.MatchChoice(value, field.Options)
                    ?? throw new LinkbaseValidationException($"{Title(field.Key)} must be one of {FieldValues.Listed(field.Options)}.");
                return $"props -> {add(field.Key, NpgsqlDbType.Text)} ? {add(chosen, NpgsqlDbType.Text)}";
            case (FieldType.Number, FilterOp.Is or FilterOp.AtLeast or FilterOp.AtMost) when field.Multiple:
                if (!decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var each))
                {
                    throw new LinkbaseValidationException($"{Title(field.Key)} must be a number, such as 12.5.");
                }

                return $"EXISTS (SELECT 1 FROM jsonb_array_elements_text(CASE WHEN jsonb_typeof(props -> {add(field.Key, NpgsqlDbType.Text)}) = 'array' THEN props -> {add(field.Key, NpgsqlDbType.Text)} ELSE jsonb_build_array(props -> {add(field.Key, NpgsqlDbType.Text)}) END) e WHERE e::numeric {Compare(filter.Op)} {add(each, NpgsqlDbType.Numeric)})";
            case (FieldType.Date, FilterOp.Is or FilterOp.AtLeast or FilterOp.AtMost) when field.Multiple:
                if (!DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
                {
                    throw new LinkbaseValidationException($"{Title(field.Key)} must be a date, such as 2026-11-06.");
                }

                return $"EXISTS (SELECT 1 FROM jsonb_array_elements_text(CASE WHEN jsonb_typeof(props -> {add(field.Key, NpgsqlDbType.Text)}) = 'array' THEN props -> {add(field.Key, NpgsqlDbType.Text)} ELSE jsonb_build_array(props -> {add(field.Key, NpgsqlDbType.Text)}) END) e WHERE e {Compare(filter.Op)} {add(day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), NpgsqlDbType.Text)})";
            case (FieldType.Choice, FilterOp.Is):
                return $"{column} = {add(FieldValues.MatchChoice(value, field.Options) ?? throw new LinkbaseValidationException($"{Title(field.Key)} must be one of {FieldValues.Listed(field.Options)}."), NpgsqlDbType.Text)}";
            case (FieldType.Boolean, FilterOp.Is):
                return $"{column} = {add(bool.TryParse(value, out var flag) ? (flag ? "true" : "false") : throw new LinkbaseValidationException($"{Title(field.Key)} must be true or false."), NpgsqlDbType.Text)}";
            case (FieldType.Number, FilterOp.Is or FilterOp.AtLeast or FilterOp.AtMost):
                if (!decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var number))
                {
                    throw new LinkbaseValidationException($"{Title(field.Key)} must be a number, such as 12.5.");
                }

                return $"({column})::numeric {Compare(filter.Op)} {add(number, NpgsqlDbType.Numeric)}";
            case (FieldType.Date, FilterOp.Is or FilterOp.AtLeast or FilterOp.AtMost):
                if (!DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                {
                    throw new LinkbaseValidationException($"{Title(field.Key)} must be a date, such as 2026-11-06.");
                }

                // Dates are stored as yyyy-MM-dd, which sorts as text.
                return $"{column} {Compare(filter.Op)} {add(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), NpgsqlDbType.Text)}";
            default:
                throw new LinkbaseValidationException($"{Title(field.Key)} can't be filtered that way.");
        }
    }

    private static string Compare(FilterOp op) => op switch { FilterOp.AtLeast => ">=", FilterOp.AtMost => "<=", _ => "=" };

    private static string OrderSql(RecordType type, RecordSort? sort, Func<object, NpgsqlDbType, string> add)
    {
        var direction = sort?.Descending == true ? "DESC" : "ASC";
        switch (sort?.Field)
        {
            case null or "" or "name":
                return $"name {direction}, key {direction}";
            case "key":
                return $"key {direction}, name";
            default:
                var field = FieldFor(type, sort.Field);
                var column = $"props ->> {add(field.Key, NpgsqlDbType.Text)}";
                var expression = field.IsMany ? $"lower({column})" : field.Type == FieldType.Number ? $"({column})::numeric" : field.Type is FieldType.Text or FieldType.Choice or FieldType.List ? $"lower({column})" : column;
                return $"{expression} {direction} NULLS LAST, name, key";
        }
    }

    public async Task<RecordDetail?> GetAsync(string type, string key, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var node = await db.Nodes.AsNoTracking()
            .Where(n => n.Type == type && n.Key == key && n.UserId == AppUser.DefaultId)
            .Select(n => new { n.Id, n.Type, n.Key, n.Name, n.Props, n.CreatedAt, n.UpdatedAt })
            .SingleOrDefaultAsync(cancellationToken);
        if (node is null)
        {
            return null;
        }

        var hidden = (await HiddenAsync(db, cancellationToken)).For(node.Type);
        return new RecordDetail(node.Id, node.Type, node.Key, node.Name, Values(node.Props, hidden), node.CreatedAt, node.UpdatedAt) { Masked = Present(node.Props, hidden) };
    }

    // Every kind of link the record has, each with its first few links in name order and the total.
    public async Task<IReadOnlyList<RecordLinkGroup>> LinksAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var outgoing = await db.NodeLinks.AsNoTracking().Where(l => l.SourceId == id)
            .GroupBy(l => l.RelType).Select(g => new { RelType = g.Key, Total = g.Count() }).ToListAsync(cancellationToken);
        var incoming = await db.NodeLinks.AsNoTracking().Where(l => l.TargetId == id)
            .GroupBy(l => l.RelType).Select(g => new { RelType = g.Key, Total = g.Count() }).ToListAsync(cancellationToken);

        var groups = new List<RecordLinkGroup>();
        foreach (var group in outgoing.OrderBy(g => g.RelType))
        {
            var links = await db.NodeLinks.AsNoTracking()
                .Where(l => l.SourceId == id && l.RelType == group.RelType)
                .OrderBy(l => l.Target.Name).ThenBy(l => l.Id)
                .Take(LinksShown)
                .Select(l => new { l.Id, l.TargetId, l.Target.Type, l.Target.Key, l.Target.Name, l.Props })
                .ToListAsync(cancellationToken);
            groups.Add(new RecordLinkGroup(group.RelType, true, group.Total,
                links.Select(l => new RecordLink(l.Id, l.TargetId, l.Type, l.Key, l.Name) { Values = Values(l.Props) }).ToList()));
        }

        foreach (var group in incoming.OrderBy(g => g.RelType))
        {
            var links = await db.NodeLinks.AsNoTracking()
                .Where(l => l.TargetId == id && l.RelType == group.RelType)
                .OrderBy(l => l.Source.Name).ThenBy(l => l.Id)
                .Take(LinksShown)
                .Select(l => new { l.Id, l.SourceId, l.Source.Type, l.Source.Key, l.Source.Name, l.Props })
                .ToListAsync(cancellationToken);
            groups.Add(new RecordLinkGroup(group.RelType, false, group.Total,
                links.Select(l => new RecordLink(l.Id, l.SourceId, l.Type, l.Key, l.Name) { Values = Values(l.Props) }).ToList()));
        }

        return groups;
    }

    // The values a link of this kind can carry, in the order the model gives them.
    public async Task<IReadOnlyList<RecordField>> LinkAttributesAsync(string sourceType, string relType, string targetType, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await AttributesAsync(db, sourceType, relType, targetType, cancellationToken);
    }

    internal static async Task<List<RecordField>> AttributesAsync(
        LinkbaseDbContext db, string sourceType, string relType, string targetType, CancellationToken cancellationToken)
    {
        var attributes = await db.LinkAttributes.AsNoTracking()
            .Where(a => a.LinkDefinition.SourceType == sourceType && a.LinkDefinition.RelType == relType && a.LinkDefinition.TargetType == targetType)
            .OrderBy(a => a.Id)
            .Select(a => new { a.Key, a.Type, a.Options, a.Required, a.Description, a.Multiple })
            .ToListAsync(cancellationToken);
        return attributes.Select(a => new RecordField(a.Key, a.Type, a.Options ?? []) { Required = a.Required, Description = a.Description, Multiple = a.Multiple }).ToList();
    }

    // The kinds of link a record of this type can have, either way round. Links to a module's derived records are its own,
    // so they're left out.
    public async Task<IReadOnlyList<LinkOption>> LinkOptionsAsync(string type, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var definitions = await db.LinkDefinitions.AsNoTracking()
            .Where(l => (l.SourceType == type || l.TargetType == type) && l.Source.Owner == null && l.Target.Owner == null)
            .OrderBy(l => l.RelType)
            .Select(l => new { l.SourceType, l.RelType, l.TargetType })
            .ToListAsync(cancellationToken);
        var options = new List<LinkOption>();
        foreach (var definition in definitions)
        {
            if (definition.SourceType == type)
            {
                options.Add(new LinkOption(definition.RelType, true, definition.TargetType));
            }

            if (definition.TargetType == type)
            {
                options.Add(new LinkOption(definition.RelType, false, definition.SourceType));
            }
        }

        return options;
    }

    public async Task<IReadOnlyList<RelationshipDefinition>> RelationshipsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.LinkDefinitions.AsNoTracking()
            .OrderBy(l => l.SourceType).ThenBy(l => l.RelType).ThenBy(l => l.TargetType)
            .Select(l => new RelationshipDefinition(l.SourceType, l.RelType, l.TargetType))
            .ToListAsync(cancellationToken);
    }

    // Records of a type whose name contains the text, for choosing the other end of a link.
    public async Task<IReadOnlyList<RecordOption>> FindAsync(string type, string? text, int limit, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var query = db.Nodes.AsNoTracking().Where(n => n.Type == type && n.UserId == AppUser.DefaultId);
        if (!string.IsNullOrWhiteSpace(text))
        {
            var pattern = $"%{EscapeLike(text.Trim())}%";
            query = query.Where(n => EF.Functions.ILike(n.Name, pattern) || n.Key == text.Trim());
        }

        return await query.OrderBy(n => n.Name).ThenBy(n => n.Key).Take(limit)
            .Select(n => new RecordOption(n.Id, n.Name, n.Key))
            .ToListAsync(cancellationToken);
    }
}
