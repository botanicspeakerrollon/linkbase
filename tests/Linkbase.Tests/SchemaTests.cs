using Linkbase.Data;
using Linkbase.Domains.Climbing.Data;
using Linkbase.Core.Graph;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Linkbase.Tests;

[Trait("Category", "Database")]
[Collection(PostgresCollection.Name)]
public class SchemaTests(PostgresFixture fixture)
{
    private const long DefaultUserId = 1;

    private static string Unique(string name) => $"{name} {Guid.NewGuid():N}";

    private static Crag NewCrag(string? name = null) => new()
    {
        UserId = DefaultUserId,
        Name = name ?? Unique("Crag"),
        Area = new Area { UserId = DefaultUserId, Name = Unique("Area"), Kind = AreaKind.Country },
    };

    // SaveChanges wraps the Postgres error in a DbUpdateException; ExecuteDelete throws it directly.
    private static async Task AssertViolatesAsync(string constraint, Func<Task> action)
    {
        var exception = await Record.ExceptionAsync(action);
        var postgresException = exception as PostgresException ?? exception?.InnerException as PostgresException;
        Assert.NotNull(postgresException);
        Assert.Equal(constraint, postgresException.ConstraintName);
    }

    [Fact]
    public async Task Migration_seeds_the_single_user()
    {
        await using var db = fixture.CreateContext();
        var user = await db.Users.AsNoTracking().SingleAsync(u => u.Id == DefaultUserId);
        Assert.False(string.IsNullOrWhiteSpace(user.DisplayName));
    }

    [Fact]
    public async Task Trigram_extension_and_indexes_exist()
    {
        await using var db = fixture.CreateContext();

        var extensions = await db.Database
            .SqlQueryRaw<string>("SELECT extname AS \"Value\" FROM pg_extension").ToListAsync();
        Assert.Contains("pg_trgm", extensions);

        var indexes = await db.Database
            .SqlQueryRaw<string>("SELECT indexdef AS \"Value\" FROM pg_indexes WHERE indexdef LIKE '%gin_trgm_ops%'")
            .ToListAsync();
        Assert.Contains(indexes, i => i.Contains("ON public.crag "));
        Assert.Contains(indexes, i => i.Contains("ON public.route "));
    }

    [Fact]
    public async Task Trigram_similarity_finds_misspelled_names()
    {
        var name = $"Stanage {Guid.NewGuid():N}"[..20];
        await using var db = fixture.CreateContext();
        db.Crags.Add(NewCrag(name));
        await db.SaveChangesAsync();

        var misspelled = name.Replace("Stanage", "Stannage");
        var found = await db.Crags.AsNoTracking()
            .Where(c => EF.Functions.TrigramsSimilarity(c.Name, misspelled) > 0.3)
            .Select(c => c.Name)
            .ToListAsync();

        Assert.Contains(name, found);
    }

    [Fact]
    public async Task Enums_are_stored_as_snake_case_strings()
    {
        await using var db = fixture.CreateContext();
        var route = new Route
        {
            UserId = DefaultUserId,
            Name = Unique("Route"),
            Crag = NewCrag(),
            Grade = "HVS 5a",
            GradeSystem = GradeSystem.UkTrad,
            Discipline = Discipline.Trad,
        };
        db.Routes.Add(route);
        await db.SaveChangesAsync();

        var stored = await db.Database
            .SqlQuery<string>($"SELECT grade_system AS \"Value\" FROM route WHERE id = {route.Id}")
            .SingleAsync();
        Assert.Equal("uk_trad", stored);
    }

    [Fact]
    public async Task Duplicate_root_areas_are_rejected()
    {
        var name = Unique("Scotland");
        await using var db = fixture.CreateContext();
        db.Areas.Add(new Area { UserId = DefaultUserId, Name = name, Kind = AreaKind.Country });
        await db.SaveChangesAsync();

        await using var second = fixture.CreateContext();
        second.Areas.Add(new Area { UserId = DefaultUserId, Name = name, Kind = AreaKind.Country });
        await AssertViolatesAsync("ix_area_parent_id_name", () => second.SaveChangesAsync());
    }

    [Fact]
    public async Task External_ids_are_unique_per_source_and_entity_type()
    {
        var externalId = Unique("123");
        await using var db = fixture.CreateContext();
        db.ExternalRefs.AddRange(
            new ExternalRef { EntityType = ExternalEntityType.Crag, EntityId = 1, Source = DataSource.Ukc, ExternalId = externalId },
            new ExternalRef { EntityType = ExternalEntityType.Route, EntityId = 1, Source = DataSource.Ukc, ExternalId = externalId });
        await db.SaveChangesAsync();

        await using var duplicate = fixture.CreateContext();
        duplicate.ExternalRefs.Add(
            new ExternalRef { EntityType = ExternalEntityType.Crag, EntityId = 2, Source = DataSource.Ukc, ExternalId = externalId });
        await AssertViolatesAsync("ix_external_ref_source_entity_type_external_id", () => duplicate.SaveChangesAsync());
    }

    [Fact]
    public async Task An_external_ref_points_at_a_row_or_a_node_but_not_both()
    {
        await using var db = fixture.CreateContext();
        db.ExternalRefs.Add(new ExternalRef { EntityType = ExternalEntityType.Crag, EntityId = 1, NodeId = "crag:1", NodeType = "Crag", Source = DataSource.Ukc, ExternalId = Unique("both") });
        await AssertViolatesAsync("ck_external_ref_target", () => db.SaveChangesAsync());

        await using var neither = fixture.CreateContext();
        neither.ExternalRefs.Add(new ExternalRef { Source = DataSource.Ukc, ExternalId = Unique("neither") });
        await AssertViolatesAsync("ck_external_ref_target", () => neither.SaveChangesAsync());
    }

    [Fact]
    public async Task Aliases_round_trip_through_a_text_array()
    {
        var crag = NewCrag();
        crag.Aliases = ["Stanage", "The Edge"];
        await using (var db = fixture.CreateContext())
        {
            db.Crags.Add(crag);
            await db.SaveChangesAsync();
        }

        await using var read = fixture.CreateContext();
        var aliases = await read.Crags.AsNoTracking().Where(c => c.Id == crag.Id).Select(c => c.Aliases).SingleAsync();
        Assert.Equal(["Stanage", "The Edge"], aliases);
    }

    [Fact]
    public async Task Timestamps_match_on_insert_and_diverge_on_edit()
    {
        var crag = NewCrag();
        await using var db = fixture.CreateContext();
        db.Crags.Add(crag);
        await db.SaveChangesAsync();
        Assert.NotEqual(default, crag.CreatedAt);
        Assert.Equal(crag.CreatedAt, crag.UpdatedAt);

        crag.Notes = "Edited";
        await db.SaveChangesAsync();
        Assert.True(crag.UpdatedAt > crag.CreatedAt);
    }

    [Fact]
    public async Task Ratings_above_three_stars_are_rejected()
    {
        await using var db = fixture.CreateContext();
        db.Ascents.Add(new Ascent
        {
            UserId = DefaultUserId,
            Route = new Route { UserId = DefaultUserId, Name = Unique("Route"), Crag = NewCrag() },
            Style = AscentStyle.Lead,
            Source = DataSource.Manual,
            MyRating = 4,
        });
        await AssertViolatesAsync("ck_ascent_my_rating", () => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Deleting_a_crag_with_routes_is_refused()
    {
        var route = new Route { UserId = DefaultUserId, Name = Unique("Route"), Crag = NewCrag() };
        await using (var db = fixture.CreateContext())
        {
            db.Routes.Add(route);
            await db.SaveChangesAsync();
        }

        await using var delete = fixture.CreateContext();
        await AssertViolatesAsync(
            "fk_route_crag_crag_id",
            () => delete.Crags.Where(c => c.Id == route.CragId).ExecuteDeleteAsync());
    }
}
