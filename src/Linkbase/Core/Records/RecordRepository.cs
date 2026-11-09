using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Linkbase.Core.Models;
using Linkbase.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace Linkbase.Core.Records;

// Options are a choice field's values, in the order to offer them, and empty for every other type.
public sealed record RecordField(string Key, FieldType Type, IReadOnlyList<string> Options)
{
    public RecordField(string key, FieldType type)
        : this(key, type, [])
    {
    }

    // A record can't be saved without a value for it.
    public bool Required { get; init; }

    // Records compare lists by reference, so compare the options by value.
    public bool Equals(RecordField? other) =>
        other is not null && Key == other.Key && Type == other.Type && Required == other.Required && Options.SequenceEqual(other.Options);

    public override int GetHashCode() => HashCode.Combine(Key, Type, Required, Options.Count);
}

// A kind of link every record of a type needs at least one of, such as an ascent's OF link to a Route.
public sealed record RequiredLink(string RelType, string OtherType);

public sealed record RecordType(string Name, string? Color, string? Icon, IReadOnlyList<RecordField> Fields)
{
    public IReadOnlyList<RequiredLink> RequiredLinks { get; init; } = [];
}

// Values are shown as text: numbers as written, dates as yyyy-MM-dd, booleans as true or false, and a list's values one
// per line.
public sealed record RecordRow(string Id, string Key, string Name, IReadOnlyDictionary<string, string> Values);

public sealed record RecordRows(int Total, IReadOnlyList<RecordRow> Rows);

// How a filter compares a field: Contains for text and lists, Is for choices and booleans (and exact numbers and dates),
// AtLeast and AtMost for the ends of a number or date range.
public enum FilterOp { Contains, Is, AtLeast, AtMost }

public sealed record RecordFilter(string Field, FilterOp Op, string Value);

// Sorts by a field's key, or by Name or Key (the default), which are always there. Blank values come last either way.
public sealed record RecordSort(string Field, bool Descending = false);

public sealed record RecordDetail(
    string Id, string Type, string Key, string Name, IReadOnlyDictionary<string, string> Values, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

// What a form sends: the name, and each field's value as typed, empty for none. A new record also brings its links, at
// least those its type requires.
public sealed record RecordInput(string Name, IReadOnlyDictionary<string, string?> Values)
{
    public IReadOnlyList<NewLink> Links { get; init; } = [];
}

// A link from a new record to one already here.
public sealed record NewLink(string RelType, string TargetId);

public sealed record RecordLink(long Id, string OtherId, string OtherType, string OtherKey, string OtherName);

// The links of one kind on one side of a record, such as every Service that CALLS_AT this Stop. Only the first few come
// back, with the total.
public sealed record RecordLinkGroup(string RelType, bool Outgoing, int Total, IReadOnlyList<RecordLink> Links);

// A kind of link a record can have, from the model's relationships.
public sealed record LinkOption(string RelType, bool Outgoing, string OtherType);

public sealed record RecordOption(string Id, string Name, string Key);

// A relationship the model has, such as Service CALLS_AT Stop.
public sealed record RelationshipDefinition(string FromType, string RelType, string ToType);

// Lists, shows and edits the generic store's data, guided by the model's definitions. See docs/models.md.
public sealed class RecordRepository(IDbContextFactory<LinkbaseDbContext> contextFactory)
{
    public const int LinksShown = 25;

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
                Fields = t.Fields.OrderBy(f => f.Id).Select(f => new { f.Key, f.Type, f.Options, f.Required }).ToList(),
            })
            .ToListAsync(cancellationToken);
        var required = await db.LinkDefinitions.AsNoTracking()
            .Where(l => l.Required)
            .OrderBy(l => l.RelType).ThenBy(l => l.TargetType)
            .Select(l => new { l.SourceType, l.RelType, l.TargetType })
            .ToListAsync(cancellationToken);
        return types.Select(t => new RecordType(t.Name, t.Color, t.Icon,
            t.Fields.Select(f => new RecordField(f.Key, f.Type, f.Options ?? []) { Required = f.Required }).ToList())
        {
            RequiredLinks = required.Where(l => l.SourceType == t.Name).Select(l => new RequiredLink(l.RelType, l.TargetType)).ToList(),
        }).ToList();
    }

    public async Task<RecordType?> TypeAsync(string name, CancellationToken cancellationToken = default) =>
        (await TypesAsync(cancellationToken)).FirstOrDefault(t => t.Name == name);

    // In name order, or only those whose name or any value contains the search text.
    public Task<RecordRows> ListAsync(string type, string? search, int page, int pageSize, CancellationToken cancellationToken = default) =>
        ListAsync(type, search, null, [], page, pageSize, cancellationToken);

    // Sorted by a field and narrowed by filters on fields, as well as the search. Values are JSON, so a field is a lookup in
    // props; a field filtered often can have an expression index (docs/climbing-to-generic-plan.md).
    public async Task<RecordRows> ListAsync(
        string type, string? search, RecordSort? sort, IReadOnlyList<RecordFilter> filters, int page, int pageSize, CancellationToken cancellationToken = default)
    {
        var definition = await TypeAsync(type, cancellationToken) ?? throw new LinkbaseValidationException($"There's no type called {type}.");
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
            where.Add($"search_text ILIKE {Add($"%{EscapeLike(search.Trim())}%", NpgsqlDbType.Text)}");
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

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var total = await db.Database.SqlQueryRaw<int>(countSql, Materialize(parameters)).SingleAsync(cancellationToken);
        var rows = await db.Database
            .SqlQueryRaw<RawRecord>(rowsSql, Materialize(parameters))
            .ToListAsync(cancellationToken);
        return new RecordRows(total, rows.Select(r => new RecordRow(r.Id, r.Key, r.Name, Values(r.Props))).ToList());
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
                var expression = field.Type == FieldType.Number ? $"({column})::numeric" : field.Type is FieldType.Text or FieldType.Choice or FieldType.List ? $"lower({column})" : column;
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
        return node is null ? null : new RecordDetail(node.Id, node.Type, node.Key, node.Name, Values(node.Props), node.CreatedAt, node.UpdatedAt);
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
                .Select(l => new RecordLink(l.Id, l.TargetId, l.Target.Type, l.Target.Key, l.Target.Name))
                .ToListAsync(cancellationToken);
            groups.Add(new RecordLinkGroup(group.RelType, true, group.Total, links));
        }

        foreach (var group in incoming.OrderBy(g => g.RelType))
        {
            var links = await db.NodeLinks.AsNoTracking()
                .Where(l => l.TargetId == id && l.RelType == group.RelType)
                .OrderBy(l => l.Source.Name).ThenBy(l => l.Id)
                .Take(LinksShown)
                .Select(l => new RecordLink(l.Id, l.SourceId, l.Source.Type, l.Source.Key, l.Source.Name))
                .ToListAsync(cancellationToken);
            groups.Add(new RecordLinkGroup(group.RelType, false, group.Total, links));
        }

        return groups;
    }

    // The kinds of link a record of this type can have, either way round.
    public async Task<IReadOnlyList<LinkOption>> LinkOptionsAsync(string type, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var definitions = await db.LinkDefinitions.AsNoTracking()
            .Where(l => l.SourceType == type || l.TargetType == type)
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

    // The key is made from the name when none is given, and can't change afterwards, because the record's id is made from it.
    // The record and its links are saved together, so a record never exists without the links its type requires.
    public async Task<string> CreateAsync(string type, string? key, RecordInput input, CancellationToken cancellationToken = default)
    {
        var definition = await TypeAsync(type, cancellationToken) ?? throw new LinkbaseValidationException($"There's no type called {type}.");
        var name = Name(input);
        var props = Props(definition, input);

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var links = await NewLinksAsync(db, definition, input.Links, cancellationToken);
        if (string.IsNullOrWhiteSpace(key))
        {
            key = await FreeKeyAsync(db, type, Slug(name), cancellationToken);
        }
        else
        {
            key = key.Trim();
            if (await db.Nodes.AnyAsync(n => n.Type == type && n.Key == key, cancellationToken))
            {
                throw new LinkbaseValidationException($"There's already a {type} with the key {key}.");
            }
        }

        var id = $"{type.ToLowerInvariant()}:{key}";
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        db.Nodes.Add(new Node
        {
            Id = id,
            UserId = AppUser.DefaultId,
            Type = type,
            Key = key,
            Name = name,
            Props = props,
        });
        db.NodeLinks.AddRange(links.Select(l => new NodeLink { SourceId = id, RelType = l.RelType, TargetId = l.TargetId }));
        await db.SaveChangesAsync(cancellationToken);
        foreach (var group in links.GroupBy(l => (l.RelType, l.TargetType)))
        {
            await CountAsync(db, type, group.Key.RelType, group.Key.TargetType, group.Count(), cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return key;
    }

    // A new record's links, checked: each goes where the model has a relationship, and every required one is there.
    private static async Task<List<(string RelType, string TargetId, string TargetType)>> NewLinksAsync(
        LinkbaseDbContext db, RecordType type, IReadOnlyList<NewLink> requested, CancellationToken cancellationToken)
    {
        var ids = requested.Select(l => l.TargetId).Distinct().ToList();
        var targets = await db.Nodes.AsNoTracking()
            .Where(n => ids.Contains(n.Id) && n.UserId == AppUser.DefaultId)
            .Select(n => new { n.Id, n.Type })
            .ToDictionaryAsync(n => n.Id, n => n.Type, cancellationToken);
        var defined = await db.LinkDefinitions.AsNoTracking()
            .Where(d => d.SourceType == type.Name)
            .Select(d => new { d.RelType, d.TargetType })
            .ToListAsync(cancellationToken);

        var links = new List<(string RelType, string TargetId, string TargetType)>();
        foreach (var link in requested.Distinct())
        {
            var targetType = targets.GetValueOrDefault(link.TargetId)
                ?? throw new LinkbaseValidationException("The record to link to no longer exists.");
            if (!defined.Any(d => d.RelType == link.RelType && d.TargetType == targetType))
            {
                throw new LinkbaseValidationException($"The model has no {type.Name} {link.RelType} {targetType} relationship.");
            }

            links.Add((link.RelType, link.TargetId, targetType));
        }

        foreach (var needed in type.RequiredLinks.Where(r => !links.Any(l => l.RelType == r.RelType && l.TargetType == r.OtherType)))
        {
            throw new LinkbaseValidationException($"Each {type.Name} needs at least one {type.Name} {needed.RelType} {needed.OtherType} link.");
        }

        return links;
    }

    public async Task UpdateAsync(string id, RecordInput input, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var node = await db.Nodes.SingleOrDefaultAsync(n => n.Id == id && n.UserId == AppUser.DefaultId, cancellationToken)
            ?? throw new LinkbaseValidationException("This record no longer exists.");
        var definition = await TypeAsync(node.Type, cancellationToken) ?? throw new LinkbaseValidationException($"There's no type called {node.Type}.");

        node.Name = Name(input);
        node.Props = Props(definition, input, node.Props);
        await db.SaveChangesAsync(cancellationToken);
    }

    // Like everything else, a record that still has links can't be deleted. Remove its links first. The links its type
    // requires can't be removed on their own, so they go with it.
    public async Task DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var type = await db.Nodes.AsNoTracking().Where(n => n.Id == id && n.UserId == AppUser.DefaultId).Select(n => n.Type).SingleOrDefaultAsync(cancellationToken);
        if (type is null)
        {
            return;
        }

        var required = await db.LinkDefinitions.AsNoTracking()
            .Where(d => d.SourceType == type && d.Required)
            .Select(d => new { d.RelType, d.TargetType })
            .ToListAsync(cancellationToken);
        var own = new List<(string RelType, string TargetType, int Count)>();
        foreach (var definition in required)
        {
            var count = await db.NodeLinks.CountAsync(l => l.SourceId == id && l.RelType == definition.RelType && l.Target.Type == definition.TargetType, cancellationToken);
            own.Add((definition.RelType, definition.TargetType, count));
        }

        var links = await db.NodeLinks.CountAsync(l => l.SourceId == id || l.TargetId == id, cancellationToken) - own.Sum(o => o.Count);
        if (links > 0)
        {
            throw new LinkbaseValidationException($"This record still has {links:N0} link(s). Remove them first.");
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            foreach (var (relType, targetType, _) in own)
            {
                var removed = await db.NodeLinks.Where(l => l.SourceId == id && l.RelType == relType && l.Target.Type == targetType).ExecuteDeleteAsync(cancellationToken);
                await CountAsync(db, type, relType, targetType, -removed, cancellationToken);
            }

            await db.Nodes.Where(n => n.Id == id && n.UserId == AppUser.DefaultId).ExecuteDeleteAsync(cancellationToken);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.ForeignKeyViolation)
        {
            throw new LinkbaseValidationException("This record was linked to something else while deleting it. Remove its links first.");
        }

        await transaction.CommitAsync(cancellationToken);
    }

    // Links only go where the model has a relationship, either way round.
    public async Task AddLinkAsync(string sourceId, string relType, string targetId, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var ends = await db.Nodes.AsNoTracking()
            .Where(n => (n.Id == sourceId || n.Id == targetId) && n.UserId == AppUser.DefaultId)
            .Select(n => new { n.Id, n.Type })
            .ToListAsync(cancellationToken);
        var source = ends.FirstOrDefault(e => e.Id == sourceId) ?? throw new LinkbaseValidationException("One end of the link no longer exists.");
        var target = ends.FirstOrDefault(e => e.Id == targetId) ?? throw new LinkbaseValidationException("One end of the link no longer exists.");

        if (!await db.LinkDefinitions.AnyAsync(l => l.SourceType == source.Type && l.RelType == relType && l.TargetType == target.Type, cancellationToken))
        {
            throw new LinkbaseValidationException($"The model has no {source.Type} {relType} {target.Type} relationship.");
        }

        if (await db.NodeLinks.AnyAsync(l => l.SourceId == sourceId && l.RelType == relType && l.TargetId == targetId, cancellationToken))
        {
            throw new LinkbaseValidationException("Those two are already linked that way.");
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        db.NodeLinks.Add(new NodeLink { SourceId = sourceId, RelType = relType, TargetId = targetId });
        await db.SaveChangesAsync(cancellationToken);
        await CountAsync(db, source.Type, relType, target.Type, 1, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task RemoveLinkAsync(long linkId, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var link = await db.NodeLinks.AsNoTracking()
            .Where(l => l.Id == linkId)
            .Select(l => new { l.SourceId, SourceName = l.Source.Name, SourceType = l.Source.Type, l.RelType, TargetType = l.Target.Type })
            .SingleOrDefaultAsync(cancellationToken);
        if (link is null)
        {
            return;
        }

        // The last of a required kind of link has to stay. Deleting the record takes it too.
        if (await db.LinkDefinitions.AnyAsync(d => d.SourceType == link.SourceType && d.RelType == link.RelType && d.TargetType == link.TargetType && d.Required, cancellationToken)
            && !await db.NodeLinks.AnyAsync(l => l.SourceId == link.SourceId && l.RelType == link.RelType && l.Target.Type == link.TargetType && l.Id != linkId, cancellationToken))
        {
            throw new LinkbaseValidationException(
                $"Each {link.SourceType} needs at least one {link.SourceType} {link.RelType} {link.TargetType} link, and this is {link.SourceName}'s only one. Add another first.");
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var removed = await db.NodeLinks.Where(l => l.Id == linkId).ExecuteDeleteAsync(cancellationToken);
        await CountAsync(db, link.SourceType, link.RelType, link.TargetType, -removed, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    // Keeps the relationship's count, which the home page and the graph model show, in step with its links.
    private static Task CountAsync(LinkbaseDbContext db, string sourceType, string relType, string targetType, int change, CancellationToken cancellationToken) =>
        db.LinkDefinitions
            .Where(d => d.SourceType == sourceType && d.RelType == relType && d.TargetType == targetType)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.Links, d => d.Links + change), cancellationToken);

    private static string Name(RecordInput input) =>
        string.IsNullOrWhiteSpace(input.Name) ? throw new LinkbaseValidationException("A record needs a name.") : input.Name.Trim();

    // Values are checked against their field's type and stored typed, as the loader does. Values for keys the type doesn't
    // define are kept as they were.
    private static string Props(RecordType type, RecordInput input, string? existing = null)
    {
        var props = (existing is null ? null : JsonNode.Parse(existing) as JsonObject) ?? [];
        foreach (var field in type.Fields)
        {
            var text = input.Values.GetValueOrDefault(field.Key)?.Trim();
            if (field.Type == FieldType.List && FieldValues.SplitList(text ?? "", '\n', '\r').Count == 0)
            {
                text = null;
            }

            if (string.IsNullOrEmpty(text))
            {
                props.Remove(field.Key);
                if (field.Required)
                {
                    throw new LinkbaseValidationException($"{Title(field.Key)} is required.");
                }

                continue;
            }

            props[field.Key] = field.Type switch
            {
                FieldType.Number => decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var number)
                    ? JsonValue.Create(number)
                    : throw new LinkbaseValidationException($"{Title(field.Key)} must be a number, such as 12.5."),
                FieldType.Date => DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                    ? JsonValue.Create(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
                    : throw new LinkbaseValidationException($"{Title(field.Key)} must be a date, such as 2026-11-06."),
                FieldType.Boolean => bool.TryParse(text, out var flag)
                    ? JsonValue.Create(flag)
                    : throw new LinkbaseValidationException($"{Title(field.Key)} must be true or false."),
                FieldType.Choice => FieldValues.MatchChoice(text, field.Options) is { } option
                    ? JsonValue.Create(option)
                    : throw new LinkbaseValidationException($"{Title(field.Key)} must be one of {FieldValues.Listed(field.Options)}."),
                FieldType.List => new JsonArray(FieldValues.SplitList(text, '\n', '\r').Select(v => (JsonNode?)JsonValue.Create(v)).ToArray()),
                _ => JsonValue.Create(text),
            };
        }

        return props.ToJsonString();
    }

    private static Dictionary<string, string> Values(string props)
    {
        using var document = JsonDocument.Parse(props);
        return document.RootElement.EnumerateObject().ToDictionary(
            p => p.Name,
            p => p.Value.ValueKind switch
            {
                JsonValueKind.String => p.Value.GetString()!,
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                JsonValueKind.Array => string.Join('\n', p.Value.EnumerateArray().Select(v => v.ValueKind == JsonValueKind.String ? v.GetString() : v.GetRawText())),
                _ => p.Value.GetRawText(),
            });
    }

    private static async Task<string> FreeKeyAsync(LinkbaseDbContext db, string type, string stem, CancellationToken cancellationToken)
    {
        var taken = await db.Nodes.AsNoTracking()
            .Where(n => n.Type == type && (n.Key == stem || n.Key.StartsWith(stem + "-")))
            .Select(n => n.Key)
            .ToListAsync(cancellationToken);
        var key = stem;
        for (var i = 2; taken.Contains(key); i++)
        {
            key = $"{stem}-{i}";
        }

        return key;
    }

    // Lower case letters, digits and hyphens, such as zoe-adams for Zoë Adams.
    private static string Slug(string name)
    {
        var plain = name.Normalize(System.Text.NormalizationForm.FormD)
            .Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark);
        var slug = new string(plain.Select(c => char.IsAsciiLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-').ToArray());
        slug = string.Join('-', slug.Split('-', StringSplitOptions.RemoveEmptyEntries));
        return slug.Length == 0 ? "record" : slug[..Math.Min(slug.Length, 60)].TrimEnd('-');
    }

    public static string Title(string key) => char.ToUpperInvariant(key[0]) + key[1..].Replace('_', ' ');

    private static string EscapeLike(string text) =>
        text.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_");
}
