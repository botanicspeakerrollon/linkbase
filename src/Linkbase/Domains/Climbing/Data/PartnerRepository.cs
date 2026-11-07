using Linkbase.Data;
using Microsoft.EntityFrameworkCore;

namespace Linkbase.Domains.Climbing.Data;

public sealed class PartnerRepository(IDbContextFactory<LinkbaseDbContext> contextFactory)
{
    private const int SearchLimit = 20;

    public async Task<IReadOnlyList<string>> SearchNamesAsync(string? text, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var query = db.Partners.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(text))
        {
            var pattern = $"%{text.Trim()}%";
            query = query.Where(p => EF.Functions.ILike(p.Name, pattern));
        }

        return await query
            .OrderByDescending(p => p.Ascents.Count)
            .ThenBy(p => p.Name)
            .Take(SearchLimit)
            .Select(p => p.Name)
            .ToListAsync(cancellationToken);
    }
}
