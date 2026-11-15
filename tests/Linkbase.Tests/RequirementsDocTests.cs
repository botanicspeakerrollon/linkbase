using System.Text.RegularExpressions;
using Linkbase.Core;
using Xunit;

namespace Linkbase.Tests;

/// <summary>
/// Keeps docs/requirements.md from drifting away from the app. These can only check structure and the page list;
/// a changed behaviour still needs its requirement reworded by hand (see CLAUDE.md).
/// </summary>
public class RequirementsDocTests
{
    private static readonly string[] Statuses = ["Met", "Partial", "Planned", "Deferred", "Withdrawn"];

    private static readonly string Doc = File.ReadAllText(RepoPaths.Resolve("docs/requirements.md"));

    private static List<(string Id, string Status)> Requirements() =>
        Regex.Matches(Doc, @"^\| ([A-Z]+-\d+) \| [MSC] \|.*\| ([^|]+) \|\s*$", RegexOptions.Multiline)
            .Select(m => (m.Groups[1].Value, m.Groups[2].Value.Trim()))
            .ToList();

    [Fact]
    public void Requirement_ids_are_unique()
    {
        var duplicates = Requirements().GroupBy(r => r.Id).Where(g => g.Count() > 1).Select(g => g.Key);
        Assert.Empty(duplicates);
    }

    [Fact]
    public void Every_requirement_has_a_known_status()
    {
        var bad = Requirements()
            .Where(r => !Statuses.Any(s => r.Status.StartsWith(s, StringComparison.Ordinal)))
            .Select(r => $"{r.Id}: {r.Status}");
        Assert.Empty(bad);
    }

    [Fact]
    public void Every_requirement_group_is_in_the_traceability_appendix()
    {
        var appendix = Doc[Doc.IndexOf("Appendix A", StringComparison.Ordinal)..];
        var missing = Requirements()
            .Select(r => r.Id.Split('-')[0])
            .Distinct()
            .Where(g => !Regex.IsMatch(appendix, $@"^\| [^|]*\b{g}\b", RegexOptions.Multiline));
        Assert.Empty(missing);
    }

    [Fact]
    public void Every_requirement_mentioned_in_the_text_exists()
    {
        var ids = Requirements().Select(r => r.Id).ToHashSet();
        var unknown = Regex.Matches(Doc, @"\b(?:DAT|FUN|REC|IMP|HIS|MOD|UI|CLN|TIM|CLM|PERF|SEC|REL|USE|OPS|MNT|DEM|INT)-\d+\b")
            .Select(m => m.Value)
            .Distinct()
            .Where(id => !ids.Contains(id));
        Assert.Empty(unknown);
    }

    [Fact]
    public void Every_page_route_in_the_app_is_listed_in_the_pages_table()
    {
        var routes = Directory.EnumerateFiles(RepoPaths.Resolve("src"), "*.razor", SearchOption.AllDirectories)
            .SelectMany(f => Regex.Matches(File.ReadAllText(f), @"^@page ""([^""]+)""", RegexOptions.Multiline)
                .Select(m => m.Groups[1].Value))
            .Distinct()
            .ToList();

        Assert.NotEmpty(routes);
        var missing = routes.Where(r => !Doc.Contains($"| `{r}` |", StringComparison.Ordinal));
        Assert.Empty(missing);
    }

    [Fact]
    public void Every_route_in_the_pages_table_is_a_real_page()
    {
        var real = Directory.EnumerateFiles(RepoPaths.Resolve("src"), "*.razor", SearchOption.AllDirectories)
            .SelectMany(f => Regex.Matches(File.ReadAllText(f), @"^@page ""([^""]+)""", RegexOptions.Multiline)
                .Select(m => m.Groups[1].Value))
            .ToHashSet();
        var listed = Regex.Matches(Doc, @"^\| `(/[^`]*)` \| [^|]+ \| [^|]+ \|\s*$", RegexOptions.Multiline)
            .Select(m => m.Groups[1].Value);

        var unknown = listed.Where(r => !real.Contains(r)).ToList();
        Assert.Empty(unknown);
    }
}
