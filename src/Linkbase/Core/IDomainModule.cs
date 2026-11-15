using Linkbase.Core.Explore;

namespace Linkbase.Core;

public sealed record NavItem(string Href, string Title, string Icon, bool ExactMatch = false);

// A way of importing that belongs to a domain, shown as a tab on the Import page. Panel is a component with no parameters.
// UsesAi is for one that sends text to the configured language model, which is hidden when Extraction:Enabled is false.
public sealed record ImportSource(string Title, Type Panel, bool UsesAi = false);

// What a domain, such as climbing, adds to the app. The core knows nothing else about it.
public interface IDomainModule
{
    // How a model's configuration refers to this domain, such as Climbing.
    string Key { get; }

    string Name { get; }

    string Icon { get; }

    IReadOnlyList<NavItem> NavItems { get; }

    // Its own ways of importing, beside the CSV file every model has.
    IReadOnlyList<ImportSource> ImportSources { get; }

    // The address of this domain's page in the user guide, linked from the guide's front page. Null if it has none.
    string? GuideHref { get; }

    // How its types of node look and behave in Explore. Other types get a default.
    IReadOnlyList<LabelStyle> LabelStyles { get; }

    // Types whose name is another node's, so free text skips the name rather than matching every one of them.
    IReadOnlyList<string> LabelsNamedAfterParent { get; }

    // Properties too long to make good search suggestions.
    IReadOnlyList<string> LongTextProperties { get; }

    void AddServices(IServiceCollection services, IConfiguration configuration);

    // Runs a command-line command if it's this domain's, and returns its exit code. Otherwise returns null.
    Task<int?> TryRunCommandAsync(string[] args, IServiceProvider services, IConfiguration configuration);
}
