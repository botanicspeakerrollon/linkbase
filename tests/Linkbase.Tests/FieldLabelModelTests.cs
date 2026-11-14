using Linkbase.Data;
using Linkbase.Core.Models;

namespace Linkbase.Tests;

// What a model file may say about labelled fields.
public sealed class FieldLabelModelTests
{
    private static string Problem(string fieldLabel, string template = "", string relationshipField = "")
    {
        var path = Path.Combine(Path.GetTempPath(), $"label-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, $$"""
            {
              "name": "T",
              "types": [
                { "name": "A", "nameTemplate": "{{template}}", "source": { "file": "a.csv", "key": "id" },
                  "fields": [ { "key": "secret", "label": {{fieldLabel}} }, { "key": "plain" } ] },
                { "name": "B", "source": { "file": "b.csv", "key": "id", "name": "name" } }
              ],
              "relationships": [ { "from": "A", "type": "TO", "to": "B", "source": { "file": "c.csv", "fromColumn": "a", "toColumn": "b" }{{relationshipField}} } ],
              "security": { "level": { "name": "Sensitivity", "values": [ "public", "secret" ] } }
            }
            """);
        try
        {
            return Assert.Throws<LinkbaseValidationException>(() => ModelFile.Read(path)).Message;
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void A_field_label_must_use_a_level_the_model_lists()
    {
        Assert.Contains("nonsense", Problem("""{ "level": "nonsense" }""", "{plain}"));
    }

    [Fact]
    public void A_name_cannot_be_made_from_a_labelled_field()
    {
        Assert.Contains("secret", Problem("""{ "level": "secret" }""", "{secret} {plain}"));
    }

    [Fact]
    public void A_relationships_field_cannot_have_a_label()
    {
        var message = Problem("""{ "level": "secret" }""", "{plain}", """, "fields": [ { "key": "role", "label": { "level": "secret" } } ]""");
        Assert.Contains("only a type's fields can have", message);
    }
}
