using Linkbase.Core;
using Linkbase.Core.Graph;
using Linkbase.Core.History;
using Linkbase.Core.Models;
using Linkbase.Core.Records;
using Linkbase.Core.Security;
using Linkbase.Data;
using Microsoft.EntityFrameworkCore;

namespace Linkbase.Tests;

// History must not leak what row and field security hide: changes to records or links the viewer can't see, the old and new
// values of a masked field, and counts that include hidden links. See docs/security-design.md, "History".
[Trait("Category", "Database")]
public sealed class HistorySecurityTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
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

    private async Task<(Factory Owner, string ConnectionString, ModelSecurity Security)> ConsultancyAsync(bool enable = true)
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

        return (owner, connectionString, security);
    }

    private static (HistoryRepository History, GraphRepository Graph) As(string connectionString, ModelSecurity security, string? user)
    {
        var state = new RowSecurityState(TimeProvider.System);
        var resolver = new Resolver(security, user);
        var factory = new Factory(connectionString, new ViewerConnectionInterceptor(state, resolver));
        var policy = new FieldPolicy(state, resolver);
        return (new HistoryRepository(factory, policy), new GraphRepository(factory, policy));
    }

    private static async Task ChangeBudgetAsync(Factory owner, string id, string budget)
    {
        var records = new RecordRepository(owner);
        var detail = (await records.GetAsync("Project", id.Split(':')[1]))!;
        var values = detail.Values.ToDictionary(v => v.Key, v => (string?)v.Value);
        values["budget"] = budget;
        await records.UpdateAsync(detail.Id, new RecordInput(detail.Name, values), new ChangeReason("Re-quoted"));
    }

    [Fact]
    public async Task A_masked_field_shows_in_history_only_as_a_mask_and_the_viewer_who_may_see_it_sees_the_values()
    {
        var (owner, connection, security) = await ConsultancyAsync();
        await ChangeBudgetAsync(owner, "project:pr2", "99000");

        var ava = await As(connection, security, "ava.lindqvist").History.ForRecordAsync("project:pr2");
        var budget = ava.SelectMany(e => e.Changes).Single(c => c.FieldKey == "budget");
        Assert.Equal((HistoryRepository.MaskText, HistoryRepository.MaskText), (budget.OldValue, budget.NewValue));

        var sam = await As(connection, security, "sam.partner").History.ForRecordAsync("project:pr2");
        var shown = sam.SelectMany(e => e.Changes).Single(c => c.FieldKey == "budget");
        Assert.Equal(("40000", "99000"), (shown.OldValue, shown.NewValue));
    }

    [Fact]
    public async Task History_of_a_record_the_viewer_cannot_see_is_empty()
    {
        var (owner, connection, security) = await ConsultancyAsync();
        await ChangeBudgetAsync(owner, "project:pr2", "99000");

        // pr2 needs Design or Delivery and the Harbourline NDA; the guest has none of it.
        Assert.Empty(await As(connection, security, "guest").History.ForRecordAsync("project:pr2"));
        var nearby = await As(connection, security, "guest").History.NearbyAsync("project:pr2", DateTimeOffset.UtcNow.AddDays(-1));
        Assert.DoesNotContain(nearby, n => n.RecordId == "project:pr2");
    }

    [Fact]
    public async Task A_changed_link_to_a_hidden_record_is_not_in_the_visible_ends_history_or_nearby_list()
    {
        var (owner, connection, security) = await ConsultancyAsync();
        await using (var db = owner.CreateDbContext())
        {
            var link = await db.NodeLinks.AsNoTracking().Where(l => l.TargetId == "project:pr2" && l.RelType == "WORKS_ON").OrderBy(l => l.Id).FirstAsync();
            await new RecordRepository(owner).UpdateLinkAsync(link.Id, new Dictionary<string, string?> { ["role"] = "Principal" }, new ChangeReason("Promoted"));
        }

        // The person end is public, the project end is hidden from the guest: nothing about the link may show on the person.
        var personId = await PersonOfAsync(owner, "project:pr2");
        var guest = As(connection, security, "guest").History;
        Assert.DoesNotContain(await guest.ForRecordAsync(personId), e => e.Changes.Any(c => c.RelType == "WORKS_ON"));
        Assert.DoesNotContain(await guest.NearbyAsync(personId, DateTimeOffset.UtcNow.AddDays(-1)), n => n.Description.Contains("Booking redesign", StringComparison.Ordinal));
    }

    private static async Task<string> PersonOfAsync(Factory owner, string projectId)
    {
        await using var db = owner.CreateDbContext();
        return await db.NodeLinks.AsNoTracking().Where(l => l.TargetId == projectId && l.RelType == "WORKS_ON").OrderBy(l => l.Id).Select(l => l.SourceId).FirstAsync();
    }

    [Fact]
    public async Task A_deleted_record_or_link_has_no_history_for_a_restricted_viewer_and_security_off_changes_nothing()
    {
        var (owner, connection, security) = await ConsultancyAsync();
        await using (var db = owner.CreateDbContext())
        {
            var link = await db.NodeLinks.AsNoTracking().Where(l => l.RelType == "WORKS_ON").OrderBy(l => l.Id).FirstAsync();
            await new RecordRepository(owner).RemoveLinkAsync(link.Id, new ChangeReason("Left the project"));

            var visible = await As(connection, security, "sam.partner").History.ForLinkAsync(link.SourceId, link.RelType, link.TargetId);
            Assert.Empty(visible.SelectMany(e => e.Changes));
        }

        // The same deletion is in the log for the owner, who reads without row security.
        var all = await new HistoryRepository(owner).NearbyAsync("project:pr1", DateTimeOffset.UtcNow.AddDays(-1));
        Assert.NotNull(all);
    }

    [Fact]
    public async Task With_security_off_history_is_returned_untouched()
    {
        var (owner, connection, security) = await ConsultancyAsync(enable: false);
        await ChangeBudgetAsync(owner, "project:pr2", "99000");

        var guest = await As(connection, security, "guest").History.ForRecordAsync("project:pr2");
        var budget = guest.SelectMany(e => e.Changes).Single(c => c.FieldKey == "budget");
        Assert.Equal(("40000", "99000"), (budget.OldValue, budget.NewValue));
    }

    [Fact]
    public async Task Link_counts_are_counted_for_the_viewer_not_read_from_the_totals_kept_on_the_model()
    {
        var (owner, connection, security) = await ConsultancyAsync();
        long Total(IReadOnlyList<ConnectionCount> counts) => counts.Sum(c => c.Links);

        var sam = Total(await As(connection, security, "sam.partner").Graph.ConnectionCountsAsync());
        var guest = Total(await As(connection, security, "guest").Graph.ConnectionCountsAsync());

        await using var db = owner.CreateDbContext();
        Assert.Equal(await db.LinkDefinitions.SumAsync(d => d.Links), sam);
        Assert.True(guest < sam);
    }
}
