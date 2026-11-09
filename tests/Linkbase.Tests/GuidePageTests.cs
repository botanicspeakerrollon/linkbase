using Bunit;
using Linkbase.Components.Pages;
using Linkbase.Domains.Climbing.Features.Guide;
using Xunit;

namespace Linkbase.Tests;

[Trait("Category", "Database")]
public class GuidePageTests(PostgresFixture fixture) : UiTestBase(fixture), IClassFixture<PostgresFixture>
{
    // The words on the page, as a reader sees them, without the markup's escaping.
    private static string Text(IRenderedComponent<UserGuide> cut) => cut.Find("article").TextContent;

    [Fact]
    public void The_general_guide_has_a_section_for_every_entry_in_its_contents()
    {
        var cut = RenderPage<UserGuide>();

        foreach (var (id, title) in UserGuide.SectionIds)
        {
            Assert.NotNull(cut.Find($"section#{id}"));
            Assert.Contains($"href=\"/guide#{id}\"", cut.Markup);
            Assert.Contains(title, Text(cut));
        }
    }

    [Fact]
    public void The_general_guide_lists_the_same_shortcuts_as_the_shortcuts_dialog()
    {
        var cut = RenderPage<UserGuide>();

        foreach (var (_, keys) in Linkbase.Components.Layout.HotkeysDialog.Sections)
        {
            foreach (var (_, what) in keys)
            {
                Assert.Contains(what, Text(cut));
            }
        }
    }

    [Fact]
    public void The_general_guide_points_to_the_climbing_guide_when_the_model_has_the_climbing_domain()
    {
        var cut = RenderPage<UserGuide>();

        Assert.Contains("href=\"/guide/climbing\"", cut.Markup);
    }

    [Fact]
    public void The_climbing_guide_has_a_section_for_every_entry_in_its_contents()
    {
        var cut = RenderPage<ClimbingGuidePage>();

        foreach (var (id, _) in ClimbingGuidePage.Contents)
        {
            Assert.NotNull(cut.Find($"section#{id}"));
            Assert.Contains($"href=\"/guide/climbing#{id}\"", cut.Markup);
        }
    }
}
