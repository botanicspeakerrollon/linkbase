using Linkbase.Core;
using Linkbase.Core.Cleanup;
using Linkbase.Core.Graph;
using Linkbase.Core.History;
using Linkbase.Core.Home;
using Linkbase.Core.Paths;
using Linkbase.Core.Records;
using Linkbase.Core.Timeline;
using Linkbase.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Linkbase.Tests;

// The home page's panels, cleanup, paths between records, the history near a record, and the timeline and "as of" view. All run on
// the made-up consultancy, with every change recorded and no reason needed.
[Trait("Category", "Database")]
public sealed class EverydayUseTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static readonly string Example = RepoPaths.Resolve("models/consultancy/model.json");

    private sealed record World(ModelFactory Factory, RecordRepository Records, GraphRepository Graph)
    {
        public CleanupRepository Cleanup => new(Factory);
        public HistoryRepository History => new(Factory);
        public PathRepository Paths => new(Factory);
        public TimelineRepository Timeline => new(Factory);
        public HomeRepository Home => new(Factory);
    }

    private async Task<World> ExampleAsync()
    {
        var factory = new ModelFactory(await fixture.CreateModelDatabaseAsync("SELECT 1"));
        await TestModels.LoadAsync(factory, Example, TextWriter.Null);
        return new World(factory, new RecordRepository(factory), new GraphRepository(factory));
    }

    private static async Task<string> PersonAsync(World world, string name, Dictionary<string, string?>? values = null, IEnumerable<NewLink>? links = null) =>
        $"person:{await world.Records.CreateAsync("Person", null, TestModels.Person(name, values, links))}";

    private static async Task<string> ProjectAsync(World world, string name) =>
        $"project:{await world.Records.CreateAsync("Project", null, new RecordInput(name, new Dictionary<string, string?> { ["status"] = "active" }))}";

    // The latest time anything was saved, whether the audit trail or a record's own stamp, which are made by two clocks.
    private static async Task<DateTimeOffset> LatestAsync(World world)
    {
        await using var db = world.Factory.CreateDbContext();
        var changed = await db.ChangeSets.MaxAsync(s => s.CreatedAt);
        var created = await db.Nodes.MaxAsync(n => n.CreatedAt);
        return changed > created ? changed : created;
    }

    [Fact]
    public async Task Home_lists_recent_records_changes_and_growth_and_leaves_out_what_was_not_logged()
    {
        var world = await ExampleAsync();
        var name = $"Recent {Guid.NewGuid():N}";
        var id = await PersonAsync(world, name);

        var records = await world.Home.RecentRecordsAsync(5);
        var recent = records.First();
        Assert.Equal(id, recent.Id);
        Assert.True(recent.Added);
        Assert.Equal(name, recent.Name);

        await world.Records.UpdateAsync(id, TestModels.Person(name, new() { ["title"] = "Designer" }));
        var edited = (await world.Home.RecentRecordsAsync(5)).Where(r => r.Id == id).ToList();
        Assert.Single(edited);

        var changes = await world.Home.RecentChangesAsync(3);
        Assert.Equal(JustificationKind.Manual, changes[0].Kind);
        Assert.True(changes[0].Changes > 0);

        var growth = await world.Home.GrowthAsync(3, DateTimeOffset.UtcNow);
        Assert.Equal(3, growth.Count);
        Assert.True(growth[^1].Count >= 1);
        Assert.Equal(DateOnly.FromDateTime(DateTime.UtcNow).Month, growth[^1].Month.Month);
    }

    [Fact]
    public async Task Cleanup_finds_names_that_match_ignoring_case_and_spacing()
    {
        var world = await ExampleAsync();
        var stem = $"Dup{Guid.NewGuid():N}";
        await PersonAsync(world, $"{stem} Adams");
        await PersonAsync(world, $"  {stem.ToLowerInvariant()}   adams ");
        await PersonAsync(world, $"{stem} Brown");

        var found = await world.Cleanup.DuplicatesAsync("Person");

        var group = Assert.Single(found.Groups, g => g.Name.Contains(stem, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(2, group.Records.Count);
        Assert.DoesNotContain(found.Groups, g => g.Name.Contains("Brown", StringComparison.OrdinalIgnoreCase) && g.Name.Contains(stem, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Cleanup_finds_records_with_no_links_and_records_missing_a_required_value()
    {
        var world = await ExampleAsync();
        var lonely = await PersonAsync(world, $"Lonely {Guid.NewGuid():N}");
        await using (var db = world.Factory.CreateDbContext())
        {
            await db.Database.ExecuteSqlRawAsync("INSERT INTO node (id, user_id, type, key, name, props) VALUES ('project:no-status', 1, 'Project', 'no-status', 'No status', '{{}}')");
        }

        var unlinked = await world.Cleanup.UnlinkedAsync("Person");
        Assert.Contains(unlinked.Items, r => r.Id == lonely);

        var missing = await world.Cleanup.MissingRequiredAsync("Project");
        var issue = Assert.Single(missing.Items);
        Assert.Equal("project:no-status", issue.Id);
        Assert.Equal("No status", issue.Problem);
    }

    [Fact]
    public async Task Merging_moves_links_fills_gaps_keeps_the_kept_records_values_and_is_recorded()
    {
        var world = await ExampleAsync();
        var project = await ProjectAsync(world, "Merge project");
        var other = await ProjectAsync(world, "Other project");
        var keep = await PersonAsync(world, "Merge Person", new() { ["skills"] = "Figma", ["title"] = "Lead" });
        var drop = await PersonAsync(world, "Merge Person", new() { ["skills"] = "SQL\nFigma", ["title"] = "Ignored", ["joined"] = "2026-02-02" });
        var values = new Dictionary<string, string?> { ["role"] = "Designer" };
        await world.Records.AddLinkAsync(keep, "WORKS_ON", project, values);
        await world.Records.AddLinkAsync(drop, "WORKS_ON", project, values);
        await world.Records.AddLinkAsync(drop, "WORKS_ON", other, new Dictionary<string, string?> { ["role"] = "Reviewer" });
        var before = (await world.Graph.ConnectionCountsAsync()).Single(c => c.RelType == "WORKS_ON").Links;

        var result = await world.Records.MergeAsync(keep, drop, new ChangeReason("Same person entered twice"));

        Assert.Equal(1, result.LinksMoved);
        Assert.Equal(1, result.LinksDropped);
        Assert.True(result.ValuesFilled >= 2);
        var merged = (await world.Records.GetAsync("Person", keep["person:".Length..]))!;
        Assert.Equal("Lead", merged.Values["title"]);
        Assert.Equal("2026-02-02", merged.Values["joined"]);
        Assert.Equal(["Figma", "SQL"], merged.Values["skills"].Split('\n').Order());
        Assert.Null(await world.Records.GetAsync("Person", drop["person:".Length..]));

        var works = (await world.Records.LinksAsync(keep)).Single(g => g.RelType == "WORKS_ON");
        Assert.Equal(["Merge project", "Other project"], works.Links.Select(l => l.OtherName).Order());
        Assert.Equal(before - 1, (await world.Graph.ConnectionCountsAsync()).Single(c => c.RelType == "WORKS_ON").Links);

        var history = (await world.History.ForRecordAsync(keep)).First();
        Assert.Equal("Same person entered twice", history.Note);
        Assert.Contains(history.Changes, c => c.FieldKey == "joined" && c.NewValue == "2026-02-02");
        var gone = (await world.History.ForRecordAsync(drop)).First();
        Assert.Equal(history.ChangeSetId, gone.ChangeSetId);
        Assert.Contains(gone.Changes, c => c.Kind == ChangeEntityKind.Node && c.Op == ChangeOp.Delete);
    }

    [Fact]
    public async Task Merging_is_refused_whole_when_it_would_break_a_limit_and_for_different_types_or_the_same_record()
    {
        var world = await ExampleAsync();
        var teams = (await world.Records.FindAsync("Team", null, 2));
        var first = await PersonAsync(world, "Limit Person", links: [new NewLink("MEMBER_OF", teams[0].Id)]);
        var second = await PersonAsync(world, "Limit Person", links: [new NewLink("MEMBER_OF", teams[1].Id)]);

        var over = await Assert.ThrowsAsync<LinkbaseValidationException>(() => world.Records.MergeAsync(first, second));
        Assert.Contains("at most 1", over.Message);
        Assert.NotNull(await world.Records.GetAsync("Person", second["person:".Length..]));
        Assert.Equal(teams[1].Name, (await world.Records.LinksAsync(second)).Single().Links.Single().OtherName);

        var project = await ProjectAsync(world, "Not a person");
        Assert.Contains("same type", (await Assert.ThrowsAsync<LinkbaseValidationException>(() => world.Records.MergeAsync(first, project))).Message);
        Assert.Contains("two different", (await Assert.ThrowsAsync<LinkbaseValidationException>(() => world.Records.MergeAsync(first, first))).Message);
    }

    [Fact]
    public async Task Merging_a_record_into_its_own_duplicate_drops_the_link_between_them()
    {
        var world = await ExampleAsync();
        var a = await PersonAsync(world, "Pair Person");
        var b = await PersonAsync(world, "Pair Person");
        var project = await ProjectAsync(world, "Pair project");
        await world.Records.AddLinkAsync(a, "WORKS_ON", project);
        await world.Records.AddLinkAsync(b, "WORKS_ON", project);

        var result = await world.Records.MergeAsync(a, b);

        Assert.Equal(0, result.LinksMoved);
        Assert.Equal(1, result.LinksDropped);
        Assert.Single((await world.Records.LinksAsync(a)).Single(g => g.RelType == "WORKS_ON").Links);
    }

    [Fact]
    public async Task The_shortest_path_runs_either_way_and_says_when_there_is_none()
    {
        var world = await ExampleAsync();
        var person = await PersonAsync(world, "Path Person");
        var project = await ProjectAsync(world, "Path project");
        var client = $"client:{await world.Records.CreateAsync("Client", null, new RecordInput("Path client", new Dictionary<string, string?>()))}";
        await world.Records.AddLinkAsync(person, "WORKS_ON", project);
        await world.Records.AddLinkAsync(project, "FOR", client);
        var island = $"client:{await world.Records.CreateAsync("Client", null, new RecordInput("Island client", new Dictionary<string, string?>()))}";

        var path = await world.Paths.ShortestPathAsync(person, client);
        Assert.True(path.Found);
        Assert.Equal([person, project, client], path.NodeIds);
        Assert.Equal(["WORKS_ON", "FOR"], path.Edges.Select(e => e.RelType));
        Assert.Equal(2, path.Hops);

        var back = await world.Paths.ShortestPathAsync(client, person);
        Assert.Equal([client, project, person], back.NodeIds);
        Assert.Equal([project, client], [back.Edges[0].SourceId, back.Edges[0].TargetId]);

        Assert.False((await world.Paths.ShortestPathAsync(person, island)).Found);
        Assert.False((await world.Paths.ShortestPathAsync(person, client, maxHops: 1)).Found);
        Assert.Equal([person], (await world.Paths.ShortestPathAsync(person, person)).NodeIds);
        await Assert.ThrowsAsync<LinkbaseValidationException>(() => world.Paths.ShortestPathAsync(person, "client:nope"));
    }

    [Fact]
    public async Task A_links_history_and_the_changes_near_a_record_come_from_the_audit_trail()
    {
        var world = await ExampleAsync();
        var person = await PersonAsync(world, "Near Person");
        var project = await ProjectAsync(world, "Near project");
        var bystander = await PersonAsync(world, "Bystander");
        await world.Records.AddLinkAsync(person, "WORKS_ON", project, new Dictionary<string, string?> { ["role"] = "Designer" });
        var linkId = (await world.Records.LinksAsync(person)).Single(g => g.RelType == "WORKS_ON").Links.Single().Id;
        await world.Records.UpdateLinkAsync(linkId, new Dictionary<string, string?> { ["role"] = "Lead" });

        var link = await world.History.ForLinkAsync(person, "WORKS_ON", project);
        Assert.Equal(2, link.Count);
        Assert.Contains(link[0].Changes, c => c.FieldKey == "role" && c.OldValue == "Designer" && c.NewValue == "Lead");
        Assert.Contains(link[1].Changes, c => c.Op == ChangeOp.Create);

        var near = await world.History.NearbyAsync(person, DateTimeOffset.UtcNow.AddDays(-1));
        Assert.Contains(near, c => c.Description.StartsWith("Linked: ", StringComparison.Ordinal));
        Assert.Contains(near, c => c.Description.Contains("Designer → Lead", StringComparison.Ordinal));
        Assert.Contains(near, c => c.RecordName == "Near project" && c.Description == "Added");
        Assert.DoesNotContain(near, c => c.RecordId == bystander);
        Assert.Empty(await world.History.NearbyAsync(person, DateTimeOffset.UtcNow.AddDays(1)));
    }

    [Fact]
    public async Task The_timeline_lists_records_by_date_and_counts_months_and_the_undated()
    {
        var world = await ExampleAsync();
        var fields = await world.Timeline.DateFieldsAsync();
        Assert.Contains(new DateFieldOption("Person", "joined"), fields);
        Assert.Contains(new DateFieldOption("Project", "started"), fields);
        Assert.DoesNotContain(new DateFieldOption("Person", "certified_on"), fields);

        await PersonAsync(world, "Dated One", new() { ["joined"] = "2031-03-10" });
        await PersonAsync(world, "Dated Two", new() { ["joined"] = "2031-03-02" });
        await PersonAsync(world, "Dated Three", new() { ["joined"] = "2031-05-20" });
        await PersonAsync(world, "Undated");

        var all = await world.Timeline.LoadAsync("Person", "joined", "2031-01-01", "2031-12-31");
        Assert.Equal(["Dated Three", "Dated One", "Dated Two"], all.Entries.Select(e => e.Name));
        Assert.Equal([("2031-03", 2), ("2031-05", 1)], all.Months.Select(m => (m.Month, m.Count)));
        Assert.Equal(3, all.Total);
        Assert.True(all.Undated >= 1);

        var march = await world.Timeline.LoadAsync("Person", "joined", "2031-03-01", "2031-03-31");
        Assert.Equal(2, march.Total);
        Assert.Equal(new DateOnly(2031, 3, 10), march.Entries[0].Date);
    }

    [Fact]
    public async Task As_of_hides_what_did_not_exist_yet_and_shows_values_as_they_were()
    {
        var world = await ExampleAsync();
        var person = await PersonAsync(world, "Then Person", new() { ["title"] = "Analyst" });
        var project = await ProjectAsync(world, "Then project");
        var at = await LatestAsync(world);
        await Task.Delay(250);

        await world.Records.UpdateAsync(person, TestModels.Person("Then Person", new() { ["title"] = "Director" }));
        var late = await PersonAsync(world, "Late Person");
        await world.Records.AddLinkAsync(person, "WORKS_ON", project);
        await world.Records.AddLinkAsync(late, "WORKS_ON", project);

        var subgraph = await world.Graph.SubgraphAsync([person, project, late]);
        var view = await world.Timeline.AsOfAsync(subgraph.Nodes, subgraph.Edges, at);

        Assert.Equal([late], view.MissingNodes);
        Assert.Equal(2, view.MissingEdges.Count);
        Assert.Equal("Analyst", view.Rewound[person].Properties.Single(p => p.Key == "title").Value);
        Assert.DoesNotContain(project, view.Rewound.Keys);

        var now = await world.Timeline.AsOfAsync(subgraph.Nodes, subgraph.Edges, (await LatestAsync(world)).AddSeconds(1));
        Assert.Empty(now.MissingNodes);
        Assert.Empty(now.MissingEdges);
        Assert.Empty(now.Rewound);
        Assert.NotNull(await world.Timeline.EarliestAsync());
    }

    [Fact]
    public void A_model_can_hide_home_panels_in_its_configuration()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Models:one:Name"] = "One",
            ["Models:one:Home:Hide:0"] = "growth",
            ["Models:one:Home:Hide:1"] = "Imports",
            ["Models:two:Name"] = "Two",
        }).Build();

        var catalog = new Linkbase.Core.Models.ModelCatalog(configuration);

        var one = catalog.Find("one")!;
        Assert.False(one.ShowsOnHome(HomeRepository.Growth));
        Assert.False(one.ShowsOnHome(HomeRepository.Imports));
        Assert.True(one.ShowsOnHome(HomeRepository.Changes));
        Assert.True(catalog.Find("two")!.ShowsOnHome(HomeRepository.Growth));
    }
}
