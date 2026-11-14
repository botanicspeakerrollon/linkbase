using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Linkbase.Core.Security;

// Where one part's grants for one user come from.
public interface IGrantSource
{
    // The values the user holds for the part. Empty if they hold none, or if the source couldn't be asked: a failure grants
    // nothing, so a viewer sees less and never more.
    Task<IReadOnlyList<string>> GetAsync(string userId, CancellationToken cancellationToken);
}

// Grants written in the model file's users table.
public sealed class ConfigGrantSource(ModelSecurity security, string partKey) : IGrantSource
{
    public Task<IReadOnlyList<string>> GetAsync(string userId, CancellationToken cancellationToken)
    {
        IReadOnlyList<string> grants = [];
        if (security.Users is not null && security.Users.TryGetValue(userId, out var user))
        {
            grants = partKey switch
            {
                ModelSecurity.LevelKey => user.Level is null ? [] : [user.Level],
                ModelSecurity.AnyOfKey => user.AnyOf ?? [],
                ModelSecurity.AllOfKey => user.AllOf ?? [],
                _ => [],
            };
        }

        return Task.FromResult(grants);
    }
}

// Grants fetched from a directory the app doesn't own. Answers are cached for the source's cacheSeconds and never served after
// that, so a revoked grant stops working within that time. A failed request is not cached and grants nothing.
public sealed class ApiGrantSource(GrantSource source, ApiResponseCache cache) : IGrantSource
{
    public async Task<IReadOnlyList<string>> GetAsync(string userId, CancellationToken cancellationToken)
    {
        var url = source.Url!.Replace("{user}", Uri.EscapeDataString(userId), StringComparison.Ordinal);
        var root = await cache.GetAsync(url, source.TokenSetting, source.CacheFor).WaitAsync(cancellationToken);
        return root is null ? [] : Extract(root.Value, source.Path!);
    }

    // The strings at a dotted path in the JSON: one string, or the strings of an array. Anything else is nothing.
    public static IReadOnlyList<string> Extract(JsonElement root, string path)
    {
        var node = root;
        foreach (var segment in path.Split('.'))
        {
            if (node.ValueKind != JsonValueKind.Object || !node.TryGetProperty(segment, out node))
            {
                return [];
            }
        }

        return node.ValueKind switch
        {
            JsonValueKind.String => [node.GetString()!],
            JsonValueKind.Array => node.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToList(),
            _ => [],
        };
    }
}

// Fetches JSON and keeps it. Parts that ask for the same address for the same time share one request and one entry, and so do
// users who ask at once. The bearer token is read from the app's settings when needed, and is never part of a key or a log line.
public sealed class ApiResponseCache(HttpClient http, IConfiguration configuration, TimeProvider time, ILogger? logger = null)
{
    private const int PruneAbove = 10_000;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    private sealed class Entry
    {
        public Task<JsonElement?> Result { get; set; } = null!;
        public DateTimeOffset? Expires { get; set; }
    }

    private readonly Dictionary<string, Entry> _entries = [];
    private readonly Lock _gate = new();
    private readonly ILogger _logger = logger ?? NullLogger.Instance;

    public Task<JsonElement?> GetAsync(string url, string? tokenSetting, TimeSpan cacheFor)
    {
        var key = $"{(long)cacheFor.TotalSeconds}|{url}";
        lock (_gate)
        {
            var now = time.GetUtcNow();
            if (_entries.TryGetValue(key, out var existing) && (existing.Expires is null || existing.Expires > now))
            {
                return existing.Result;
            }

            if (_entries.Count > PruneAbove)
            {
                foreach (var expired in _entries.Where(e => e.Value.Expires <= now).Select(e => e.Key).ToList())
                {
                    _entries.Remove(expired);
                }
            }

            var entry = new Entry();
            _entries[key] = entry;
            entry.Result = FetchAsync(key, entry, url, tokenSetting, cacheFor);
            return entry.Result;
        }
    }

    private async Task<JsonElement?> FetchAsync(string key, Entry entry, string url, string? tokenSetting, TimeSpan cacheFor)
    {
        // Yield first so the lock isn't held while the request is built and sent.
        await Task.Yield();
        var host = new Uri(url).Host;
        JsonElement? result = null;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Accept.ParseAdd("application/json");
            if (tokenSetting is not null)
            {
                var token = configuration[tokenSetting];
                if (string.IsNullOrEmpty(token))
                {
                    throw new InvalidOperationException($"The setting {tokenSetting} holds no token.");
                }

                request.Headers.Authorization = new("Bearer", token);
            }

            using var timeout = new CancellationTokenSource(RequestTimeout);
            using var response = await http.SendAsync(request, timeout.Token);
            response.EnsureSuccessStatusCode();
            await using var body = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var document = await JsonDocument.ParseAsync(body, cancellationToken: timeout.Token);
            result = document.RootElement.Clone();
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException or OperationCanceledException)
        {
            _logger.LogWarning("The grant directory at {Host} couldn't be read, so its grants are empty: {Reason}", host, ex.Message);
        }

        lock (_gate)
        {
            if (result is null || cacheFor <= TimeSpan.Zero)
            {
                if (_entries.TryGetValue(key, out var current) && ReferenceEquals(current, entry))
                {
                    _entries.Remove(key);
                }
            }
            else
            {
                entry.Expires = time.GetUtcNow() + cacheFor;
            }
        }

        return result;
    }
}

// Resolves a user's grants, from each part's own source, into a viewer. A part that can't be read gives nothing, so the viewer
// sees less, and a level that can't be read ranks below every item.
public sealed class ViewerDirectory(ModelSecurity security, IReadOnlyDictionary<string, IGrantSource> sources)
{
    public static ViewerDirectory Create(ModelSecurity security, HttpClient? http = null, IConfiguration? configuration = null,
        TimeProvider? time = null, ILogger? logger = null)
    {
        var sources = new Dictionary<string, IGrantSource>();
        ApiResponseCache? cache = null;
        foreach (var (key, part) in security.Parts)
        {
            if (part.Source is { Type: GrantSourceType.Api } api)
            {
                cache ??= new ApiResponseCache(http ?? new HttpClient(), configuration ?? new ConfigurationBuilder().Build(), time ?? TimeProvider.System, logger);
                sources[key] = new ApiGrantSource(api, cache);
            }
            else
            {
                sources[key] = new ConfigGrantSource(security, key);
            }
        }

        return new ViewerDirectory(security, sources);
    }

    public async Task<Viewer> ResolveAsync(string? userId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(userId))
        {
            return Viewer.Nobody;
        }

        var level = 0;
        if (security.HasLevel)
        {
            // If a directory returns several, the lowest counts, which is the cautious reading.
            var ranks = (await sources[ModelSecurity.LevelKey].GetAsync(userId, cancellationToken))
                .Select(security.LevelRank).Where(r => r >= 0).ToList();
            level = ranks.Count == 0 ? -1 : ranks.Min();
        }

        return new Viewer(level, await SetAsync(ModelSecurity.AnyOfKey, userId, cancellationToken), await SetAsync(ModelSecurity.AllOfKey, userId, cancellationToken));
    }

    private async Task<IEnumerable<string>> SetAsync(string key, string userId, CancellationToken cancellationToken) =>
        security.Part(key) is null
            ? []
            : (await sources[key].GetAsync(userId, cancellationToken)).Where(ModelSecurity.IsValidValue);
}

// The viewer for one user, resolved on first use and then kept, so a request or circuit asks the directory once.
public sealed class DirectoryViewerContext(ViewerDirectory directory, string? userId) : IViewerContext
{
    private Task<Viewer>? _viewer;

    public async ValueTask<Viewer> GetViewerAsync(CancellationToken cancellationToken = default) =>
        await (_viewer ??= directory.ResolveAsync(userId, cancellationToken));
}
