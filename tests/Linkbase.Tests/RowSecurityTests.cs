using Linkbase.Core;
using Linkbase.Core.Models;
using Linkbase.Core.Security;
using Linkbase.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Linkbase.Tests;

// Postgres row-level security: the policies, the viewer reaching the database, and the views. Each test loads the consultancy
// model, whose projects carry labels (see models/consultancy/projects.csv). See docs/security-design.md.
[Trait("Category", "Database")]
public sealed class RowSecurityTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static readonly string Consultancy = RepoPaths.Resolve("models/consultancy/model.json");

    private sealed class Resolver(ModelSecurity directorySecurity, string? user) : IViewerResolver
    {
        private readonly ViewerDirectory _directory = ViewerDirectory.Create(directorySecurity);
        public async ValueTask<Viewer> ResolveAsync(ModelSecurity security, CancellationToken cancellationToken = default) =>
            await _directory.ResolveAsync(user, cancellationToken);
    }

    private sealed class Factory(string connectionString, params Microsoft.EntityFrameworkCore.Diagnostics.IInterceptor[] interceptors) : IDbContextFactory<LinkbaseDbContext>
    {
        public LinkbaseDbContext CreateDbContext() =>
            new(new DbContextOptionsBuilder<LinkbaseDbContext>().UseNpgsql(connectionString).AddInterceptors(interceptors).Options);
    }

    private sealed record Setup(string ConnectionString, ModelSecurity Security)
    {
        // The factory for what a person's circuit would use.
        public Factory For(string? user) =>
            new(ConnectionString, new ViewerConnectionInterceptor(new RowSecurityState(TimeProvider.System), new Resolver(Security, user)));

        public LinkbaseDbContext Owner() =>
            new(new DbContextOptionsBuilder<LinkbaseDbContext>().UseNpgsql(ConnectionString).Options);
    }

    private async Task<Setup> ConsultancyAsync(bool enable = true)
    {
        var connectionString = await fixture.CreateModelDatabaseAsync("SELECT 1");
        var factory = new Factory(connectionString);
        await TestModels.LoadAsync(factory, Consultancy, TextWriter.Null);
        var security = await new LabelRepository(factory).SettingsAsync() ?? throw new InvalidOperationException("The consultancy model has security.");
        var setup = new Setup(connectionString, security);
        if (enable)
        {
            await using var db = setup.Owner();
            await RowSecurity.EnableAsync(db);
        }

        return setup;
    }

    private static async Task<string[]> ProjectsAsync(IDbContextFactory<LinkbaseDbContext> factory)
    {
        await using var db = factory.CreateDbContext();
        return await db.Nodes.AsNoTracking().Where(n => n.Type == "Project").OrderBy(n => n.Id).Select(n => n.Id.Substring(8)).ToArrayAsync();
    }

    [Theory]
    [InlineData("sam.partner", "pr1,pr2,pr3,pr4,pr5")]
    [InlineData("ava.lindqvist", "pr1,pr2")]
    [InlineData("chloe.marsh", "pr1,pr3")]
    [InlineData("guest", "pr1")]
    [InlineData("someone.unlisted", "")]
    [InlineData(null, "")]
    public async Task Each_person_sees_the_projects_their_grants_allow(string? user, string expected)
    {
        var setup = await ConsultancyAsync();
        Assert.Equal(expected, string.Join(",", await ProjectsAsync(setup.For(user))));
    }

    [Fact]
    public async Task The_database_agrees_with_the_rule_for_every_record_and_person()
    {
        var setup = await ConsultancyAsync();
        var directory = ViewerDirectory.Create(setup.Security);
        List<(string Id, Label Label)> all;
        await using (var owner = setup.Owner())
        {
            all = (await owner.Nodes.AsNoTracking().Select(n => new { n.Id, n.LabelLevel, n.LabelAny, n.LabelAll }).ToListAsync())
                .Select(n => (n.Id, LabelCodec.ToLabel(n.LabelLevel, n.LabelAny, n.LabelAll))).ToList();
        }

        foreach (var user in new[] { "sam.partner", "ava.lindqvist", "chloe.marsh", "guest", "nobody" })
        {
            var viewer = await directory.ResolveAsync(user);
            var expected = all.Where(n => VisibilityRule.CanSee(setup.Security, viewer, n.Label)).Select(n => n.Id).Order(StringComparer.Ordinal).ToArray();
            await using var db = setup.For(user).CreateDbContext();
            var actual = await db.Nodes.AsNoTracking().Select(n => n.Id).ToListAsync();
            Assert.Equal(expected, actual.Order(StringComparer.Ordinal).ToArray());
        }
    }

    [Fact]
    public async Task A_link_shows_only_when_both_its_records_do_and_the_views_follow()
    {
        var setup = await ConsultancyAsync();
        await using var owner = setup.Owner();
        var everything = await owner.NodeLinks.CountAsync();
        Assert.True(everything > 0);

        await using var sam = setup.For("sam.partner").CreateDbContext();
        Assert.Equal(everything, await sam.NodeLinks.CountAsync());

        await using var guest = setup.For("guest").CreateDbContext();
        var visible = (await guest.Nodes.Select(n => n.Id).ToListAsync()).ToHashSet();
        var links = await guest.NodeLinks.Select(l => new { l.SourceId, l.TargetId }).ToListAsync();
        Assert.True(links.Count < everything);
        Assert.All(links, l => Assert.True(visible.Contains(l.SourceId) && visible.Contains(l.TargetId)));

        // The views run as the caller, so they show the same.
        Assert.Equal(links.Count, await guest.Database.SqlQuery<int>($"select count(*)::int as \"Value\" from graph_edges").SingleAsync());
        Assert.Equal(visible.Count, await guest.Database.SqlQuery<int>($"select count(*)::int as \"Value\" from graph_nodes").SingleAsync());
    }

    [Fact]
    public async Task A_person_cannot_write_a_record_they_could_not_read()
    {
        var setup = await ConsultancyAsync();
        await using var ava = setup.For("ava.lindqvist").CreateDbContext();
        ava.Nodes.Add(new Node { Id = "project:secret", Type = "Project", Key = "secret", Name = "Secret", Props = "{}", UserId = AppUser.DefaultId, LabelLevel = 2 });
        var failure = await Assert.ThrowsAsync<DbUpdateException>(() => ava.SaveChangesAsync());
        Assert.Equal("42501", ((PostgresException)failure.InnerException!).SqlState); // row-level security violation

        await using var ava2 = setup.For("ava.lindqvist").CreateDbContext();
        ava2.Nodes.Add(new Node { Id = "project:fine", Type = "Project", Key = "fine", Name = "Fine", Props = "{}", UserId = AppUser.DefaultId, LabelLevel = 1 });
        await ava2.SaveChangesAsync();
    }

    [Fact]
    public async Task Where_a_record_came_from_is_visible_only_with_the_record()
    {
        var setup = await ConsultancyAsync();
        await using (var owner = setup.Owner())
        {
            owner.ExternalRefs.AddRange(
                new ExternalRef { NodeId = "project:pr1", NodeType = "Project", Source = DataSource.Csv, ExternalId = "1" },
                new ExternalRef { NodeId = "project:pr4", NodeType = "Project", Source = DataSource.Csv, ExternalId = "4" });
            await owner.SaveChangesAsync();
        }

        await using var guest = setup.For("guest").CreateDbContext();
        Assert.Equal(["1"], await guest.ExternalRefs.Select(r => r.ExternalId).ToArrayAsync());
    }

    [Fact]
    public async Task A_connection_never_keeps_the_last_viewer()
    {
        var setup = await ConsultancyAsync();
        await using (var guest = setup.For("guest").CreateDbContext())
        {
            Assert.Equal("linkbase_app", await guest.Database.SqlQuery<string>($"select current_user::text as \"Value\"").SingleAsync());
        }

        // The pool hands the same physical connection back; it must be the owner again, with no viewer settings.
        await using var plain = setup.Owner();
        Assert.NotEqual("linkbase_app", await plain.Database.SqlQuery<string>($"select current_user::text as \"Value\"").SingleAsync());
        Assert.Equal("", await plain.Database.SqlQuery<string>($"select coalesce(current_setting('linkbase.level', true), '') as \"Value\"").SingleAsync());
        Assert.Equal(await plain.Nodes.CountAsync(), (await ProjectsAsync(new Factory(setup.ConnectionString))).Length > 0 ? await plain.Nodes.CountAsync() : -1);
    }

    [Fact]
    public async Task Turning_security_off_removes_everything_and_everyone_sees_all()
    {
        var setup = await ConsultancyAsync();
        await using (var owner = setup.Owner())
        {
            Assert.True(await RowSecurity.IsEnabledAsync(owner));
            await RowSecurity.DisableAsync(owner);
            Assert.False(await RowSecurity.IsEnabledAsync(owner));
            Assert.Equal(0, await owner.Database.SqlQuery<int>($"select count(*)::int as \"Value\" from pg_policies").SingleAsync());
            Assert.Equal(0, await owner.Database.SqlQuery<int>($"select count(*)::int as \"Value\" from pg_namespace where nspname = 'linkbase_sec'").SingleAsync());
        }

        Assert.Equal("pr1,pr2,pr3,pr4,pr5", string.Join(",", await ProjectsAsync(setup.For("guest"))));
    }

    [Fact]
    public async Task Turning_security_on_again_is_safe_to_repeat()
    {
        var setup = await ConsultancyAsync();
        await using (var owner = setup.Owner())
        {
            await RowSecurity.EnableAsync(owner);
        }

        Assert.Equal("pr1", string.Join(",", await ProjectsAsync(setup.For("guest"))));
    }

    [Fact]
    public async Task Without_row_security_the_connection_is_left_alone()
    {
        var setup = await ConsultancyAsync(enable: false);
        await using var guest = setup.For("guest").CreateDbContext();
        Assert.NotEqual("linkbase_app", await guest.Database.SqlQuery<string>($"select current_user::text as \"Value\"").SingleAsync());
        Assert.Equal("pr1,pr2,pr3,pr4,pr5", string.Join(",", await ProjectsAsync(setup.For("guest"))));
    }

    [Fact]
    public async Task A_model_without_a_level_part_still_works()
    {
        var connectionString = await fixture.CreateModelDatabaseAsync("SELECT 1");
        var security = new ModelSecurity();
        await using var db = new LinkbaseDbContext(new DbContextOptionsBuilder<LinkbaseDbContext>().UseNpgsql(connectionString).Options);
        db.NodeTypes.Add(new NodeType { Name = "T" });
        db.Nodes.AddRange(
            new Node { Id = "t:a", Type = "T", Key = "a", Name = "A", Props = "{}", UserId = AppUser.DefaultId, LabelAny = ["Design"] },
            new Node { Id = "t:b", Type = "T", Key = "b", Name = "B", Props = "{}", UserId = AppUser.DefaultId });
        var settings = await db.ModelSettings.SingleOrDefaultAsync();
        if (settings is null) { db.ModelSettings.Add(settings = new ModelSetting()); }
        settings.Security = LabelCodec.ToJson(new ModelSecurity { AnyOf = new SecurityPart { Name = "Practice" } });
        await db.SaveChangesAsync();
        await RowSecurity.EnableAsync(db);

        var any = new ModelSecurity { AnyOf = new SecurityPart { Name = "Practice" }, Users = new Dictionary<string, UserGrants> { ["x"] = new UserGrants { AnyOf = ["Engineering"] } } };
        var setup = new Setup(connectionString, any);
        await using var viewer = setup.For("x").CreateDbContext();
        Assert.Equal(["t:b"], await viewer.Nodes.Select(n => n.Id).ToArrayAsync());
    }
}
