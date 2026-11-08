using Linkbase.Data;
using Linkbase.Domains.Climbing.Data;
using Linkbase.Core.Graph;
using Microsoft.EntityFrameworkCore;

namespace Linkbase.Tests;

public class ModelTests
{
    private static LinkbaseDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<LinkbaseDbContext>().UseNpgsql("Host=unused").Options);

    [Fact]
    public void Migrations_are_in_sync_with_the_model()
    {
        using var db = CreateContext();
        Assert.False(db.Database.HasPendingModelChanges());
    }

    [Fact]
    public void Tables_have_the_documented_names()
    {
        using var db = CreateContext();
        var tables = db.Model.GetEntityTypes().Where(e => e.GetViewName() is null).Select(e => e.GetTableName()!).Order().ToArray();
        Assert.Equal(
            ["app_user", "area", "ascent", "ascent_partner", "crag", "data_protection_key", "external_ref", "field_definition", "graph_scene", "import_batch", "link_definition", "node", "node_link", "node_type", "partner", "review_item", "route", "trip"],
            tables);
    }

    [Fact]
    public void No_foreign_key_cascades()
    {
        using var db = CreateContext();
        var foreignKeys = db.Model.GetEntityTypes().SelectMany(e => e.GetForeignKeys());
        Assert.All(foreignKeys, fk => Assert.Equal(DeleteBehavior.Restrict, fk.DeleteBehavior));
    }

    [Fact]
    public void Enum_columns_are_text()
    {
        using var db = CreateContext();
        var entity = db.Model.FindEntityType(typeof(Route))!;
        Assert.Equal("text", entity.FindProperty(nameof(Route.GradeSystem))!.GetColumnType());
        Assert.Equal("text", entity.FindProperty(nameof(Route.Discipline))!.GetColumnType());
    }
}
