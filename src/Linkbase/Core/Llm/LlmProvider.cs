using Linkbase.Core.Llm.Providers;

namespace Linkbase.Core.Llm;

// One language-model provider, as the app's startup sees it: an id to pick it by and the services that make it work. It plays
// the part for providers that IDomainModule plays for domains.
public abstract class LlmProvider
{
    // The Llm:Provider setting that picks this provider.
    public abstract string Id { get; }

    // Registers an ILlmToolClient. Called only for the provider that is picked.
    public abstract void AddServices(IServiceCollection services, IConfiguration configuration);
}

// Every provider the app knows. Adding one here is the last step of linking a new provider in.
public static class LlmProviders
{
    public const string DefaultId = "anthropic";

    public static IReadOnlyList<LlmProvider> All { get; } = [new AnthropicProvider(), new OpenAiProvider()];

    // Registers the client of the provider that Llm:Provider names ("anthropic" when it isn't set).
    public static void AddLlm(this IServiceCollection services, IConfiguration configuration)
    {
        var id = (configuration["Llm:Provider"] ?? DefaultId).Trim().ToLowerInvariant();
        var provider = All.FirstOrDefault(p => p.Id == id)
            ?? throw new InvalidOperationException($"Llm:Provider is '{id}', which isn't one of: {string.Join(", ", All.Select(p => p.Id))}.");
        provider.AddServices(services, configuration);
    }
}
