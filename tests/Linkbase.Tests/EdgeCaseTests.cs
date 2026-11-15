using Linkbase.Core.Extraction;
using System.Text.Json.Nodes;
using Linkbase.Core;
using Linkbase.Core.Models;
using Linkbase.Data;
using Linkbase.Domains.Climbing.Data;
using Linkbase.Core.Graph;
using Linkbase.Domains.Climbing.Import;
using Microsoft.EntityFrameworkCore;

namespace Linkbase.Tests;

public class GraphPatternEdgeCaseTests
{
    private static readonly GraphSchema Schema = new(
        [],
        [
            new GraphPropertyInfo("Trip", "abroad", GraphValueType.Boolean),
            new GraphPropertyInfo("Route", "length_m", GraphValueType.Number),
        ]);

    private static string Sql(string label, string key, FilterOperator op, string value) =>
        GraphPatternSql.Build(new GraphPattern([new PatternNode(label, [new PatternFilter(key, op, value)])], []), Schema, 10, 0).Sql;

    [Fact]
    public void An_unfinished_pattern_is_refused()
    {
        Assert.Throws<LinkbaseValidationException>(() => GraphPatternSql.Build(new GraphPattern([], []), Schema, 10, 0));
        Assert.Throws<LinkbaseValidationException>(() => GraphPatternSql.Build(new GraphPattern([new PatternNode("Crag", []), new PatternNode("Route", [])], []), Schema, 10, 0));
    }

    [Theory]
    [InlineData(FilterOperator.Equals, " = ")]
    [InlineData(FilterOperator.LessOrEqual, " <= ")]
    public void Numbers_compare_as_numbers(FilterOperator op, string expected) =>
        Assert.Contains(expected, Sql("Route", "length_m", op, "20"));

    [Fact]
    public void Booleans_compare_as_text_from_the_view() =>
        Assert.Contains("= ", Sql("Trip", "abroad", FilterOperator.Equals, " TRUE "));
}

public class ExtractionEdgeCaseTests
{
    [Fact]
    public void A_paragraph_with_no_spaces_is_still_cut_to_length()
    {
        var chunks = TextChunker.Split(new string('x', 2500), 1000);
        Assert.True(chunks.Count >= 3);
        Assert.All(chunks, c => Assert.True(c.Length <= 1000));
        Assert.Equal(2500, chunks.Sum(c => c.Length));
    }

    [Fact]
    public void A_boulder_tick_is_bouldering()
    {
        const string header = "Date,Route,Rating,Notes,URL,Pitches,Location,\"Avg Stars\",\"Your Stars\",Style,\"Lead Style\",\"Route Type\",\"Your Rating\",Length,\"Rating Code\"\n";
        var row = MountainProjectParser.Parse(new StringReader(header + "2023-01-01,R,V3,,https://www.mountainproject.com/route/1/r,1,\"Utah > Crag\",1,-1,Send,,Boulder,,,1\n")).Single();
        Assert.Equal(Discipline.Boulder, row.Discipline);
    }
}

[Trait("Category", "Database")]
public class ContextEdgeCaseTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    [Fact]
    public void Saving_synchronously_stamps_timestamps_too()
    {
        using var db = fixture.CreateContext();
        var batch = new ImportBatch { UserId = AppUser.DefaultId, Source = DataSource.Csv, Status = ImportStatus.Staged };
        db.ImportBatches.Add(batch);

        db.SaveChanges();

        Assert.NotEqual(default, batch.CreatedAt);
        Assert.Equal(batch.CreatedAt, batch.UpdatedAt);
    }

    [Fact]
    public async Task Nothing_typed_matches_nothing() =>
        Assert.Empty(await new GraphRepository(fixture.ContextFactory(), Linkbase.Domains.RegisteredDomains.Registry).MatchPropertiesAsync(null, " "));
}
