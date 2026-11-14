namespace Linkbase.Core.Security;

// What a person has been granted, resolved against a model's security parts. A level is a rank in the model's list, so that
// comparing is a number against a number. Policies and the field policy only ever see this, never where it came from.
public sealed class Viewer(int level, IEnumerable<string> anyOf, IEnumerable<string> allOf)
{
    // -1 matches no label's level, so a viewer without a level sees nothing of a model that uses levels.
    public int Level { get; } = level;
    public IReadOnlySet<string> AnyOf { get; } = new HashSet<string>(anyOf, StringComparer.Ordinal);
    public IReadOnlySet<string> AllOf { get; } = new HashSet<string>(allOf, StringComparer.Ordinal);

    // Nobody: sees only what has no restrictions, and not even that when the model uses levels.
    public static Viewer Nobody { get; } = new(-1, [], []);
}

// What an item, link or property requires. The default label, no requirements at all, is visible to every viewer.
public sealed class Label(int level, IEnumerable<string>? anyOf = null, IEnumerable<string>? allOf = null)
{
    // The rank of the lowest level that may see it.
    public int Level { get; } = level;

    // The viewer needs any one of these. Empty means no restriction.
    public IReadOnlySet<string> AnyOf { get; } = new HashSet<string>(anyOf ?? [], StringComparer.Ordinal);

    // The viewer needs every one of these. Empty means no restriction.
    public IReadOnlySet<string> AllOf { get; } = new HashSet<string>(allOf ?? [], StringComparer.Ordinal);

    public static Label Open { get; } = new(0);
}

// Who is asking. Everything that reads a model with security on takes the viewer from here. Sign-in will supply the user
// later; until then, implementations come from tests and the grant directory.
public interface IViewerContext
{
    ValueTask<Viewer> GetViewerAsync(CancellationToken cancellationToken = default);
}

// A viewer that never changes, for tests and for a model with security off.
public sealed class FixedViewerContext(Viewer viewer) : IViewerContext
{
    public ValueTask<Viewer> GetViewerAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(viewer);
}
