using Linkbase.Core.Models;
using Linkbase.Data;
using Microsoft.EntityFrameworkCore;

namespace Linkbase.Tests;

internal static class TestModels
{
    // Loads a model file, the way ModelLoader does, and then drops the need for a reason with each change, which the loader
    // turns on unless the model file says otherwise. Most tests aren't about reasons. HistoryTests use the loader directly.
    public static async Task<ModelLoadSummary> LoadAsync(IDbContextFactory<LinkbaseDbContext> factory, string path, TextWriter output)
    {
        var summary = await new ModelLoader(factory).LoadAsync(path, output);
        await using var db = factory.CreateDbContext();
        await db.Database.ExecuteSqlRawAsync("UPDATE model_setting SET require_reason = false");
        return summary;
    }
}
