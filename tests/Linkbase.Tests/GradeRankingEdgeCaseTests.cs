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

public class GradeRankingEdgeCaseTests
{
    [Theory]
    [InlineData(GradeSystem.UkTrad, "E2")]
    [InlineData(GradeSystem.UkTrad, "VS xx")]
    [InlineData(GradeSystem.Font, "f7A+")]
    [InlineData(GradeSystem.Yds, "5.10")]
    [InlineData(GradeSystem.V, "V3-")]
    [InlineData(GradeSystem.Australian, "22")]
    public void Ranks_grades_in_every_form(GradeSystem system, string grade) => Assert.NotNull(GradeRanking.Rank(system, grade));

    [Theory]
    [InlineData(GradeSystem.French, "easy")]
    [InlineData(GradeSystem.Yds, "hard")]
    [InlineData(GradeSystem.V, "hard")]
    [InlineData(GradeSystem.Uiaa, "abc")]
    [InlineData(GradeSystem.Uiaa, "XIII")]
    [InlineData(GradeSystem.Alpine, "nope")]
    [InlineData(GradeSystem.Alpine, "XX")]
    public void Leaves_unrecognised_grades_unranked(GradeSystem system, string grade) => Assert.Null(GradeRanking.Rank(system, grade));
}
