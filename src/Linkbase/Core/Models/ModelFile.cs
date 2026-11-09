using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Linkbase.Data;
using Linkbase.Domains;

namespace Linkbase.Core.Models;

// A model file: the types, fields and relationships of a model, and the CSV files its data comes from. See docs/models.md.
public sealed partial class ModelFile
{
    public required string Name { get; init; }

    // Where the CSV files are, relative to the model file. The model file's own folder if left out.
    public string? Folder { get; init; }

    public IReadOnlyList<ModelType> Types { get; init; } = [];
    public IReadOnlyList<ModelRelationship> Relationships { get; init; } = [];

    // The folder the CSV files were found in, once read.
    [JsonIgnore] public string DataFolder { get; private set; } = "";

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static ModelFile Read(string path)
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
        model.Validate();
        return model;
    }

    // Names end up in the database and in Explore, and keys and relationship names in SQL text, so they're kept simple.
    private void Validate()
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

        foreach (var type in Types)
        {
            if (!TypeName().IsMatch(type.Name ?? ""))
            {
                problems.Add($"Type '{type.Name}' needs a name of letters and digits, starting with a capital, such as Stop.");
            }

            ValidateFields($"Type '{type.Name}'", type.Fields, problems);
            ValidateOwner($"Type '{type.Name}'", type.Owner, problems);
            foreach (var field in type.Fields)
            {
                ValidateOwner($"Field '{field.Key}' of type '{type.Name}'", field.Owner, problems);
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
            foreach (var field in relationship.Fields.Where(f => f.Owner is not null))
            {
                problems.Add($"{what}'s field '{field.Key}' has an owner, which only a type's fields can have.");
            }

            // People can't link to a module's derived records, so they couldn't add a record that needs such a link.
            if (relationship.Required && Types.FirstOrDefault(t => t.Name == relationship.To)?.Owner is { } owner
                && Types.FirstOrDefault(t => t.Name == relationship.From)?.Owner is null)
            {
                problems.Add($"{what} is required, but {relationship.To} records are derived by {owner}, so people couldn't add {relationship.From} records.");
            }
        }

        if (problems.Count > 0)
        {
            throw new LinkbaseValidationException(string.Join(Environment.NewLine, problems));
        }
    }

    // An owner is one of this app's domain modules, by its key, such as Climbing.
    private static void ValidateOwner(string what, string? owner, List<string> problems)
    {
        if (owner is not null && RegisteredDomains.All.All(d => d.Key != owner))
        {
            problems.Add($"{what} is owned by '{owner}', which isn't a domain. The domains are: {string.Join(", ", RegisteredDomains.All.Select(d => d.Key))}.");
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

    // The domain module that derives this type's records, such as Climbing for trips. Only it writes them.
    public string? Owner { get; init; }
}

public sealed class ModelTypeSource
{
    public required string File { get; init; }

    // The column that identifies a row. Relationships refer to rows by it.
    public required string Key { get; init; }

    // The column to show as the node's name. The key, if left out or empty.
    public string? Name { get; init; }
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
}

public sealed class ModelRelationship
{
    public required string From { get; init; }
    public required string Type { get; init; }
    public required string To { get; init; }
    // Leave it out for a relationship that starts with no links.
    public ModelRelationshipSource? Source { get; init; }
    public IReadOnlyList<ModelField> Fields { get; init; } = [];

    // Every record of the From type needs at least one of these links, such as an ascent's route.
    public bool Required { get; init; }
}

public sealed class ModelRelationshipSource
{
    public required string File { get; init; }

    // The columns holding the keys of the two ends.
    public required string FromColumn { get; init; }
    public required string ToColumn { get; init; }
}
