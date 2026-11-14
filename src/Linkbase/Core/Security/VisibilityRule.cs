namespace Linkbase.Core.Security;

// The one rule for what a viewer may see, written plainly in C#. The database policies and the field policy have to agree with
// it, and the tests check them against it. See docs/security-design.md.
//
// A model uses some of the three kinds of part, and a part it doesn't use is never checked. An item is visible only if every
// part the model uses passes:
//   level   the viewer's rank is at least the item's
//   any of  the item has no values, or shares at least one with the viewer
//   all of  the viewer has every one of the item's values
public static class VisibilityRule
{
    // Security off, or no security section: everything is visible.
    public static bool CanSee(ModelSecurity? security, Viewer viewer, Label label)
    {
        if (security is not { Enabled: true })
        {
            return true;
        }

        return (!security.HasLevel || label.Level <= viewer.Level)
            && (!security.HasAnyOf || label.AnyOf.Count == 0 || label.AnyOf.Overlaps(viewer.AnyOf))
            && (!security.HasAllOf || label.AllOf.IsSubsetOf(viewer.AllOf));
    }

    // A link is visible only if its own label passes and both its ends are visible, or it would show that a hidden record exists.
    public static bool CanSeeLink(ModelSecurity? security, Viewer viewer, Label link, Label from, Label to) =>
        CanSee(security, viewer, link) && CanSee(security, viewer, from) && CanSee(security, viewer, to);

    // A property is shown only if its record, its definition's default and any override on it all pass. Each is checked on its
    // own, because two any-of lists can't be merged into one without changing what they mean.
    public static bool CanSeeProperty(ModelSecurity? security, Viewer viewer, Label record, Label? definition, Label? overridden) =>
        CanSee(security, viewer, record)
        && (definition is null || CanSee(security, viewer, definition))
        && (overridden is null || CanSee(security, viewer, overridden));
}
