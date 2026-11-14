using System.Collections.Concurrent;

namespace Linkbase.Core.Security;

// Who is asking, until sign-in says: the one user named by the Security:User setting, resolved by the model's own grant sources.
// With none configured the viewer is nobody, who sees nothing of a model that uses levels. Sign-in replaces this.
public sealed class ConfiguredViewerResolver(IConfiguration configuration, ILoggerFactory loggers, TimeProvider time) : IViewerResolver
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private readonly ConcurrentDictionary<string, ViewerDirectory> _directories = new();

    public async ValueTask<Viewer> ResolveAsync(ModelSecurity security, CancellationToken cancellationToken = default)
    {
        // One directory per security section, so its API cache is shared by every circuit.
        var directory = _directories.GetOrAdd(LabelCodec.ToJson(security) ?? "", _ =>
            ViewerDirectory.Create(security, _http, configuration, time, loggers.CreateLogger<ViewerDirectory>()));
        return await directory.ResolveAsync(configuration["Security:User"], cancellationToken);
    }
}
