using System.Collections.Concurrent;

namespace Linkbase.Core.Security;

// One viewer directory per security section, kept for the life of the app so that an API source's cache is shared by every
// circuit and request.
public sealed class ViewerDirectories(IConfiguration configuration, ILoggerFactory loggers, TimeProvider time, IEnumerable<ICustomGrantProvider> providers)
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private readonly ConcurrentDictionary<string, ViewerDirectory> _directories = new();

    public ViewerDirectory For(ModelSecurity security) =>
        _directories.GetOrAdd(LabelCodec.ToJson(security) ?? "", _ =>
            ViewerDirectory.Create(security, _http, configuration, time, loggers.CreateLogger<ViewerDirectory>(), providers));
}

// Who is asking: the current user (see ICurrentUserAccessor), resolved into a viewer by the model's own grant sources. With no
// user the viewer is nobody, who sees nothing of a model that uses levels.
public sealed class DirectoryViewerResolver(ICurrentUserAccessor user, ViewerDirectories directories) : IViewerResolver
{
    public async ValueTask<Viewer> ResolveAsync(ModelSecurity security, CancellationToken cancellationToken = default) =>
        await directories.For(security).ResolveAsync(await user.GetAsync(cancellationToken), cancellationToken);
}
