using System.Text;
using ProAdmin.Reports;

namespace ProAdmin.Tests;

public class ReportVersionTests
{
    private static readonly DateTimeOffset Day = new(2026, 9, 27, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void FirstPromoteOfTheDayIsTheDate() =>
        Assert.Equal("2026.09.27", ReportVersion.Next(Day, new[] { "v2026.09.20", "v1.0.0", "other" }));

    [Fact]
    public void NextPromotesOfTheDayGetSuffix()
    {
        Assert.Equal("2026.09.27.2", ReportVersion.Next(Day, new[] { "v2026.09.27" }));
        Assert.Equal("2026.09.27.4", ReportVersion.Next(Day, new[] { "v2026.09.27", "v2026.09.27.3", "v2026.09.26.9" }));
    }

    [Fact]
    public void ParsesOnlyDateTags()
    {
        Assert.True(ReportVersion.TryParseTag("v2026.09.27.2", out var v));
        Assert.Equal("2026.09.27.2", v);
        Assert.False(ReportVersion.TryParseTag("v1.0.3", out _));
        Assert.False(ReportVersion.TryParseTag("2026.09.27", out _));
    }

    [Fact]
    public void ComparesByDateThenNumber()
    {
        Assert.True(ReportVersion.Compare("2026.09.27.2", "2026.09.27") > 0);
        Assert.True(ReportVersion.Compare("2026.09.27", "2026.09.26.5") > 0);
        Assert.True(ReportVersion.Compare("2026.10.01", "2026.09.30.9") > 0);
        Assert.Equal(0, ReportVersion.Compare("2026.09.27", "2026.09.27.1"));
    }

    [Fact]
    public void ReasonAndHeadingCarryVersion()
    {
        Assert.Equal("2026.09.27 - Добавили отслеживание; Поправили шапку (55343)",
            ReportVersion.Reason("2026.09.27", new[] { "Добавили отслеживание", "Поправили шапку" }, " 55343 "));
        Assert.Equal("[2026.09.27] - 55343", ReportVersion.Heading("2026.09.27", "55343"));
        Assert.Equal("[2026.09.27]", ReportVersion.Heading("2026.09.27", null));
    }

    [Fact]
    public void ReportTextSeparatesScript()
    {
        var xml = "<?xml version=\"1.0\" encoding=\"utf-8\"?><XtraReportsLayoutSerializer ScriptsSource=\"int a = 1;&#xD;&#xA;if (a &lt; 2) {}\"><Bands /></XtraReportsLayoutSerializer>";
        var text = ReportText.Parse(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(xml)).ToArray());
        Assert.Equal("int a = 1;\r\nif (a < 2) {}", text.Script);
        Assert.DoesNotContain("int a", text.Markup);
        Assert.Equal("", ReportText.Parse(null).Script);
    }
}
