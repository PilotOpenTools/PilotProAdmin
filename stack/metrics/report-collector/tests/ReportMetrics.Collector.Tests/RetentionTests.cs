using Microsoft.Extensions.Logging.Abstractions;
using ReportMetrics.Collector;
using Xunit;

namespace ReportMetrics.Collector.Tests;

public class RetentionTests
{
    private sealed class FakeTarget(string name, params object[] steps) : IRetentionTarget
    {
        private readonly Queue<object> _steps = new(steps);
        public string Name { get; } = name;
        public int Calls { get; private set; }
        public DateTimeOffset? Cutoff { get; private set; }

        public Task<int> DeleteOlderThanAsync(DateTimeOffset cutoff, int batch, CancellationToken ct)
        {
            Calls++;
            Cutoff = cutoff;
            var step = _steps.Count > 0 ? _steps.Dequeue() : 0;
            return step is Exception ex ? Task.FromException<int>(ex) : Task.FromResult((int)step);
        }
    }

    private static Task<int> Purge(params IRetentionTarget[] targets) =>
        RetentionService.PurgeAsync(targets, DateTimeOffset.UtcNow, 100, NullLogger.Instance, CancellationToken.None);

    [Theory]
    [InlineData(null, 30)]
    [InlineData("", 30)]
    [InlineData("  ", 30)]
    [InlineData("0", 0)]
    [InlineData("7", 7)]
    [InlineData("365", 365)]
    public void Parses_days(string? value, int expected) =>
        Assert.Equal(expected, RetentionService.ParseDays(value));

    [Theory]
    [InlineData("-1")]
    [InlineData("abc")]
    [InlineData("1.5")]
    public void Rejects_bad_days(string value) =>
        Assert.Throws<InvalidOperationException>(() => RetentionService.ParseDays(value));

    [Fact]
    public async Task Repeats_batches_until_a_short_one()
    {
        var t = new FakeTarget("t", 100, 100, 40);
        Assert.Equal(240, await Purge(t));
        Assert.Equal(3, t.Calls);
    }

    [Fact]
    public async Task Nothing_to_delete_is_one_call()
    {
        var t = new FakeTarget("t", 0);
        Assert.Equal(0, await Purge(t));
        Assert.Equal(1, t.Calls);
    }

    [Fact]
    public async Task Failure_of_one_table_does_not_stop_the_others()
    {
        var bad = new FakeTarget("bad", new InvalidOperationException("нет доступа"));
        var good = new FakeTarget("good", 7);
        Assert.Equal(7, await Purge(bad, good));
        Assert.Equal(1, good.Calls);
    }

    [Fact]
    public async Task Rows_deleted_before_a_failure_are_counted()
    {
        var t = new FakeTarget("t", 100, new InvalidOperationException("обрыв"));
        Assert.Equal(100, await Purge(t));
    }

    [Fact]
    public async Task Cancellation_stops_without_exception()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var t = new FakeTarget("t", new OperationCanceledException(cts.Token));
        Assert.Equal(0, await RetentionService.PurgeAsync([t], DateTimeOffset.UtcNow, 100, NullLogger.Instance, cts.Token));
    }

    [Fact]
    public void Retention_runs_on_indexed_time_column()
    {
        // Удаление идёт по ts: индекс на нём должен быть в DDL обеих таблиц.
        Assert.Contains("report_events_ts_idx", ReportEventStore.Schema);
        Assert.Contains("module_events_ts_idx", ModuleEventStore.Schema);
    }
}
