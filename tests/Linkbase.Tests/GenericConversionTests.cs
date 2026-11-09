using System.Text.Json.Nodes;
using Linkbase.Core;
using Linkbase.Core.Models;
using Linkbase.Data;
using Linkbase.Domains.Climbing.Data;
using Linkbase.Domains.Climbing.Import;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Linkbase.Tests;

// Moving climbing into the generic store: models/climbing/model.json, and copying the typed tables into it. Everything here
// is made up. See docs/climbing-to-generic-plan.md.
[Trait("Category", "Database")]
[Collection(PostgresCollection.Name)]
public sealed class GenericConversionTests(PostgresFixture fixture)
{
    private static readonly string ModelPath = RepoPaths.Resolve("models/climbing/model.json");

    // Two countries, a region, two crags (one with aliases), three routes, four ascents on two trips, one of them inferred,
    // a partner on two ascents, and ids from UKC and Mountain Project.
    internal static async Task<(ModelFactory Source, ModelFactory Target)> TypedAsync(PostgresFixture fixture)
    {
        var source = new ModelFactory(await fixture.CreateModelDatabaseAsync("SELECT 1"));
        var target = new ModelFactory(await fixture.CreateModelDatabaseAsync("SELECT 1"));
        await using var db = source.CreateDbContext();

        var england = new Area { UserId = AppUser.DefaultId, Name = "Wessex", Kind = AreaKind.Country };
        var france = new Area { UserId = AppUser.DefaultId, Name = "Gaule", Kind = AreaKind.Country };
        var peak = new Area { UserId = AppUser.DefaultId, Name = "The Downs", Kind = AreaKind.Region, Parent = england };
        var tor = new Crag { UserId = AppUser.DefaultId, Name = "Hollow Tor", Area = peak, RockType = "granite", Lat = 51.5, Lon = -1.25, Aliases = ["Hollow", "The Tor"], Notes = "Quiet" };
        var cliffs = new Crag { UserId = AppUser.DefaultId, Name = "Les Falaises", Area = france };
        var crack = new Route { UserId = AppUser.DefaultId, Name = "Crack of Dawn", Crag = tor, Grade = "E1 5b", GradeSystem = GradeSystem.UkTrad, Discipline = Discipline.Trad, LengthM = 30, Pitches = 1 };
        var slab = new Route { UserId = AppUser.DefaultId, Name = "Slab Happy", Crag = tor, Grade = "VS 4c", GradeSystem = GradeSystem.UkTrad, Discipline = Discipline.Trad };
        var blue = new Route { UserId = AppUser.DefaultId, Name = "La Bleue", Crag = cliffs, Grade = "6a+", GradeSystem = GradeSystem.French, Discipline = Discipline.Sport };
        var partner = new Partner { UserId = AppUser.DefaultId, Name = "Sam Example" };
        var spring = new Trip { UserId = AppUser.DefaultId, Key = "Wessex 2020-05-02", Name = "Wessex, May 2020", StartDate = new DateOnly(2020, 5, 2), EndDate = new DateOnly(2020, 5, 3), SortDate = new DateOnly(2020, 5, 2), Home = true };
        var summer = new Trip { UserId = AppUser.DefaultId, Key = "Gaule 2021-07", Name = "Gaule, July 2021", SortDate = new DateOnly(2021, 7, 1), Abroad = true, Approximate = true, DatesPartlyUnknown = true };
        var first = new Ascent { UserId = AppUser.DefaultId, Route = crack, Date = new DateOnly(2020, 5, 2), DatePrecision = DatePrecision.Day, Style = AscentStyle.Onsight, StyleRaw = "Onsight", MyRating = 3, Source = DataSource.Ukc, Trip = spring, Notes = "Cold hands" };
        var second = new Ascent { UserId = AppUser.DefaultId, Route = slab, Date = new DateOnly(2020, 5, 3), DatePrecision = DatePrecision.Day, Style = AscentStyle.Redpoint, Source = DataSource.Ukc, Trip = spring, TripInferred = true };
        var third = new Ascent { UserId = AppUser.DefaultId, Route = blue, Date = new DateOnly(2021, 7, 1), DatePrecision = DatePrecision.Month, Style = AscentStyle.TopRope, Source = DataSource.MountainProject, Trip = summer };
        var fourth = new Ascent { UserId = AppUser.DefaultId, Route = crack, Date = new DateOnly(2022, 1, 1), DatePrecision = DatePrecision.Year, Style = AscentStyle.Lead, Source = DataSource.Manual };
        db.Ascents.AddRange(first, second, third, fourth);
        db.AscentPartners.AddRange(new AscentPartner { Ascent = first, Partner = partner }, new AscentPartner { Ascent = third, Partner = partner });
        await db.SaveChangesAsync();
        db.ExternalRefs.AddRange(
            new ExternalRef { EntityType = ExternalEntityType.Crag, EntityId = tor.Id, Source = DataSource.Ukc, ExternalId = "1234", Url = "https://example.test/crag/1234" },
            new ExternalRef { EntityType = ExternalEntityType.Ascent, EntityId = third.Id, Source = DataSource.MountainProject, ExternalId = "tick-9" });
        await db.SaveChangesAsync();
        return (source, target);
    }

    [Fact]
    public void The_climbing_model_file_reads()
    {
        var model = ModelFile.Read(ModelPath);

        Assert.Equal(["Area", "Crag", "Route", "Ascent", "Partner", "Trip"], model.Types.Select(t => t.Name));
        Assert.Equal("Climbing", model.Types.Single(t => t.Name == "Trip").Owner);
        Assert.Equal("Climbing", model.Types.Single(t => t.Name == "Route").Fields.Single(f => f.Key == "grade_rank").Owner);
        Assert.All(model.Relationships.Where(r => r.Type is "AT" or "OF"), r => Assert.True(r.Required));
    }

    [Fact]
    public async Task Converts_the_typed_tables_and_verifies_them()
    {
        var (source, target) = await TypedAsync(fixture);

        var summary = await new GenericConverter(source, target).ConvertAsync(ModelPath, TextWriter.Null);

        Assert.Equal(3, summary.Nodes["Area"]);
        Assert.Equal(2, summary.Nodes["Crag"]);
        Assert.Equal(3, summary.Nodes["Route"]);
        Assert.Equal(4, summary.Nodes["Ascent"]);
        Assert.Equal(1, summary.Nodes["Partner"]);
        Assert.Equal(2, summary.Nodes["Trip"]);
        Assert.Equal(2, summary.Refs);
        Assert.Empty(await new GenericVerifier(source, target).VerifyAsync());
    }

    [Fact]
    public async Task Keeps_ids_fields_links_and_refs()
    {
        var (source, target) = await TypedAsync(fixture);
        await new GenericConverter(source, target).ConvertAsync(ModelPath, TextWriter.Null);

        await using var typed = source.CreateDbContext();
        var cragId = await typed.Crags.Where(c => c.Name == "Hollow Tor").Select(c => c.Id).SingleAsync();
        var thirdId = await typed.Ascents.Where(a => a.Style == AscentStyle.TopRope).Select(a => a.Id).SingleAsync();
        var secondId = await typed.Ascents.Where(a => a.TripInferred).Select(a => a.Id).SingleAsync();
        var routeId = await typed.Routes.Where(r => r.Name == "Crack of Dawn").Select(r => r.Id).SingleAsync();

        await using var db = target.CreateDbContext();
        var crag = await db.Nodes.SingleAsync(n => n.Id == $"crag:{cragId}");
        var props = JsonNode.Parse(crag.Props)!.AsObject();
        Assert.Equal("Hollow Tor", crag.Name);
        Assert.Equal("granite", (string?)props["rock_type"]);
        Assert.Equal(["Hollow", "The Tor"], props["aliases"]!.AsArray().Select(a => (string?)a));
        Assert.Equal(51.5, (double?)props["lat"]);

        // An ascent is named for its route, and a partial date keeps its precision.
        var ascent = await db.Nodes.SingleAsync(n => n.Id == $"ascent:{thirdId}");
        Assert.Equal("La Bleue", ascent.Name);
        var ascentProps = JsonNode.Parse(ascent.Props)!.AsObject();
        Assert.Equal("2021-07-01", (string?)ascentProps["date"]);
        Assert.Equal("month", (string?)ascentProps["date_precision"]);
        Assert.Equal("top_rope", (string?)ascentProps["style"]);
        Assert.Equal("mountain_project", (string?)ascentProps["source"]);
        Assert.Null(ascentProps["notes"]);

        // The module's grade rank is kept for each route, from the same ranking the typed tables use.
        var route = JsonNode.Parse((await db.Nodes.SingleAsync(n => n.Id == $"route:{routeId}")).Props)!.AsObject();
        Assert.Equal(GradeRanking.Rank(GradeSystem.UkTrad, "E1 5b"), (double?)route["grade_rank"]);

        var onTrip = await db.NodeLinks.Where(l => l.RelType == "ON_TRIP").ToListAsync();
        Assert.Equal([$"ascent:{secondId}"], onTrip.Where(l => (bool?)JsonNode.Parse(l.Props)!["inferred"] == true).Select(l => l.SourceId));
        Assert.Equal(3, onTrip.Count);
        Assert.True(await db.NodeLinks.AnyAsync(l => l.SourceId == $"ascent:{thirdId}" && l.RelType == "WITH"));
        Assert.Equal(2, await db.NodeLinks.CountAsync(l => l.RelType == "VISITED"));
        Assert.Equal(3, await db.NodeLinks.CountAsync(l => l.RelType == "IN"));

        var refs = await db.ExternalRefs.Where(r => r.NodeId != null).OrderBy(r => r.ExternalId).ToListAsync();
        Assert.Equal([$"crag:{cragId}", $"ascent:{thirdId}"], refs.Select(r => r.NodeId));
        Assert.Equal("https://example.test/crag/1234", refs[0].Url);

        // The relationship counts are what the home page shows.
        Assert.Equal(4, (await db.LinkDefinitions.SingleAsync(d => d.SourceType == "Ascent" && d.RelType == "OF")).Links);
    }

    [Fact]
    public async Task Converting_again_gives_the_same_store()
    {
        var (source, target) = await TypedAsync(fixture);
        var converter = new GenericConverter(source, target);
        await converter.ConvertAsync(ModelPath, TextWriter.Null);
        await converter.ConvertAsync(ModelPath, TextWriter.Null);

        Assert.Empty(await new GenericVerifier(source, target).VerifyAsync());
        await using var db = target.CreateDbContext();
        Assert.Equal(2, await db.ExternalRefs.CountAsync(r => r.NodeId != null));
    }

    [Fact]
    public async Task The_verifier_reports_what_differs()
    {
        var (source, target) = await TypedAsync(fixture);
        await new GenericConverter(source, target).ConvertAsync(ModelPath, TextWriter.Null);
        await using (var db = target.CreateDbContext())
        {
            await db.NodeLinks.Where(l => l.RelType == "WITH").ExecuteDeleteAsync();
            await db.Nodes.Where(n => n.Type == "Partner").ExecuteDeleteAsync();
        }

        await using (var db = target.CreateDbContext())
        {
            await db.NodeLinks.Where(l => l.RelType == "VISITED").Take(1).ExecuteDeleteAsync();
            // Straight to Npgsql, because EF would read the braces as parameters.
            var connection = (Npgsql.NpgsqlConnection)db.Database.GetDbConnection();
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE node SET props = jsonb_set(props, '{style}', '\"flash\"') WHERE type = 'Ascent' AND props->>'style' = 'lead'";
            await command.ExecuteNonQueryAsync();
        }

        var problems = await new GenericVerifier(source, target).VerifyAsync();

        Assert.Contains("Partner: 0 records, expected 1", problems);
        Assert.Contains(problems, p => p.Contains("style is \"flash\", expected \"lead\""));
        Assert.Contains(problems, p => p.Contains("VISITED") && p.EndsWith("missing"));
        Assert.Contains(problems, p => p.Contains("WITH") && p.EndsWith("missing"));
    }

    [Fact]
    public async Task The_command_refuses_the_same_database_unless_asked()
    {
        var connection = await fixture.CreateModelDatabaseAsync("SELECT 1");
        var catalog = new ModelCatalog(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Models:climbing:ConnectionString"] = "Same",
            ["Models:climbing:Domains:0"] = "Climbing",
            ["Models:generic:ConnectionString"] = "Same",
            ["ConnectionStrings:Same"] = connection,
        }).Build());
        var output = new StringWriter();

        var refused = await new GenericCommands(catalog, output).RunAsync(["climbing", "to-generic", "generic"]);
        var allowed = await new GenericCommands(catalog, output).RunAsync(["climbing", "to-generic", "generic", "--same-database"]);

        Assert.Equal(1, refused);
        Assert.Contains("add --same-database", output.ToString());
        Assert.Equal(0, allowed);
        Assert.Contains("matches the typed tables", output.ToString());
        Assert.True(GenericCommands.IsCommand(["climbing", "verify-generic", "generic"]));
        Assert.False(GenericCommands.IsCommand(["climbing", "to-generic"]));
    }
}
