using System.Text.RegularExpressions;
using Linkbase.Core;

namespace Linkbase.Tests;

// Every place that adds, changes or removes records or links has to write the audit trail in the same transaction, or say here
// why it doesn't. This reads the source, so a new write path that forgets the recorder fails here rather than leaving a gap in
// the history. ExecuteUpdate and ExecuteDelete are the easy ones to miss, because they skip SaveChanges.
public sealed partial class ChangeRecorderCoverageTests
{
    // Files that write records or links without using ChangeRecorder, and why that's right.
    private static readonly Dictionary<string, string> Exempt = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Domains/Climbing/Data/ClimbingRecordHook.cs"] = "Renames ascents after their route, which is derived from the route's own logged rename, and leaves updated_at alone.",
    };

    [GeneratedRegex(@"(\bdb\.(Nodes|NodeLinks)\b[^;{}]*?\.(Add|AddRange|Remove|ExecuteDeleteAsync|ExecuteUpdateAsync)\b|\b(INSERT\s+INTO|DELETE\s+FROM|UPDATE|TRUNCATE)\s+(TABLE\s+)?(node|node_link)\b)", RegexOptions.Singleline)]
    private static partial Regex WritesRecords();

    [Fact]
    public void Every_code_path_that_writes_records_or_links_records_the_change()
    {
        var root = RepoPaths.Resolve("src/Linkbase");
        var missing = new List<string>();
        foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (relative.StartsWith("Data/Migrations/", StringComparison.Ordinal) || relative.StartsWith("obj/", StringComparison.Ordinal) || relative.StartsWith("bin/", StringComparison.Ordinal))
            {
                continue;
            }

            var source = File.ReadAllText(file);
            if (WritesRecords().IsMatch(source) && !source.Contains("ChangeRecorder", StringComparison.Ordinal) && !Exempt.ContainsKey(relative))
            {
                missing.Add(relative);
            }
        }

        Assert.True(missing.Count == 0, "These write records or links without recording the change: " + string.Join(", ", missing));
    }

    [Fact]
    public void The_exemptions_are_still_needed()
    {
        var root = RepoPaths.Resolve("src/Linkbase");
        foreach (var (relative, _) in Exempt)
        {
            Assert.True(File.Exists(Path.Combine(root, relative)), $"{relative} is exempt but no longer exists. Remove it from the list.");
            Assert.True(WritesRecords().IsMatch(File.ReadAllText(Path.Combine(root, relative))), $"{relative} no longer writes records. Remove it from the list.");
        }
    }
}
