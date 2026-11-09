using System.Text.RegularExpressions;
using Linkbase.Core;
using Microsoft.EntityFrameworkCore;

namespace Linkbase.Tests;

// scripts/rehearse-recovery.ps1 needs Docker and the real database, so it can't run here. Its SQL can: this runs the same
// text against the made-up logbook, so a column or view that goes missing shows up in the tests rather than in a recovery.
[Trait("Category", "Database")]
public sealed class RehearsalScriptTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static string Script => File.ReadAllText(RepoPaths.Resolve("scripts/rehearse-recovery.ps1"));

    // The script's own text, which is fixed, run as it is.
    private static Task<List<string>> DigestAsync(Linkbase.Data.LinkbaseDbContext db, string sql)
    {
        var wrapped = string.Concat("SELECT d AS \"Value\" FROM (", sql.Trim().TrimEnd(';'), ") q(d)");
#pragma warning disable EF1002
        return db.Database.SqlQueryRaw<string>(wrapped).ToListAsync();
#pragma warning restore EF1002
    }

    [Fact]
    public async Task The_digest_query_runs_and_changes_when_an_ascent_does()
    {
        var digestSql = Regex.Match(Script, @"\$digestSql = @'\r?\n(?<sql>.*?)\r?\n'@", RegexOptions.Singleline).Groups["sql"].Value;
        Assert.Contains("climbing_crag_country", digestSql);
        var store = await MadeUpLogbook.CreateAsync(fixture);
        await using var db = store.CreateDbContext();

        var before = await DigestAsync(db, digestSql);
        await db.Nodes.Where(n => n.Type == "Ascent").Take(1).ExecuteUpdateAsync(s => s.SetProperty(n => n.Props, "{\"date\": \"2020-05-02\", \"date_precision\": \"day\", \"style\": \"flash\", \"source\": \"ukc\"}"));
        var after = await DigestAsync(db, digestSql);

        Assert.Matches("^[0-9a-f]{32}$", Assert.Single(before));
        Assert.NotEqual(before, after);
    }

    [Fact]
    public async Task The_count_queries_run_for_every_climbing_type()
    {
        var store = await MadeUpLogbook.CreateAsync(fixture);
        await using var db = store.CreateDbContext();

        foreach (var type in new[] { "Area", "Crag", "Route", "Ascent", "Partner", "Trip" })
        {
            Assert.Matches(@"\$climbing = .*'" + type + "'", Script);
            Assert.True(await db.Nodes.CountAsync(n => n.Type == type) > 0, type);
        }

        Assert.Contains("climbing', 'import', 'default', 'all', '--fresh', '--same-database'", Script);
    }
}
