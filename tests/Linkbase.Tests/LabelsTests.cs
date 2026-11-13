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

public class LabelsTests
{
    [Theory]
    [InlineData(AscentStyle.TopRope, "Top rope")]
    [InlineData(AscentStyle.Dnf, "DNF")]
    [InlineData(AscentStyle.Onsight, "Onsight")]
    public void Styles_read_naturally(AscentStyle style, string expected) => Assert.Equal(expected, Labels.Of(style));

    [Theory]
    [InlineData(null, "")]
    [InlineData(GradeSystem.UkTrad, "UK trad")]
    [InlineData(GradeSystem.UkTech, "UK tech")]
    [InlineData(GradeSystem.Yds, "YDS")]
    [InlineData(GradeSystem.V, "V grade")]
    [InlineData(GradeSystem.Uiaa, "UIAA")]
    [InlineData(GradeSystem.ViaFerrata, "Via ferrata")]
    [InlineData(GradeSystem.French, "French")]
    public void Grade_systems_read_naturally(GradeSystem? system, string expected) => Assert.Equal(expected, Labels.Of(system));

    [Fact]
    public void Disciplines_read_naturally()
    {
        Assert.Equal("", Labels.Of((Discipline?)null));
        Assert.Equal("Trad", Labels.Of(Discipline.Trad));
    }

    [Theory]
    [InlineData(DatePrecision.Day, "Exact day", "5 Apr 2012")]
    [InlineData(DatePrecision.Month, "Month only", "Apr 2012")]
    [InlineData(DatePrecision.Year, "Year only", "2012")]
    public void Dates_show_only_what_is_known(DatePrecision precision, string label, string date)
    {
        Assert.Equal(label, Labels.Of(precision));
        Assert.Equal(date, Labels.Date(new DateOnly(2012, 4, 5), precision));
    }

    [Fact]
    public void Counts_and_stars()
    {
        Assert.Equal("1 route", Labels.Count(1, "route"));
        Assert.Equal("2 routes", Labels.Count(2, "route"));
        Assert.Equal("★★", Labels.Stars(2));
        Assert.Equal("", Labels.Stars(0));
        Assert.Equal("", Labels.Stars(null));
    }
}
