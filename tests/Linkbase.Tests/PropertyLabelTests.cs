using Linkbase.Core;
using Linkbase.Core.Graph;
using Linkbase.Core.History;
using Linkbase.Core.Records;
using Linkbase.Core.Security;
using Linkbase.Data;
using Microsoft.EntityFrameworkCore;

namespace Linkbase.Tests;

// A label on one record's one property, on top of the field's own. It only restricts, and every reader honours it. See
// docs/history/security-design.md.
[Trait("Category", "Database")]
public sealed class PropertyLabelTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private const string Project = "project:pr2";
    private static readonly string Consultancy = RepoPaths.Resolve("models/consultancy/model.json");

    private sealed class Resolver(ModelSecurity security, string? user) : IViewerResolver
    {
        private readonly ViewerDirectory _directory = ViewerDirectory.Create(security);
        public async ValueTask<Viewer> ResolveAsync(ModelSecurity _, CancellationToken cancellationToken = default) =>
            await _directory.ResolveAsync(user, cancellationToken);
    }

    private sealed class Factory(string connectionString, params Microsoft.EntityFrameworkCore.Diagnostics.IInterceptor[] interceptors) : IDbContextFactory<LinkbaseDbContext>
    {
        public LinkbaseDbContext CreateDbContext() =>
            new(new DbContextOptionsBuilder<LinkbaseDbContext>().UseNpgsql(connectionString).AddInterceptors(interceptors).Options);
    }

    private sealed record Viewing(RecordRepository Records, GraphRepository Graph, LabelRepository Labels, HistoryRepository History);

    private async Task<(string Connection, ModelSecurity Security)> ConsultancyAsync()
    {
        var connection = await fixture.CreateModelDatabaseAsync("SELECT 1");
        var owner = new Factory(connection);
        await TestModels.LoadAsync(owner, Consultancy, TextWriter.Null);
        var security = await new LabelRepository(owner).SettingsAsync() ?? throw new InvalidOperationException("The consultancy model has security.");
        await using var db = owner.CreateDbContext();
        await RowSecurity.EnableAsync(db);
        return (connection, security);
    }

    private static Viewing As(string connection, ModelSecurity security, string user)
    {
        var state = new RowSecurityState(TimeProvider.System);
        var resolver = new Resolver(security, user);
        var factory = new Factory(connection, new ViewerConnectionInterceptor(state, resolver));
        var policy = new FieldPolicy(state, resolver);
        return new Viewing(new RecordRepository(factory, null, policy), new GraphRepository(factory, policy), new LabelRepository(factory),
            new HistoryRepository(factory, policy));
    }

    // Sam (confidential) hides the status of one project from everyone below confidential.
    private static Task<bool> HideStatusAsync(Viewing sam) =>
        sam.Labels.EditPropertyAsync(Project, "status", new LabelEdit(Level: "confidential"), new ChangeReason("Client asked"));

    [Fact]
    public async Task A_property_label_hides_the_value_from_a_viewer_who_fails_it_and_only_on_that_record()
    {
        var (connection, security) = await ConsultancyAsync();
        var sam = As(connection, security, "sam.partner");
        var ava = As(connection, security, "ava.lindqvist");
        Assert.True(await HideStatusAsync(sam));

        var hiddenFromAva = await ava.Records.GetAsync("Project", "pr2");
        Assert.NotNull(hiddenFromAva);
        Assert.DoesNotContain("status", hiddenFromAva.Values.Keys);
        Assert.Contains("status", hiddenFromAva.Masked);

        var seenBySam = await sam.Records.GetAsync("Project", "pr2");
        Assert.Equal("active", seenBySam!.Values["status"]);
        Assert.Empty(seenBySam.Masked);

        // Other records keep showing it.
        var rows = await ava.Records.ListAsync("Project", null, null, [], 0, 50);
        Assert.Contains(rows.Rows, r => r.Key != "pr2" && r.Values.ContainsKey("status"));
        Assert.All(rows.Rows.Where(r => r.Key == "pr2"), r => Assert.DoesNotContain("status", r.Values.Keys));
    }

    [Fact]
    public async Task Filtering_cannot_reach_a_value_a_property_label_hides()
    {
        var (connection, security) = await ConsultancyAsync();
        var sam = As(connection, security, "sam.partner");
        var ava = As(connection, security, "ava.lindqvist");
        var active = new[] { new RecordFilter("status", FilterOp.Is, "active") };
        var before = (await ava.Records.ListAsync("Project", null, null, active, 0, 50)).Rows;
        Assert.Contains(before, r => r.Key == "pr2");
        await HideStatusAsync(sam);

        var after = (await ava.Records.ListAsync("Project", null, null, active, 0, 50)).Rows;
        Assert.DoesNotContain(after, r => r.Key == "pr2");
        Assert.Equal(before.Count - 1, after.Count);
        Assert.Contains((await sam.Records.ListAsync("Project", null, null, active, 0, 50)).Rows, r => r.Key == "pr2");
    }

    [Fact]
    public async Task A_viewer_cannot_set_a_label_above_their_own_and_nothing_is_written()
    {
        var (connection, security) = await ConsultancyAsync();
        var ava = As(connection, security, "ava.lindqvist");

        await Assert.ThrowsAsync<LinkbaseValidationException>(() =>
            ava.Labels.EditPropertyAsync(Project, "status", new LabelEdit(Level: "confidential"), null));
        Assert.Empty(await As(connection, security, "sam.partner").Labels.GetPropertyLabelsAsync(Project));
    }

    [Fact]
    public async Task Clearing_the_label_shows_the_value_again_and_both_changes_are_in_the_history()
    {
        var (connection, security) = await ConsultancyAsync();
        var sam = As(connection, security, "sam.partner");
        var ava = As(connection, security, "ava.lindqvist");
        await HideStatusAsync(sam);
        Assert.Contains("status", (await sam.Labels.GetPropertyLabelsAsync(Project)).Keys);

        // Setting the same label again changes nothing.
        Assert.False(await sam.Labels.EditPropertyAsync(Project, "status", new LabelEdit(Level: "confidential"), null));

        Assert.True(await sam.Labels.EditPropertyAsync(Project, "status", new LabelEdit(Level: "public"), new ChangeReason("Client agreed")));
        Assert.Empty(await sam.Labels.GetPropertyLabelsAsync(Project));
        Assert.Equal("active", (await ava.Records.GetAsync("Project", "pr2"))!.Values["status"]);

        var entries = await sam.History.ForRecordAsync(Project);
        var changes = entries.SelectMany(e => e.Changes).Where(c => c.Kind == ChangeEntityKind.PropertyLabel).ToList();
        Assert.Equal(2, changes.Count);
        Assert.All(changes, c => Assert.Equal("status", c.FieldKey));
    }

    [Fact]
    public async Task The_graph_and_its_suggestions_leave_out_a_value_a_property_label_hides()
    {
        var (connection, security) = await ConsultancyAsync();
        var sam = As(connection, security, "sam.partner");
        var ava = As(connection, security, "ava.lindqvist");
        await HideStatusAsync(sam);

        var node = (await ava.Graph.SearchNodesAsync("Booking", ["Project"])).Single();
        Assert.Null(node.Property("status"));
        Assert.Equal("active", (await sam.Graph.SearchNodesAsync("Booking", ["Project"])).Single().Property("status"));

        Assert.True((await ava.Graph.SchemaAsync()).HasOverrides);
        var seen = await ava.Graph.PropertyValuesAsync("Project", "status", "act");
        var seenBySam = await sam.Graph.PropertyValuesAsync("Project", "status", "act");
        Assert.Contains("active", seenBySam);
        Assert.True(seen.Count <= seenBySam.Count);
    }
}
