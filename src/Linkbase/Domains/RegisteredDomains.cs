using Linkbase.Core;
using Linkbase.Domains.Climbing;

namespace Linkbase.Domains;

// The one place that says which domains this app is made of. Only the composition root (Program.cs) and GraphStyle's
// static style lookup read it; everything else is handed the DomainRegistry.
public static class RegisteredDomains
{
    public static readonly IReadOnlyList<IDomainModule> All = [new ClimbingDomain()];

    public static readonly DomainRegistry Registry = new(All);
}
