using Linkbase.Core;
using Linkbase.Domains.Climbing;

namespace Linkbase.Domains;

// The one place that says which domains this app is made of.
public static class RegisteredDomains
{
    public static readonly IReadOnlyList<IDomainModule> All = [new ClimbingDomain()];

    public static readonly string[] LabelsNamedAfterParent = All.SelectMany(d => d.LabelsNamedAfterParent).ToArray();

    public static readonly string[] LongTextProperties = All.SelectMany(d => d.LongTextProperties).ToArray();
}
