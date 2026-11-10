using Linkbase.Core.Records;
using Linkbase.Data;
using Linkbase.Domains.Climbing.Data;
using Linkbase.Core.Graph;
using Linkbase.Domains.Climbing.Import;

namespace Linkbase.Tests;

public class GradeRankingTests
{
    [Theory]
    [InlineData(GradeSystem.UkTrad, "VD", "HVD")]
    [InlineData(GradeSystem.UkTrad, "S 4a", "HS 4a")]
    [InlineData(GradeSystem.UkTrad, "HVS 5a", "E1 5a")]
    [InlineData(GradeSystem.UkTrad, "E2 5b", "E2 5c")]
    [InlineData(GradeSystem.UkTrad, "E3 6a", "E10 6c")]
    [InlineData(GradeSystem.French, "6a", "6a+")]
    [InlineData(GradeSystem.French, "6a+", "6b")]
    [InlineData(GradeSystem.French, "6c+", "7a")]
    [InlineData(GradeSystem.French, "6a+ S1", "6b S1")]
    [InlineData(GradeSystem.Font, "f6A", "f6A+")]
    [InlineData(GradeSystem.Font, "f5+", "f6A")]
    [InlineData(GradeSystem.Yds, "5.9", "5.10a")]
    [InlineData(GradeSystem.Yds, "5.10a", "5.10b")]
    [InlineData(GradeSystem.Yds, "5.10a R", "5.10-")]
    [InlineData(GradeSystem.Yds, "5.10-", "5.10")]
    [InlineData(GradeSystem.Yds, "5.10", "5.10+")]
    [InlineData(GradeSystem.Yds, "5.11a", "5.11a/b")]
    [InlineData(GradeSystem.Yds, "5.11a/b", "5.11b")]
    [InlineData(GradeSystem.Yds, "5.10d", "5.11-")]
    [InlineData(GradeSystem.V, "V0-", "V0")]
    [InlineData(GradeSystem.V, "V2", "V2+")]
    [InlineData(GradeSystem.V, "V9", "V10")]
    [InlineData(GradeSystem.Nordic, "n6-", "n6")]
    [InlineData(GradeSystem.Australian, "19", "21")]
    [InlineData(GradeSystem.Uiaa, "VI-", "VI")]
    [InlineData(GradeSystem.Uiaa, "VII+", "VIII-")]
    [InlineData(GradeSystem.Alpine, "AD+ 4c", "D")]
    [InlineData(GradeSystem.Alpine, "TD- 6a+", "TD+ 6a")]
    public void Orders_grades_within_a_system(GradeSystem system, string easier, string harder)
    {
        var easierRank = GradeRanking.Rank(system, easier);
        var harderRank = GradeRanking.Rank(system, harder);
        Assert.NotNull(easierRank);
        Assert.NotNull(harderRank);
        Assert.True(easierRank < harderRank, $"{easier} = {easierRank}, {harder} = {harderRank}");
    }

    [Theory]
    [InlineData(GradeSystem.Scrambling, "Grade-3")]
    [InlineData(GradeSystem.UkTrad, "nonsense")]
    [InlineData(GradeSystem.French, "")]
    [InlineData(null, "VS")]
    public void Grades_it_cannot_rank_return_null(GradeSystem? system, string grade) =>
        Assert.Null(GradeRanking.Rank(system, grade));
}

public class NameMatchingTests
{
    [Theory]
    [InlineData("Red 13 Problem 3", "Red 11 Problem 7")]
    [InlineData("Blue 1 - Problem 11", "Blue 27, Problem 13")]
    [InlineData("Problem A", "Problem C")]
    [InlineData("B2, P16", "B2, P13")]
    [InlineData("Route 1", "Route 3")]
    [InlineData("Grey Arete 3", "Grey Arete 1")]
    public void Series_names_only_differ_by_numbering(string first, string second) =>
        Assert.True(NameMatching.OnlyNumberingDiffers(first, second));

    [Theory]
    [InlineData("Shadow Wind", "Wind Shadow")]
    [InlineData("Hanging Groove", "Hanging Groove Direct")]
    [InlineData("Planter", "Plaster")]
    [InlineData("Fawr Ridge", "Fach Ridge")]
    [InlineData("Two Ledges II", "Two Ledges")]
    public void Other_similar_names_differ_by_more_than_numbering(string first, string second) =>
        Assert.False(NameMatching.OnlyNumberingDiffers(first, second));
}
