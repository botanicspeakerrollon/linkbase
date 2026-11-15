using Linkbase.Core.Models;
using Linkbase.Core.Records;
using Linkbase.Data;
using Microsoft.EntityFrameworkCore;

namespace Linkbase.Tests;

internal static class TestModels
{
    // A Person of the example consultancy, whose name is made from name_first and name_last. The
    // name is split at its last space, so "Zoë Adams" is Zoë and Adams, and "Joiner" is just a first name.
    public static RecordInput Person(string name, Dictionary<string, string?>? values = null, IEnumerable<NewLink>? links = null)
    {
        var given = values ?? [];
        var trimmed = name.Trim();
        var space = trimmed.LastIndexOf(' ');
        given["name_first"] = space < 0 ? trimmed : trimmed[..space];
        given["name_last"] = space < 0 ? null : trimmed[(space + 1)..];
        return new RecordInput(name, given) { Links = [.. links ?? []] };
    }

    // Loads a model file, the way ModelLoader does, and then drops the need for a reason with each change, which the loader
    // turns on unless the model file says otherwise. Most tests aren't about reasons. HistoryTests use the loader directly.
    public static async Task<ModelLoadSummary> LoadAsync(IDbContextFactory<LinkbaseDbContext> factory, string path, TextWriter output)
    {
        var summary = await new ModelLoader(factory, Linkbase.Domains.RegisteredDomains.Registry).LoadAsync(path, output);
        await using var db = factory.CreateDbContext();
        await db.Database.ExecuteSqlRawAsync("UPDATE model_setting SET require_reason = false");
        return summary;
    }
}
