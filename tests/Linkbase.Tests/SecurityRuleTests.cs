using Linkbase.Core;
using Linkbase.Core.Models;
using Linkbase.Core.Security;
using Linkbase.Data;

namespace Linkbase.Tests;

// The visibility rule, the model file's security section and who-has-what, with no database. The same rule is later checked
// against the database policies, so these are the specification. See docs/security-design.md.
public sealed class SecurityRuleTests
{
    // The bank of the design document: Classification (level), Department (any of) and Project (all of).
    private static readonly ModelSecurity Bank = new()
    {
        Level = new SecurityPart { Name = "Classification", Values = ["public", "private"] },
        AnyOf = new SecurityPart { Name = "Department" },
        AllOf = new SecurityPart { Name = "Project" },
    };

    private static Viewer Who(int level, string[]? any = null, string[]? all = null) => new(level, any ?? [], all ?? []);

    private static Label Item(int level, string[]? any = null, string[]? all = null) => new(level, any, all);

    [Fact]
    public void The_example_from_the_design_needs_the_level_either_department_and_both_projects()
    {
        var itemA = Item(0, ["Finance", "HR"], ["ProjX", "ProjY"]);

        Assert.True(VisibilityRule.CanSee(Bank, Who(0, ["Finance"], ["ProjX", "ProjY"]), itemA));
        Assert.True(VisibilityRule.CanSee(Bank, Who(1, ["HR"], ["ProjX", "ProjY", "ProjZ"]), itemA));
        Assert.True(VisibilityRule.CanSee(Bank, Who(0, ["Finance", "HR"], ["ProjX", "ProjY"]), itemA));

        Assert.False(VisibilityRule.CanSee(Bank, Who(0, ["Legal"], ["ProjX", "ProjY"]), itemA), "neither department");
        Assert.False(VisibilityRule.CanSee(Bank, Who(0, ["Finance"], ["ProjX"]), itemA), "only one project");
        Assert.False(VisibilityRule.CanSee(Bank, Who(0, ["Finance"], []), itemA), "no projects");
        Assert.False(VisibilityRule.CanSee(Bank, Who(-1, ["Finance"], ["ProjX", "ProjY"]), itemA), "no clearance");
    }

    [Fact]
    public void A_higher_level_needs_at_least_that_clearance()
    {
        var secret = Item(1);

        Assert.False(VisibilityRule.CanSee(Bank, Who(0), secret));
        Assert.True(VisibilityRule.CanSee(Bank, Who(1), secret));
        Assert.True(VisibilityRule.CanSee(Bank, Who(1), Item(0)), "someone cleared for private also sees public");
    }

    [Fact]
    public void An_item_with_no_departments_or_projects_is_unrestricted_on_those_parts()
    {
        Assert.True(VisibilityRule.CanSee(Bank, Who(0), Item(0)));
        Assert.True(VisibilityRule.CanSee(Bank, Who(0, ["Finance"]), Item(0, null, [])));
        Assert.True(VisibilityRule.CanSee(Bank, Who(0), Label.Open));
    }

    [Fact]
    public void Nobody_sees_nothing_when_the_model_uses_levels_and_only_unrestricted_items_when_it_does_not()
    {
        Assert.False(VisibilityRule.CanSee(Bank, Viewer.Nobody, Label.Open));

        var sets = new ModelSecurity { AnyOf = new SecurityPart { Name = "Team" } };
        Assert.True(VisibilityRule.CanSee(sets, Viewer.Nobody, Label.Open));
        Assert.False(VisibilityRule.CanSee(sets, Viewer.Nobody, Item(0, ["Red"])));
    }

    [Fact]
    public void A_part_the_model_does_not_use_is_never_checked()
    {
        var onlyAny = new ModelSecurity { AnyOf = new SecurityPart { Name = "Team" } };
        var viewer = Who(-1, ["Red"], []);

        // The label asks for a level and projects the viewer lacks, but this model has neither.
        Assert.True(VisibilityRule.CanSee(onlyAny, viewer, Item(5, ["Red"], ["Secret"])));

        var onlyAll = new ModelSecurity { AllOf = new SecurityPart { Name = "Case" } };
        Assert.True(VisibilityRule.CanSee(onlyAll, Viewer.Nobody, Item(5, ["Red"])));
        Assert.False(VisibilityRule.CanSee(onlyAll, Viewer.Nobody, Item(0, null, ["Case 1"])));
    }

    [Fact]
    public void A_model_without_security_shows_everything()
    {
        Assert.True(VisibilityRule.CanSee(null, Viewer.Nobody, Item(9, ["x"], ["y"])));
        Assert.True(VisibilityRule.CanSee(new ModelSecurity(), Viewer.Nobody, Item(9, ["x"], ["y"])));
    }

    [Fact]
    public void A_link_needs_its_own_label_and_both_ends()
    {
        var viewer = Who(1, ["Finance"], ["ProjX"]);
        var open = Item(0);
        var hidden = Item(1, ["HR"]);

        Assert.True(VisibilityRule.CanSeeLink(Bank, viewer, open, open, open));
        Assert.False(VisibilityRule.CanSeeLink(Bank, viewer, hidden, open, open), "its own label");
        Assert.False(VisibilityRule.CanSeeLink(Bank, viewer, open, hidden, open), "the from end");
        Assert.False(VisibilityRule.CanSeeLink(Bank, viewer, open, open, hidden), "the to end");
    }

    [Fact]
    public void A_property_needs_its_record_its_definition_and_any_override_checked_separately()
    {
        var viewer = Who(1, ["Finance"]);
        var record = Item(0, ["Finance"]);
        var definition = Item(1, ["Finance", "HR"]);

        Assert.True(VisibilityRule.CanSeeProperty(Bank, viewer, record, definition, null));
        Assert.True(VisibilityRule.CanSeeProperty(Bank, viewer, record, null, null));

        // Two any-of lists are two separate tests: sharing Finance with one and HR with the other is not enough.
        var hr = Who(1, ["HR"]);
        Assert.False(VisibilityRule.CanSeeProperty(Bank, hr, record, definition, null), "the record doesn't pass");
        Assert.False(VisibilityRule.CanSeeProperty(Bank, viewer, record, definition, Item(1, ["HR"])), "the override doesn't pass");
        Assert.False(VisibilityRule.CanSeeProperty(Bank, viewer, record, Item(2), null), "the definition's level is too high");
    }

    // The rule against a differently written version of it, over many random labels and viewers.
    [Fact]
    public void The_rule_matches_a_plain_reading_of_the_design_for_every_combination_of_parts()
    {
        string[] pool = ["A", "B", "C", "D"];
        var random = new Random(20260514);
        string[] Some() => pool.Where(_ => random.Next(3) == 0).ToArray();

        for (var round = 0; round < 5_000; round++)
        {
            var useLevel = random.Next(2) == 0;
            var useAny = random.Next(2) == 0;
            var useAll = random.Next(2) == 0;
            if (!useLevel && !useAny && !useAll)
            {
                continue;
            }

            var security = new ModelSecurity
            {
                Level = useLevel ? new SecurityPart { Name = "L", Values = ["a", "b", "c"] } : null,
                AnyOf = useAny ? new SecurityPart { Name = "Y" } : null,
                AllOf = useAll ? new SecurityPart { Name = "Z" } : null,
            };
            var (viewerLevel, labelLevel) = (random.Next(-1, 3), random.Next(0, 3));
            var (viewerAny, labelAny, viewerAll, labelAll) = (Some(), Some(), Some(), Some());

            var expected = true;
            if (useLevel && viewerLevel < labelLevel)
            {
                expected = false;
            }

            if (useAny && labelAny.Length > 0 && !viewerAny.Any(v => labelAny.Contains(v)))
            {
                expected = false;
            }

            if (useAll && labelAll.Any(v => !viewerAll.Contains(v)))
            {
                expected = false;
            }

            Assert.Equal(expected, VisibilityRule.CanSee(security, Who(viewerLevel, viewerAny, viewerAll), Item(labelLevel, labelAny, labelAll)));
        }
    }

    // The consultancy example in models/consultancy, as the models doc shows it.
    [Fact]
    public async Task The_consultancy_example_gives_its_people_what_the_models_doc_says()
    {
        var model = ModelFile.Read(RepoPaths.Resolve("models/consultancy/model.json"));
        var security = model.Security!;
        var directory = ViewerDirectory.Create(security);

        var sam = await directory.ResolveAsync("sam.partner");
        var ava = await directory.ResolveAsync("ava.lindqvist");
        var chloe = await directory.ResolveAsync("chloe.marsh");
        var guest = await directory.ResolveAsync("guest");
        var stranger = await directory.ResolveAsync("someone.else");

        var budget = Item(security.LevelRank("confidential"));
        var bookingRedesign = Item(security.LevelRank("internal"), ["Design", "Delivery"], ["Harbourline"]);
        var twoClients = Item(security.LevelRank("internal"), null, ["Harbourline", "Greenleaf"]);
        var timetable = Item(security.LevelRank("public"));

        bool[] Sees(Label label) => [.. new[] { sam, ava, chloe, guest, stranger }.Select(v => VisibilityRule.CanSee(security, v, label))];

        //                                     sam   ava    chloe  guest  stranger
        Assert.Equal([true, false, false, false, false], Sees(budget));
        Assert.Equal([true, true, false, false, false], Sees(bookingRedesign));
        Assert.Equal([true, false, true, false, false], Sees(twoClients));
        Assert.Equal([true, true, true, true, false], Sees(timetable));
    }

    // Validation of the model file section.

    private static readonly string Folder = Path.Combine(Path.GetTempPath(), $"linkbase-security-{Guid.NewGuid():N}");

    private static ModelFile Read(string security)
    {
        Directory.CreateDirectory(Folder);
        var path = Path.Combine(Folder, $"{Guid.NewGuid():N}.json");
        File.WriteAllText(path, $$"""{ "name": "Test", "types": [ { "name": "Thing", "fields": [] } ]{{(security.Length == 0 ? "" : $", \"security\": {security}")}} }""");
        return ModelFile.Read(path);
    }

    private static string Problems(string security) =>
        Assert.Throws<LinkbaseValidationException>(() => Read(security)).Message;

    [Fact]
    public void A_model_file_can_leave_security_out_or_use_any_one_part()
    {
        Assert.Null(Read("").Security);
        Assert.True(Read("""{ "anyOf": { "name": "Team" } }""").Security!.Enabled);
        Assert.True(Read("""{ "level": { "name": "Sensitivity", "values": ["low", "high"] } }""").Security!.HasLevel);
    }

    [Fact]
    public void The_names_of_the_parts_are_the_models_own()
    {
        var security = Read("""{ "level": { "name": "Rating", "values": ["a"] }, "anyOf": { "name": "Guild" }, "allOf": { "name": "Case" } }""").Security!;

        Assert.Equal(["Rating", "Guild", "Case"], security.Parts.Select(p => p.Part.Name));
    }

    [Theory]
    [InlineData("{}", "at least one of level, anyOf and allOf")]
    [InlineData("""{ "level": { "name": "L" } }""", "needs values")]
    [InlineData("""{ "level": { "name": "L", "values": [] } }""", "needs values")]
    [InlineData("""{ "level": { "name": "L", "values": ["a", "A"] } }""", "more than once")]
    [InlineData("""{ "level": { "name": "L", "values": ["a,b"] } }""", "which needs 1 to 60 characters")]
    [InlineData("""{ "anyOf": { "name": "T", "values": ["*"] } }""", "which needs 1 to 60 characters")]
    [InlineData("""{ "anyOf": { "name": "T", "values": ["{x}"] } }""", "which needs 1 to 60 characters")]
    [InlineData("""{ "anyOf": { "name": "" } }""", "needs a name")]
    [InlineData("""{ "anyOf": { "name": "Same" }, "allOf": { "name": "same" } }""", "both called")]
    [InlineData("""{ "anyOf": { "name": "T", "source": { "type": "api" } } }""", "needs a url")]
    [InlineData("""{ "anyOf": { "name": "T", "source": { "type": "api", "url": "http://dir.example/{user}", "path": "x" } } }""", "full https address")]
    [InlineData("""{ "anyOf": { "name": "T", "source": { "type": "api", "url": "https://dir.example/{user}" } } }""", "needs a path")]
    [InlineData("""{ "anyOf": { "name": "T", "source": { "type": "api", "url": "https://dir.example/{user}", "path": "x", "cacheSeconds": -1 } } }""", "cacheSeconds")]
    [InlineData("""{ "anyOf": { "name": "T", "source": { "type": "config", "url": "https://dir.example/" } } }""", "can't have a url")]
    [InlineData("""{ "anyOf": { "name": "T" }, "users": { "u": { "level": "high" } } }""", "has no level part")]
    [InlineData("""{ "level": { "name": "L", "values": ["a"] }, "users": { "u": { "level": "z" } } }""", "isn't one of")]
    [InlineData("""{ "anyOf": { "name": "T" }, "users": { "u": { "allOf": ["x"] } } }""", "no allOf part")]
    [InlineData("""{ "anyOf": { "name": "T", "source": { "type": "api", "url": "https://dir.example/{user}", "path": "x" } }, "users": { "u": {} } }""", "would never be used")]
    public void A_bad_security_section_says_what_is_wrong(string security, string expected) =>
        Assert.Contains(expected, Problems(security));

    [Fact]
    public void Http_is_allowed_for_a_directory_on_this_machine()
    {
        var security = Read("""{ "anyOf": { "name": "T", "source": { "type": "api", "url": "http://localhost:5000/{user}", "path": "x" } } }""").Security!;

        Assert.Equal(GrantSourceType.Api, security.AnyOf!.Source!.Type);
        Assert.Equal(TimeSpan.FromSeconds(300), security.AnyOf.Source.CacheFor);
    }
}
