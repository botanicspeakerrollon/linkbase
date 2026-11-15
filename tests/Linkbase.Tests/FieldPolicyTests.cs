using Linkbase.Core;
using Linkbase.Core.Graph;
using Linkbase.Core.History;
using Linkbase.Core.Models;
using Linkbase.Core.Records;
using Linkbase.Core.Security;
using Linkbase.Data;
using Microsoft.EntityFrameworkCore;

namespace Linkbase.Tests;

// Field labels: a property a viewer may not see is masked, and can't be reached by filtering, sorting, searching, suggestions or
// the graph. The consultancy model labels a project's budget "confidential". See docs/security-design.md.
[Trait("Category", "Database")]
public sealed class FieldPolicyTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
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

    // What a person's circuit would use: their records, graph and policy, all reading as them.
    private sealed record Viewing(RecordRepository Records, GraphRepository Graph, FieldPolicy Policy, LinkbaseDbContextFactoryHolder Holder);

    private sealed record LinkbaseDbContextFactoryHolder(IDbContextFactory<LinkbaseDbContext> Factory);

    private async Task<(string ConnectionString, ModelSecurity Security)> ConsultancyAsync(bool enable = true)
    {
        var connectionString = await fixture.CreateModelDatabaseAsync("SELECT 1");
        var owner = new Factory(connectionString);
        await TestModels.LoadAsync(owner, Consultancy, TextWriter.Null);
        var security = await new LabelRepository(owner).SettingsAsync() ?? throw new InvalidOperationException("The consultancy model has security.");
        if (enable)
        {
            await using var db = owner.CreateDbContext();
            await RowSecurity.EnableAsync(db);
        }

        return (connectionString, security);
    }

    private static Viewing As(string connectionString, ModelSecurity security, string? user)
    {
        var state = new RowSecurityState(TimeProvider.System);
        var resolver = new Resolver(security, user);
        var factory = new Factory(connectionString, new ViewerConnectionInterceptor(state, resolver));
        var policy = new FieldPolicy(state, resolver);
        return new Viewing(new RecordRepository(factory, null, policy), new GraphRepository(factory, Linkbase.Domains.RegisteredDomains.Registry, policy), policy, new(factory));
    }

    [Fact]
    public async Task A_viewer_below_the_fields_level_sees_it_masked_and_one_at_it_sees_the_value()
    {
        var (connection, security) = await ConsultancyAsync();

        var sam = await As(connection, security, "sam.partner").Records.GetAsync("Project", "pr2");
        Assert.NotNull(sam);
        Assert.Equal("40000", sam.Values["budget"]);
        Assert.Empty(sam.Masked);

        var ava = await As(connection, security, "ava.lindqvist").Records.GetAsync("Project", "pr2");
        Assert.NotNull(ava);
        Assert.DoesNotContain("budget", ava.Values.Keys);
        Assert.Contains("budget", ava.Masked);
        Assert.Equal("active", ava.Values["status"]);
    }

    [Fact]
    public async Task The_list_masks_the_field_and_the_value_is_nowhere_in_the_rows()
    {
        var (connection, security) = await ConsultancyAsync();
        var rows = await As(connection, security, "ava.lindqvist").Records.ListAsync("Project", null, null, [], 0, 50);
        Assert.NotEmpty(rows.Rows);
        Assert.All(rows.Rows, r =>
        {
            Assert.Contains("budget", r.Masked);
            Assert.DoesNotContain("budget", r.Values.Keys);
        });
    }

    [Fact]
    public async Task Filtering_or_sorting_on_a_masked_field_fails_like_an_unknown_one()
    {
        var (connection, security) = await ConsultancyAsync();
        var records = As(connection, security, "ava.lindqvist").Records;

        var filter = await Assert.ThrowsAnyAsync<Exception>(() => records.ListAsync("Project", null, null, [new RecordFilter("budget", FilterOp.AtLeast, "50000")], 0, 50));
        var unknown = await Assert.ThrowsAnyAsync<Exception>(() => records.ListAsync("Project", null, null, [new RecordFilter("nothing", FilterOp.AtLeast, "50000")], 0, 50));
        Assert.Equal(unknown.Message.Replace("nothing", "budget"), filter.Message);

        await Assert.ThrowsAnyAsync<Exception>(() => records.ListAsync("Project", null, new RecordSort("budget"), [], 0, 50));

        // The one who may see it can.
        var sam = As(connection, security, "sam.partner").Records;
        var rows = await sam.ListAsync("Project", null, null, [new RecordFilter("budget", FilterOp.AtLeast, "50000")], 0, 50);
        Assert.NotEmpty(rows.Rows);
    }

    [Fact]
    public async Task Search_cannot_find_a_record_by_a_value_the_viewer_cannot_see()
    {
        var (connection, security) = await ConsultancyAsync();
        var ava = As(connection, security, "ava.lindqvist");
        var sam = As(connection, security, "sam.partner");

        Assert.NotEmpty((await sam.Records.ListAsync("Project", "40000", null, [], 0, 50)).Rows);
        Assert.Empty((await ava.Records.ListAsync("Project", "40000", null, [], 0, 50)).Rows);

        // Neither can the graph's free text, or its suggestions.
        Assert.DoesNotContain(await ava.Graph.MatchPropertiesAsync("Project", "40000"), m => m.Key == "budget");
        Assert.Empty(await ava.Graph.PropertyValuesAsync("Project", "budget", "4"));
        Assert.NotEmpty(await sam.Graph.PropertyValuesAsync("Project", "budget", "4"));
    }

    [Fact]
    public async Task The_graph_leaves_out_the_property_and_the_schema_does_not_offer_it()
    {
        var (connection, security) = await ConsultancyAsync();
        var ava = As(connection, security, "ava.lindqvist");
        var sam = As(connection, security, "sam.partner");

        var shown = (await ava.Graph.SearchNodesAsync("Booking", ["Project"])).Single();
        Assert.Null(shown.Property("budget"));
        Assert.Equal("active", shown.Property("status"));
        Assert.Equal("40000", (await sam.Graph.SearchNodesAsync("Booking", ["Project"])).Single().Property("budget"));

        Assert.DoesNotContain((await ava.Graph.SchemaAsync()).Properties, p => p is { Label: "Project", Key: "budget" });
        Assert.Contains((await sam.Graph.SchemaAsync()).Properties, p => p is { Label: "Project", Key: "budget" });
    }

    [Fact]
    public async Task A_save_by_a_viewer_who_cannot_see_a_field_keeps_its_value()
    {
        var (connection, security) = await ConsultancyAsync();
        var ava = As(connection, security, "ava.lindqvist");
        var detail = (await ava.Records.GetAsync("Project", "pr2"))!;

        var values = detail.Values.ToDictionary(v => v.Key, v => (string?)v.Value);
        values["status"] = "live";
        await ava.Records.UpdateAsync(detail.Id, new RecordInput(detail.Name, values), new ChangeReason("test"));

        var sam = (await As(connection, security, "sam.partner").Records.GetAsync("Project", "pr2"))!;
        Assert.Equal("live", sam.Values["status"]);
        Assert.Equal("40000", sam.Values["budget"]);
    }

    [Fact]
    public async Task With_security_off_nothing_is_masked_and_no_policy_query_runs()
    {
        var (connection, security) = await ConsultancyAsync(enable: false);
        var guest = As(connection, security, "guest");
        var detail = (await guest.Records.GetAsync("Project", "pr2"))!;
        Assert.Equal("40000", detail.Values["budget"]);
        Assert.Empty(detail.Masked);

        await using var db = guest.Holder.Factory.CreateDbContext();
        Assert.Same(HiddenFields.None, await guest.Policy.HiddenAsync(db));
    }

    [Fact]
    public async Task The_policy_agrees_with_the_rule_for_every_field_and_person()
    {
        var (connection, security) = await ConsultancyAsync();
        var directory = ViewerDirectory.Create(security);
        List<(string Type, string Key, Label Label)> fields;
        await using (var owner = new Factory(connection).CreateDbContext())
        {
            fields = (await owner.FieldDefinitions.AsNoTracking().Select(f => new { f.NodeTypeName, f.Key, f.LabelLevel, f.LabelAny, f.LabelAll }).ToListAsync())
                .Select(f => (f.NodeTypeName, f.Key, LabelCodec.ToLabel(f.LabelLevel, f.LabelAny, f.LabelAll))).ToList();
        }

        Assert.Contains(fields, f => f.Type == "Project" && f.Key == "budget" && f.Label.Level > 0);
        foreach (var user in new[] { "sam.partner", "ava.lindqvist", "chloe.marsh", "guest", "nobody" })
        {
            var viewer = await directory.ResolveAsync(user);
            var viewing = As(connection, security, user);
            await using var db = viewing.Holder.Factory.CreateDbContext();
            var hidden = await viewing.Policy.HiddenAsync(db);

            // Only fields with a label of their own are looked at. An unlabelled field is also "hidden" from someone who sees no
            // records at all, by the rule, but they have nothing to mask it on.
            foreach (var (type, key, label) in fields.Where(f => f.Label.Level > 0 || f.Label.AnyOf.Count > 0 || f.Label.AllOf.Count > 0))
            {
                Assert.Equal(!VisibilityRule.CanSee(security, viewer, label), hidden.IsHidden(type, key));
            }
        }
    }
}
