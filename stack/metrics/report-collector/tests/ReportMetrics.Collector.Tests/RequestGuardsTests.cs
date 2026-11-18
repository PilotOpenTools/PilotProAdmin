using ReportMetrics.Collector;
using Xunit;

namespace ReportMetrics.Collector.Tests;

public class RequestGuardsTests
{
    [Theory]
    [InlineData(null, null, true)]
    [InlineData("", "anything", true)]
    [InlineData("secret", "secret", true)]
    [InlineData("secret", "Secret", false)]
    [InlineData("secret", "secre", false)]
    [InlineData("secret", "secret2", false)]
    [InlineData("secret", "", false)]
    [InlineData("secret", null, false)]
    public void Checks_api_key(string? configured, string? provided, bool expected)
    {
        Assert.Equal(expected, RequestGuards.IsKeyValid(configured, provided));
    }

    [Theory]
    [InlineData(null, 24)]
    [InlineData(0, 24)]
    [InlineData(-5, 24)]
    [InlineData(1, 1)]
    [InlineData(RequestGuards.MaxSummaryHours, RequestGuards.MaxSummaryHours)]
    public void Summary_period_accepts_valid_hours(int? hours, int expectedHours)
    {
        Assert.True(RequestGuards.TryGetSummaryPeriod(hours, out var period));
        Assert.Equal(TimeSpan.FromHours(expectedHours), period);
    }

    [Theory]
    [InlineData(RequestGuards.MaxSummaryHours + 1)]
    [InlineData(100_000_000)]
    [InlineData(int.MaxValue)]
    public void Summary_period_rejects_huge_hours(int hours)
    {
        Assert.False(RequestGuards.TryGetSummaryPeriod(hours, out var period));
        Assert.True(period <= TimeSpan.FromHours(RequestGuards.MaxSummaryHours));
    }
}
