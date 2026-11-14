namespace Linkbase.Core.Security;

// How a label is kept in the node and node_link columns, and how the model file's JSON for the security section is kept in
// model_setting. The columns are label_level (a rank), label_any (a set, where {*} means no restriction, so that a plain overlap
// test in SQL works) and label_all (a set).
public static class LabelCodec
{
    // Stands for "no restriction" in label_any, so that an item nobody restricted overlaps every viewer's any-of set, which
    // always includes it.
    public const string Everyone = "*";

    public static Label ToLabel(int level, IEnumerable<string> any, IEnumerable<string> all) =>
        new(level, any.Where(v => v != Everyone), all);

    public static string[] AnyColumn(Label label) => label.AnyOf.Count == 0 ? [Everyone] : [.. label.AnyOf.Order(StringComparer.Ordinal)];

    public static string[] AllColumn(Label label) => [.. label.AllOf.Order(StringComparer.Ordinal)];

    // The model's security section as it's kept in model_setting.security.
    private static readonly System.Text.Json.JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(System.Text.Json.JsonNamingPolicy.CamelCase) },
    };

    public static string? ToJson(ModelSecurity? security) => security is { Enabled: true } ? System.Text.Json.JsonSerializer.Serialize(security, Json) : null;

    public static ModelSecurity? FromJson(string? json) => json is null ? null : System.Text.Json.JsonSerializer.Deserialize<ModelSecurity>(json, Json);
}
