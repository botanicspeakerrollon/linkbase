using Linkbase.Core.Models;

namespace Linkbase.Tests;

public class TypeLookTests
{
    [Fact]
    public void Every_icon_it_can_pick_exists()
    {
        Assert.All(TypeLook.AllIcons, icon => Assert.True(TypeLook.Exists(icon), icon));
    }

    [Fact]
    public void The_icon_follows_the_name_and_colours_are_not_repeated()
    {
        List<string?> colors = [];
        List<string?> icons = [];
        foreach (var name in new[] { "Customer", "Company", "Account", "Trade", "Alert", "Widget", "Gadget" })
        {
            var (color, icon) = TypeLook.Pick(name, colors, icons);
            colors.Add(color);
            icons.Add(icon);
        }

        Assert.Equal(["Person", "Business", "AccountBalance", "SwapHoriz", "NotificationImportant"], icons.Take(5));
        Assert.Equal(colors.Count, colors.Distinct().Count());
        Assert.Equal(icons.Count, icons.Distinct().Count());
    }

    [Fact]
    public void A_full_palette_wraps_instead_of_failing()
    {
        var (color, icon) = TypeLook.Pick("Thing", Enumerable.Range(0, 30).Select(i => (string?)$"#00000{i % 10}"), []);
        Assert.StartsWith("#", color);
        Assert.NotEmpty(icon);
    }
}
