using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Linkbase.Core.Security;
using Linkbase.Data;

namespace Linkbase.Core.Models;

// A model file: the types, fields and relationships of a model, and the CSV files its data comes from. See docs/models.md.
public sealed partial class ModelFile
{
    public required string Name { get; init; }

    // Where the CSV files are, relative to the model file. The model file's own folder if left out.
    public string? Folder { get; init; }

    public IReadOnlyList<ModelType> Types { get; init; } = [];
    public IReadOnlyList<ModelRelationship> Relationships { get; init; } = [];

    // Whether the model keeps an audit trail, and whether every change needs a reason. Both are on if left out. See
    // docs/history/audit-history-plan.md.
    public ModelHistory? History { get; init; }

    // Which of the three kinds of visibility part the model uses, what it calls them, and where people's grants come from.
    // Security is off if left out. See docs/history/security-design.md.
    public ModelSecurity? Security { get; init; }

    [JsonIgnore] public bool RecordHistory => History?.Record ?? true;
    [JsonIgnore] public bool RequireReason => RecordHistory && (History?.RequireReason ?? true);
    [JsonIgnore] public string[] SourceKinds => History?.SourceKinds?.Select(k => k.Trim()).ToArray() ?? [];

    // The folder the CSV files were found in, once read.
    [JsonIgnore] public string DataFolder { get; private set; } = "";

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static ModelFile Read(string path, DomainRegistry registry)
    {
        if (!File.Exists(path))
        {
            throw new LinkbaseValidationException($"There's no model file at {path}.");
        }

        ModelFile? model;
        try
        {
            model = JsonSerializer.Deserialize<ModelFile>(File.ReadAllText(path), Options);
        }
        catch (JsonException ex)
        {
            throw new LinkbaseValidationException($"{Path.GetFileName(path)} isn't a valid model file: {ex.Message}");
        }

        if (model is null)
        {
            throw new LinkbaseValidationException($"{Path.GetFileName(path)} is empty.");
        }

        model.DataFolder = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, model.Folder ?? "."));
        model.Validate(registry);
        return model;
    }

    // Names end up in the database and in Explore, and keys and relationship names in SQL text, so they're kept simple.
    private void Validate(DomainRegistry registry)
    {
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(Name))
        {
            problems.Add("The model needs a name.");
        }

        if (Types.Count == 0)
        {
            problems.Add("The model needs at least one type.");
        }

        if (History is { Record: false, RequireReason: true })
        {
            problems.Add("History can't both be off and require a reason. Leave requireReason out, or set it to false.");
        }

        Security?.Validate(problems);

        if (SourceKinds.Any(k => k.Length is 0 or > 60))
        {
            problems.Add("Each source kind needs a name of 1 to 60 characters.");
        }

        foreach (var duplicate in SourceKinds.GroupBy(k => k, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
        {
            problems.Add($"Source kind '{duplicate.Key}' is listed more than once.");
        }

        foreach (var type in Types)
        {
            if (!TypeName().IsMatch(type.Name ?? ""))
            {
                problems.Add($"Type '{type.Name}' needs a name of letters and digits, starting with a capital, such as Stop.");
            }

            ValidateFields($"Type '{type.Name}'", type.Fields, problems);
            ValidateTemplate(type, problems);
            ValidateOwner(registry, $"Type '{type.Name}'", type.Owner, problems);
            ValidateLabel($"Type '{type.Name}'", type.Label, type.Source?.Label, problems);
            foreach (var field in type.Fields)
            {
                ValidateOwner(registry, $"Field '{field.Key}' of type '{type.Name}'", field.Owner, problems);
                ValidateLabel($"Field '{field.Key}' of type '{type.Name}'", field.Label, null, problems);
            }
        }

        foreach (var duplicate in Types.GroupBy(t => t.Name).Where(g => g.Count() > 1))
        {
            problems.Add($"Type '{duplicate.Key}' appears more than once.");
        }

        var names = Types.Select(t => t.Name).ToHashSet();
        foreach (var relationship in Relationships)
        {
            var what = $"Relationship {relationship.From} {relationship.Type} {relationship.To}";
            if (!RelType().IsMatch(relationship.Type ?? ""))
            {
                problems.Add($"{what} needs a name in capitals, digits and underscores, such as CALLS_AT.");
            }

            foreach (var end in new[] { relationship.From, relationship.To }.Where(end => !names.Contains(end)))
            {
                problems.Add($"{what} refers to type '{end}', which isn't in the model.");
            }

            ValidateFields(what, relationship.Fields, problems);
            ValidateLabel(what, relationship.Label, relationship.Source?.Label, problems);
            foreach (var field in relationship.Fields.Where(f => f.Owner is not null))
            {
                problems.Add($"{what}'s field '{field.Key}' has an owner, which only a type's fields can have.");
            }

            foreach (var field in relationship.Fields.Where(f => f.Label is not null))
            {
                problems.Add($"{what}'s field '{field.Key}' has a label, which only a type's fields can have.");
            }

            foreach (var field in relationship.Fields.Where(f => f.Group is not null))
            {
                problems.Add($"{what}'s field '{field.Key}' has a group, which only a type's fields can have.");
            }

            if (relationship.Max is < 1)
            {
                problems.Add($"{what} has a max of {relationship.Max}, which needs to be 1 or more.");
            }

            // People can't link to a module's derived records, so they couldn't add a record that needs such a link.
            if (relationship.Required && Types.FirstOrDefault(t => t.Name == relationship.To)?.Owner is { } owner
                && Types.FirstOrDefault(t => t.Name == relationship.From)?.Owner is null)
            {
                problems.Add($"{what} is required, but {relationship.To} records are derived by {owner}, so people couldn't add {relationship.From} records.");
            }
        }

        foreach (var type in Types.Where(t => t.Hierarchy is not null))
        {
            ValidateHierarchy(type, problems);
        }

        if (problems.Count > 0)
        {
            throw new LinkbaseValidationException(string.Join(Environment.NewLine, problems));
        }
    }

    // A label, or a CSV column for one, only means something if the model has security, and has to use that model's own values.
    private void ValidateLabel(string what, ModelLabel? label, ModelLabelColumns? columns, List<string> problems)
    {
        if (label is null && columns is null)
        {
            return;
        }

        if (Security is not { Enabled: true })
        {
            problems.Add($"{what} has a label, but the model has no security section.");
            return;
        }

        Security.ValidateLabel(what, label, columns, problems);
    }

    // The levels have to cover the `by` field's options exactly, with somewhere for the tree to start, and the parent
    // relationship has to be one the type has with itself, which can't be required because the top level has none.
    private void ValidateHierarchy(ModelType type, List<string> problems)
    {
        var hierarchy = type.Hierarchy!;
        var what = $"Type '{type.Name}''s hierarchy";
        var by = type.Fields.FirstOrDefault(f => f.Key == hierarchy.By);
        if (by is null || by.Type != FieldType.Choice)
        {
            problems.Add($"{what} is by '{hierarchy.By}', which needs to be a choice field of the type.");
        }
        else if (!by.Required)
        {
            problems.Add($"{what} is by '{hierarchy.By}', which needs to be required, so every record has a level.");
        }

        var via = Relationships.FirstOrDefault(r => r.From == type.Name && r.To == type.Name && r.Type == hierarchy.Via);
        if (via is null)
        {
            problems.Add($"{what} goes via '{hierarchy.Via}', but the model has no relationship {type.Name} {hierarchy.Via} {type.Name}.");
        }
        else if (via.Required)
        {
            problems.Add($"{what} goes via {type.Name} {hierarchy.Via} {type.Name}, which can't be required, because the top level has no parent.");
        }

        if (hierarchy.Levels.Count == 0)
        {
            problems.Add($"{what} needs levels.");
            return;
        }

        var kinds = hierarchy.Levels.Select(l => l.Kind).ToList();
        foreach (var repeated in kinds.GroupBy(k => k).Where(g => g.Count() > 1))
        {
            problems.Add($"{what} has the level '{repeated.Key}' more than once.");
        }

        if (by?.Type == FieldType.Choice)
        {
            var options = (by.Options ?? []).Select(o => o.Trim()).ToList();
            foreach (var kind in kinds.Where(k => !options.Contains(k)))
            {
                problems.Add($"{what} has the level '{kind}', which isn't an option of '{hierarchy.By}'.");
            }

            foreach (var option in options.Where(o => !kinds.Contains(o)))
            {
                problems.Add($"{what} has no level for '{hierarchy.By}' option '{option}'.");
            }
        }

        if (!hierarchy.Levels.Any(l => l.Root))
        {
            problems.Add($"{what} needs a level with \"root\": true, or nothing could be at the top.");
        }

        foreach (var level in hierarchy.Levels)
        {
            var under = level.Under ?? [];
            if (level.Root && under.Count > 0)
            {
                problems.Add($"{what}'s level '{level.Kind}' is a root, so it can't be under anything.");
            }
            else if (!level.Root && under.Count == 0)
            {
                problems.Add($"{what}'s level '{level.Kind}' needs \"under\": the levels it may be in, or \"root\": true.");
            }

            foreach (var other in under.Where(u => !kinds.Contains(u)))
            {
                problems.Add($"{what}'s level '{level.Kind}' is under '{other}', which isn't a level.");
            }
        }

        // A tree needs a way down from the top: every level has to be reachable from a root.
        var reached = hierarchy.Levels.Where(l => l.Root).Select(l => l.Kind).ToHashSet();
        for (var grew = true; grew;)
        {
            grew = false;
            foreach (var level in hierarchy.Levels.Where(l => !reached.Contains(l.Kind) && (l.Under ?? []).Any(reached.Contains)))
            {
                reached.Add(level.Kind);
                grew = true;
            }
        }

        foreach (var level in hierarchy.Levels.Where(l => !reached.Contains(l.Kind)))
        {
            problems.Add($"{what}'s level '{level.Kind}' can't be reached from a root, so no record could be at it.");
        }
    }

    private const int MaxDescription = 300;
    private const int MaxGroup = 40;

    // A name template is text with {field} in it, each naming a text-like field of the type that has one value.
    private static void ValidateTemplate(ModelType type, List<string> problems)
    {
        if (type.Template is not { } template)
        {
            return;
        }

        var what = $"Type '{type.Name}''s name template";
        var tokens = NameTemplates.Tokens(template);
        if (tokens.Count == 0)
        {
            problems.Add($"{what} has no {{field}} in it. Use something like \"{{first}} {{last}}\".");
        }

        foreach (var token in tokens.Distinct())
        {
            var field = type.Fields.FirstOrDefault(f => f.Key == token);
            if (field is null)
            {
                problems.Add($"{what} uses {{{token}}}, which isn't a field of the type.");
            }
            else if (field.Label is not null)
            {
                problems.Add($"{what} uses {{{token}}}, which has a label, so the name would show its value to people who can't see it.");
            }
            else if (field.IsMany || field.Owner is not null)
            {
                problems.Add($"{what} uses {{{token}}}, which needs to be a field with one value that people fill in.");
            }
        }

        if (!NameTemplates.IsBalanced(template))
        {
            problems.Add($"{what} has a stray brace. Write each field as {{key}}.");
        }
    }

    // An owner is one of this app's domain modules, by its key, such as Climbing.
    private static void ValidateOwner(DomainRegistry registry, string what, string? owner, List<string> problems)
    {
        if (owner is not null && registry.All.All(d => d.Key != owner))
        {
            problems.Add($"{what} is owned by '{owner}', which isn't a domain. The domains are: {string.Join(", ", registry.Keys)}.");
        }
    }

    private static void ValidateFields(string owner, IReadOnlyList<ModelField> fields, List<string> problems)
    {
        foreach (var field in fields)
        {
            if (!FieldKey().IsMatch(field.Key ?? "") || field.Key == "name")
            {
                problems.Add($"{owner} has a field '{field.Key}', which needs to be lower case letters, digits and underscores, and not 'name'.");
            }

            if (field.Description is { } description && (description.Length > MaxDescription || description.Any(char.IsControl)))
            {
                problems.Add($"{owner}'s field '{field.Key}' has a description with control characters or over {MaxDescription} characters.");
            }

            if (field.Group is { } group && (group.Trim().Length is 0 or > MaxGroup || group.Any(char.IsControl)))
            {
                problems.Add($"{owner}'s field '{field.Key}' has a group of 1 to {MaxGroup} characters of text.");
            }

            if (field.Multiple && field.Type == FieldType.Boolean)
            {
                problems.Add($"{owner}'s field '{field.Key}' is a boolean, which can't have several values.");
            }

            var options = field.Options ?? [];
            if (field.Type == FieldType.Choice)
            {
                if (options.Count == 0 || options.Count > FieldValues.MaxOptions)
                {
                    problems.Add($"{owner}'s field '{field.Key}' is a choice, so it needs between 1 and {FieldValues.MaxOptions} options.");
                }

                if (options.Any(string.IsNullOrWhiteSpace))
                {
                    problems.Add($"{owner}'s field '{field.Key}' has an empty option.");
                }

                if (options.GroupBy(o => o.Trim(), StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1) is { } repeated)
                {
                    problems.Add($"{owner}'s field '{field.Key}' has the option '{repeated.Key}' more than once.");
                }
            }
            else if (options.Count > 0)
            {
                problems.Add($"{owner}'s field '{field.Key}' has options, which only a choice field can have.");
            }
        }

        foreach (var duplicate in fields.GroupBy(f => f.Key).Where(g => g.Count() > 1))
        {
            problems.Add($"{owner} has the field '{duplicate.Key}' more than once.");
        }
    }

    [GeneratedRegex("^[A-Z][A-Za-z0-9]{0,62}$")]
    internal static partial Regex TypeName();

    [GeneratedRegex("^[A-Z][A-Z0-9_]{0,62}$")]
    internal static partial Regex RelType();

    [GeneratedRegex("^[a-z][a-z0-9_]{0,62}$")]
    internal static partial Regex FieldKey();
}

public sealed class ModelType
{
    public required string Name { get; init; }
    public string? Color { get; init; }

    // A Material icon's name, such as Train.
    public string? Icon { get; init; }

    // Where the type's records come from. Leave it out for a type that starts empty, such as one a domain module fills.
    public ModelTypeSource? Source { get; init; }
    public IReadOnlyList<ModelField> Fields { get; init; } = [];

    // Who may see this type's records, unless a column of its CSV file says otherwise. Needs the model's security section.
    public ModelLabel? Label { get; init; }

    // The domain module that derives this type's records, such as Climbing for trips. Only it writes them.
    public string? Owner { get; init; }

    // Leave it out for a type whose records aren't a tree.
    public ModelHierarchy? Hierarchy { get; init; }

    // Makes each record's name from its fields, such as "{name_first} {name_last}". A field with no value drops out, and so
    // does the space around it. People then don't type a name. The source's `name` can say the same, if it has braces.
    public string? NameTemplate { get; init; }

    [JsonIgnore]
    public string? Template => NameTemplate ?? (Source?.Name is { } name && name.Contains('{', StringComparison.Ordinal) ? name : null);
}

// How a type's records form a tree, such as areas in regions in countries. See docs/models.md.
public sealed class ModelHierarchy
{
    // The choice field saying which level a record is on.
    public required string By { get; init; }

    // The relationship from a record to its parent, from the type to itself.
    public required string Via { get; init; }

    public IReadOnlyList<ModelHierarchyLevel> Levels { get; init; } = [];

    public HierarchyRules ToRules(string typeName) =>
        new(typeName, By, Via, Levels.Select(l => new HierarchyLevelRule(l.Kind, l.Root, l.Under ?? [])).ToList());
}

public sealed class ModelHierarchyLevel
{
    // One of the options of the `by` field.
    public required string Kind { get; init; }

    // A record at this level has no parent.
    public bool Root { get; init; }

    // The levels a record at this level may be in, by kind. Not for a root.
    public IReadOnlyList<string>? Under { get; init; }
}

public sealed class ModelTypeSource
{
    public required string File { get; init; }

    // The column that identifies a row. Relationships refer to rows by it.
    public required string Key { get; init; }

    // The column to show as the node's name. The key, if left out or empty.
    public string? Name { get; init; }

    // The columns holding each record's label. A blank cell takes the type's label. Needs the model's security section.
    public ModelLabelColumns? Label { get; init; }
}

public sealed class ModelField
{
    public required string Key { get; init; }
    public FieldType Type { get; init; } = FieldType.Text;

    // The column the value comes from. A column with the same name as the key, if left out.
    public string? Column { get; init; }

    // A choice field's values, in the order to offer them. Only for "type": "choice".
    public IReadOnlyList<string>? Options { get; init; }

    // Every row needs a value. On a relationship's field, every link does.
    public bool Required { get; init; }

    // The domain module that keeps this field's value, such as Climbing. Only a type's fields can have one.
    public string? Owner { get; init; }

    // Shown to people beside the field. Keep it short.
    public string? Description { get; init; }

    // Fields with the same group are shown together under its heading. Display only; they stay separate fields. A type's
    // fields only.
    public string? Group { get; init; }

    // Several values of the type, such as a list of dates. A list is the same as this on a text field.
    public bool Multiple { get; init; }

    // Who may see this field's value. A type's fields only. Anything left out asks for nothing, so the record's own label rules.
    public ModelLabel? Label { get; init; }

    [JsonIgnore] public bool IsMany => Multiple || Type == FieldType.List;

    // The type of one of the values: a list's are text.
    [JsonIgnore] public FieldType ElementType => Type == FieldType.List ? FieldType.Text : Type;
}

public sealed class ModelRelationship
{
    public required string From { get; init; }
    public required string Type { get; init; }
    public required string To { get; init; }
    // Leave it out for a relationship that starts with no links.
    public ModelRelationshipSource? Source { get; init; }
    public IReadOnlyList<ModelField> Fields { get; init; } = [];

    // Who may see this relationship's links, unless a column of its CSV file says otherwise. Needs the model's security section.
    public ModelLabel? Label { get; init; }

    // Every record of the From type needs at least one of these links, such as an ascent's route.
    public bool Required { get; init; }

    // The most links of this kind one record of the From type may have, such as 1 for a person's team. No limit if left out.
    public int? Max { get; init; }
}

public sealed class ModelRelationshipSource
{
    public required string File { get; init; }

    // The columns holding the keys of the two ends.
    public required string FromColumn { get; init; }
    public required string ToColumn { get; init; }

    // The columns holding each link's label. A blank cell takes the relationship's label.
    public ModelLabelColumns? Label { get; init; }
}

// The model file's history settings: record is whether changes are logged, requireReason whether a change can't be saved
// without a reason. Turning record off makes requireReason mean nothing.
public sealed class ModelHistory
{
    public bool? Record { get; init; }
    public bool? RequireReason { get; init; }

    // The kinds of source a change can name, such as Internet or the applications of the organisation. Just Internet if
    // left out.
    public IReadOnlyList<string>? SourceKinds { get; init; }
}
