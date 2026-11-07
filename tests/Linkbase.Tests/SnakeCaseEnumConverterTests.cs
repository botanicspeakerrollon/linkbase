using Linkbase.Data;
using Linkbase.Domains.Climbing.Data;
using Linkbase.Core.Graph;

namespace Linkbase.Tests;

public class SnakeCaseEnumConverterTests
{
    [Fact]
    public void Stores_the_names_the_schema_documents()
    {
        Assert.Equal("uk_trad", SnakeCaseEnumConverter<GradeSystem>.ToStore(GradeSystem.UkTrad));
        Assert.Equal("uk_tech", SnakeCaseEnumConverter<GradeSystem>.ToStore(GradeSystem.UkTech));
        Assert.Equal("v", SnakeCaseEnumConverter<GradeSystem>.ToStore(GradeSystem.V));
        Assert.Equal("top_rope", SnakeCaseEnumConverter<AscentStyle>.ToStore(AscentStyle.TopRope));
        Assert.Equal("dnf", SnakeCaseEnumConverter<AscentStyle>.ToStore(AscentStyle.Dnf));
        Assert.Equal("mountain_project", SnakeCaseEnumConverter<DataSource>.ToStore(DataSource.MountainProject));
        Assert.Equal("neo4j", SnakeCaseEnumConverter<DataSource>.ToStore(DataSource.Neo4j));
        Assert.Equal("web_import", SnakeCaseEnumConverter<DataSource>.ToStore(DataSource.WebImport));
    }

    [Fact]
    public void Round_trips_every_value()
    {
        foreach (var value in Enum.GetValues<AscentStyle>())
        {
            var stored = SnakeCaseEnumConverter<AscentStyle>.ToStore(value);
            Assert.Equal(value, SnakeCaseEnumConverter<AscentStyle>.FromStore(stored));
        }
    }

    [Fact]
    public void Rejects_unknown_values_with_a_clear_message()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => SnakeCaseEnumConverter<Discipline>.FromStore("ice"));
        Assert.Contains("ice", ex.Message);
    }
}
