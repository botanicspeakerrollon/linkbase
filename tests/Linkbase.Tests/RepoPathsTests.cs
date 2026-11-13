using Linkbase.Core.Extraction;
using Linkbase.Core;
using System.Net;
using System.Text;
using System.Text.Json;
using Linkbase.Data;
using Linkbase.Domains.Climbing.Data;
using Linkbase.Core.Graph;
using Linkbase.Domains.Climbing.Features;
using Linkbase.Core.Explore;
using Linkbase.Domains.Climbing.Import;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Linkbase.Tests;

public class RepoPathsTests
{
    [Fact]
    public void Finds_the_repo_root_from_inside_it()
    {
        var path = RepoPaths.Resolve("data/corrections.json");
        Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(path)!)!, "Linkbase.sln")));
    }

    [Fact]
    public void Says_so_when_started_outside_the_repo() =>
        Assert.Throws<DirectoryNotFoundException>(() => RepoPaths.Resolve("x", Path.GetTempPath()));
}
