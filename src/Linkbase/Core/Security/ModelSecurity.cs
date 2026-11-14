using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Linkbase.Core.Security;

// The model file's security section: which of the three kinds of part the model uses, what it calls them, and where people's
// grants come from. Nothing here is specific to any one organisation; the names and values are the model's own. See
// docs/security-design.md.
public sealed partial class ModelSecurity
{
    public const string LevelKey = "level";
    public const string AnyOfKey = "anyOf";
    public const string AllOfKey = "allOf";

    // An ordered list: a person's value has to be at least the item's. Its position in the list is its rank, lowest first.
    public SecurityPart? Level { get; init; }

    // A set: a person needs any one of an item's values.
    public SecurityPart? AnyOf { get; init; }

    // A set: a person needs every one of an item's values.
    public SecurityPart? AllOf { get; init; }

    // Grants written in the model file, by user id, for parts whose source is config.
    public IReadOnlyDictionary<string, UserGrants>? Users { get; init; }

    [JsonIgnore] public bool Enabled => Level is not null || AnyOf is not null || AllOf is not null;
    [JsonIgnore] public bool HasLevel => Level is not null;
    [JsonIgnore] public bool HasAnyOf => AnyOf is not null;
    [JsonIgnore] public bool HasAllOf => AllOf is not null;

    public SecurityPart? Part(string key) => key switch
    {
        LevelKey => Level,
        AnyOfKey => AnyOf,
        AllOfKey => AllOf,
        _ => null,
    };

    // The parts the model uses, with their keys.
    [JsonIgnore]
    public IEnumerable<(string Key, SecurityPart Part)> Parts
    {
        get
        {
            if (Level is not null) yield return (LevelKey, Level);
            if (AnyOf is not null) yield return (AnyOfKey, AnyOf);
            if (AllOf is not null) yield return (AllOfKey, AllOf);
        }
    }

    // The rank of a level's value, or -1 if it isn't one. Values are matched exactly.
    public int LevelRank(string? value)
    {
        var values = Level?.Values;
        if (values is null || value is null)
        {
            return -1;
        }

        for (var i = 0; i < values.Count; i++)
        {
            if (string.Equals(values[i], value, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    // Values travel in Postgres array literals and session settings, and "*" stands for "no restriction" in the any-of column,
    // so they're kept plain: no braces, commas, quotes, backslashes, asterisks or control characters.
    public static bool IsValidValue(string? value) =>
        value is { Length: > 0 and <= MaxValue } && value == value.Trim() && ValuePattern().IsMatch(value);

    public const int MaxValue = 60;
    private const int MaxName = 40;
    private const int MaxLevels = 50;
    private const int MaxUserId = 200;
    public const int DefaultCacheSeconds = 300;

    [GeneratedRegex("""^[^\p{C}{},"\\*]+$""")]
    private static partial Regex ValuePattern();

    public void Validate(List<string> problems)
    {
        if (!Enabled)
        {
            problems.Add("The security section needs at least one of level, anyOf and allOf, or leave security out.");
        }

        var names = new List<string>();
        foreach (var (key, part) in Parts)
        {
            var what = $"Security part '{key}'";
            if (string.IsNullOrWhiteSpace(part.Name) || part.Name != part.Name.Trim() || part.Name.Length > MaxName || part.Name.Any(char.IsControl))
            {
                problems.Add($"{what} needs a name of 1 to {MaxName} characters, such as Department.");
            }
            else
            {
                names.Add(part.Name);
            }

            ValidateValues(what, part, key == LevelKey, problems);
            ValidateSource(what, part.Source, problems);
        }

        foreach (var duplicate in names.GroupBy(n => n, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
        {
            problems.Add($"Two security parts are both called '{duplicate.Key}'.");
        }

        ValidateUsers(problems);
    }

    private static void ValidateValues(string what, SecurityPart part, bool isLevel, List<string> problems)
    {
        var values = part.Values;
        if (values is null)
        {
            if (isLevel)
            {
                problems.Add($"{what} needs values, listed lowest first, such as [\"public\", \"private\"].");
            }

            return;
        }

        if (isLevel && values.Count == 0)
        {
            problems.Add($"{what} needs values, listed lowest first, such as [\"public\", \"private\"].");
        }

        if (values.Count > MaxLevels && isLevel)
        {
            problems.Add($"{what} has {values.Count} values, and the most a level can have is {MaxLevels}.");
        }

        foreach (var value in values.Where(v => !IsValidValue(v)))
        {
            problems.Add($"{what} has the value '{value}', which needs 1 to {MaxValue} characters with no braces, commas, quotes, backslashes, asterisks or surrounding spaces.");
        }

        foreach (var duplicate in values.Where(IsValidValue).GroupBy(v => v, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
        {
            problems.Add($"{what} lists '{duplicate.Key}' more than once (values that differ only by case are too easily confused).");
        }
    }

    private static void ValidateSource(string what, GrantSource? source, List<string> problems)
    {
        if (source is null)
        {
            return;
        }

        if (source.Type == GrantSourceType.Config)
        {
            if (source.Url is not null || source.Path is not null || source.CacheSeconds is not null || source.TokenSetting is not null)
            {
                problems.Add($"{what} reads its grants from the model file, so its source can't have a url, path, cacheSeconds or tokenSetting.");
            }

            return;
        }

        if (string.IsNullOrWhiteSpace(source.Url))
        {
            problems.Add($"{what} reads its grants from an API, so its source needs a url, such as https://directory.example/users/{{user}}.");
        }
        else if (!Uri.TryCreate(source.Url.Replace("{user}", "user", StringComparison.Ordinal), UriKind.Absolute, out var uri)
                 || !(uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)))
        {
            problems.Add($"{what}'s url '{source.Url}' needs to be a full https address (http is only allowed for localhost).");
        }

        if (string.IsNullOrWhiteSpace(source.Path))
        {
            problems.Add($"{what} reads its grants from an API, so its source needs a path: the property in the JSON that holds them, such as departments or data.departments.");
        }

        if (source.CacheSeconds is < 0)
        {
            problems.Add($"{what}'s cacheSeconds is {source.CacheSeconds}, which needs to be 0 (no caching) or more.");
        }
    }

    private void ValidateUsers(List<string> problems)
    {
        var configParts = Parts.Where(p => (p.Part.Source?.Type ?? GrantSourceType.Config) == GrantSourceType.Config).Select(p => p.Key).ToHashSet();
        if (Users is null)
        {
            return;
        }

        if (configParts.Count == 0)
        {
            problems.Add("The security section has users, but every part reads its grants from an API, so they would never be used.");
        }

        foreach (var (id, grants) in Users)
        {
            var who = $"Security user '{id}'";
            if (string.IsNullOrWhiteSpace(id) || id.Length > MaxUserId)
            {
                problems.Add($"A security user needs an id of 1 to {MaxUserId} characters.");
            }

            if (grants.Level is not null)
            {
                if (!HasLevel)
                {
                    problems.Add($"{who} has a level, but the model has no level part.");
                }
                else if (LevelRank(grants.Level) < 0)
                {
                    problems.Add($"{who} has the level '{grants.Level}', which isn't one of {string.Join(", ", Level!.Values ?? [])}.");
                }
            }

            ValidateGrantValues(who, "anyOf", grants.AnyOf, HasAnyOf, problems);
            ValidateGrantValues(who, "allOf", grants.AllOf, HasAllOf, problems);
        }
    }

    private static void ValidateGrantValues(string who, string key, IReadOnlyList<string>? values, bool partUsed, List<string> problems)
    {
        if (values is null)
        {
            return;
        }

        if (!partUsed)
        {
            problems.Add($"{who} has {key} values, but the model has no {key} part.");
            return;
        }

        foreach (var value in values.Where(v => !IsValidValue(v)))
        {
            problems.Add($"{who} has the {key} value '{value}', which isn't a valid value.");
        }
    }
}

// One part of a model's security: what people call it, and how it behaves for that model.
public sealed class SecurityPart
{
    // What the app shows people, such as Classification, Department or Project.
    public required string Name { get; init; }

    // A level's ordered values, lowest first. For the other two kinds, optionally the values a label may use; a part that
    // leaves them out accepts any value.
    public IReadOnlyList<string>? Values { get; init; }

    // Where people's grants for this part come from. The model file's users if left out.
    public GrantSource? Source { get; init; }
}

public enum GrantSourceType
{
    Config,
    Api,
}

// Where one part's grants come from: the model file, or a directory the app doesn't own, fetched over HTTP. The directory is
// asked "what does this user have?" with {user} replaced in the url, answers in JSON, and the answer is kept for cacheSeconds.
public sealed class GrantSource
{
    public GrantSourceType Type { get; init; } = GrantSourceType.Config;

    // For an API: the address, with {user} for the user's id, such as https://directory.example/users/{user}.
    public string? Url { get; init; }

    // For an API: the property in the JSON holding the grants, written with dots to go inwards, such as data.departments. It
    // holds a string, or for a set an array of strings.
    public string? Path { get; init; }

    // For an API: how long an answer is kept, in seconds. 0 keeps nothing. 300 if left out.
    public int? CacheSeconds { get; init; }

    // For an API: the name of the app setting that holds a bearer token to send, such as Security:DirectoryToken. The token
    // itself never goes in the model file.
    public string? TokenSetting { get; init; }

    [JsonIgnore] public TimeSpan CacheFor => TimeSpan.FromSeconds(CacheSeconds ?? ModelSecurity.DefaultCacheSeconds);
}

// What one user has, in a model file's users table. Anything left out is nothing.
public sealed class UserGrants
{
    public string? Level { get; init; }
    public IReadOnlyList<string>? AnyOf { get; init; }
    public IReadOnlyList<string>? AllOf { get; init; }
}
