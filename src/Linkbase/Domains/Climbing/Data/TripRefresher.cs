using Linkbase.Data;
using Linkbase.Domains.Climbing.Import;

namespace Linkbase.Domains.Climbing.Data;

// Keeps trips up to date after an ascent changes. Without the corrections file, a rebuild would dissolve
// the multi-country trips, so it's skipped and logged instead.
public sealed class TripRefresher(TripService trips, Func<string> correctionsPath, ILogger<TripRefresher> logger)
{
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        var path = correctionsPath();
        if (!File.Exists(path))
        {
            logger.LogWarning("Trips weren't recalculated because {Path} is missing", path);
            return;
        }

        await trips.RebuildAsync(Corrections.Load(path).TripMerges, cancellationToken);
    }
}
