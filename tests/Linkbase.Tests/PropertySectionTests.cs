using Linkbase.Core.Explore;
using Linkbase.Core.Graph;

namespace Linkbase.Tests;

public class PropertySectionTests
{
    private static readonly GraphSchema Schema = new([],
    [
        new GraphPropertyInfo("Person", "name", GraphValueType.Text),
        new GraphPropertyInfo("Person", "name_first", GraphValueType.Text) { Group = "Name" },
        new GraphPropertyInfo("Person", "title", GraphValueType.Text) { Group = "Role" },
        new GraphPropertyInfo("Person", "name_last", GraphValueType.Text) { Group = "Name" },
        new GraphPropertyInfo("Person", "email", GraphValueType.Text),
    ]);

    [Fact]
    public void Fields_of_a_group_sit_together_where_the_first_one_is()
    {
        var values = new[] { new GraphProperty("email", "a@example.test"), new GraphProperty("name_last", "Lind"), new GraphProperty("title", "Lead"), new GraphProperty("name_first", "Ava") };

        var sections = PropertySection.Of("Person", values, Schema);

        Assert.Equal(["Name", "Role", null], sections.Select(s => s.Group));
        Assert.Equal(["name_first", "name_last"], sections[0].Properties.Select(p => p.Key));
    }

    [Fact]
    public void Values_the_model_does_not_define_follow_ungrouped()
    {
        var sections = PropertySection.Of("Person", [new GraphProperty("extra", "x"), new GraphProperty("title", "Lead")], Schema);

        Assert.Equal(["Role", null], sections.Select(s => s.Group));
        Assert.Equal("extra", sections[1].Properties[0].Key);
    }
}
