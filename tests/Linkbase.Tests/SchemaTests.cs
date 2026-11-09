using Linkbase.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Linkbase.Tests;

[Trait("Category", "Database")]
[Collection(PostgresCollection.Name)]
public class SchemaTests(PostgresFixture fixture)
{
    private const long DefaultUserId = 1;

    private static string Unique(string name) => $"{name} {Guid.NewGuid():N}";

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
        Assert.Contains(indexes, i => i.Contains("ON public.node ") && i.Contains("(name "));
        Assert.Contains(indexes, i => i.Contains("ON public.node ") && i.Contains("(search_text "));
    }

    [Fact]
    public async Task Trigram_similarity_finds_misspelled_names()
    {
        var store = await ClimbingStore.CreateAsync(fixture);
        var name = $"Stanage {Guid.NewGuid():N}"[..20];
        await ClimbingStore.AddAsync(store, "Crag", name);

        await using var db = store.CreateDbContext();
        var found = await db.Nodes.AsNoTracking()
            .Where(n => EF.Functions.TrigramsSimilarity(n.Name, name.Replace("Stanage", "Stannage")) > 0.3)
            .Select(n => n.Name)
            .ToListAsync();

        Assert.Contains(name, found);
    }

    [Fact]
    public async Task External_ids_are_unique_per_source_and_node_type()
    {
        var externalId = Unique("123");
        await using var db = fixture.CreateContext();
        db.ExternalRefs.AddRange(
            new ExternalRef { NodeId = "crag:1", NodeType = "Crag", Source = DataSource.Ukc, ExternalId = externalId },
            new ExternalRef { NodeId = "route:1", NodeType = "Route", Source = DataSource.Ukc, ExternalId = externalId });
        await db.SaveChangesAsync();

        await using var duplicate = fixture.CreateContext();
        duplicate.ExternalRefs.Add(new ExternalRef { NodeId = "crag:2", NodeType = "Crag", Source = DataSource.Ukc, ExternalId = externalId });
        await AssertViolatesAsync("ix_external_ref_source_node_type_external_id", () => duplicate.SaveChangesAsync());
    }

    [Fact]
    public async Task Timestamps_match_on_insert_and_diverge_on_edit()
    {
        var store = await ClimbingStore.CreateAsync(fixture);
        var id = await ClimbingStore.AddAsync(store, "Crag", Unique("Crag"));

        await using var db = store.CreateDbContext();
        var crag = await db.Nodes.SingleAsync(n => n.Id == id);
        Assert.NotEqual(default, crag.CreatedAt);
        Assert.Equal(crag.CreatedAt, crag.UpdatedAt);

        crag.Name = Unique("Edited");
        await db.SaveChangesAsync();
        Assert.True(crag.UpdatedAt > crag.CreatedAt);
    }

    [Fact]
    public async Task Deleting_a_record_that_is_still_linked_is_refused()
    {
        var store = await ClimbingStore.CreateAsync(fixture);
        var place = await ClimbingStore.AddPlaceAsync(store);

        await using var delete = store.CreateDbContext();
        await AssertViolatesAsync(
            "fk_node_link_node_source_id",
            () => delete.Nodes.Where(n => n.Id == place.CragId).ExecuteDeleteAsync());
    }
}
