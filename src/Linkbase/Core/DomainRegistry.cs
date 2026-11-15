namespace Linkbase.Core;

// The domain modules this app is made of, as the core sees them. The composition root builds one from the modules it
// links in (Linkbase.Domains.RegisteredDomains) and registers it, so nothing in the core reaches for a domain by name.
public sealed class DomainRegistry
{
    public DomainRegistry(IEnumerable<IDomainModule> modules)
    {
        All = modules.ToList();
        LabelsNamedAfterParent = All.SelectMany(d => d.LabelsNamedAfterParent).ToArray();
        LongTextProperties = All.SelectMany(d => d.LongTextProperties).ToArray();
    }

    public IReadOnlyList<IDomainModule> All { get; }

    public IReadOnlyList<string> Keys => All.Select(d => d.Key).ToList();

    public string[] LabelsNamedAfterParent { get; }

    public string[] LongTextProperties { get; }

    // The domains a model uses, by key.
    public IEnumerable<IDomainModule> Used(IReadOnlyCollection<string> keys) => All.Where(d => keys.Contains(d.Key));
}
