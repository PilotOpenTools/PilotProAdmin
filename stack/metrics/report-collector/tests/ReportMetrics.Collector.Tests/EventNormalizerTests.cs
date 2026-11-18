using ReportMetrics.Collector;
using Xunit;

namespace ReportMetrics.Collector.Tests;

public class EventNormalizerTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 26, 12, 0, 0, TimeSpan.Zero);

    private static EventNormalizer Create(string map = "stage=stage;test=test;*=prod") => new(new EnvResolver(map));

    private static ReportEventDto Valid() => new()
    {
        RunId = Guid.NewGuid(),
        Ts = Now.AddMinutes(-1),
        Report = "  Реестр НОПРИЗ ",
        Status = "Success",
        DurationMs = 1200,
        Pages = 3,
        Server = "https://pilot-stage.example.com/PilotDb",
    };

    [Fact]
    public void Normalizes_valid_event()
    {
        Assert.True(Create().TryNormalize(Valid(), Now, out var row, out _));
        Assert.Equal("Реестр НОПРИЗ", row!.Report);
        Assert.Equal("success", row.Status);
        Assert.Equal("stage", row.Env);
        Assert.Equal(Now.AddMinutes(-1), row.Ts);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("done")]
    public void Rejects_unknown_status(string? status)
    {
        var dto = Valid();
        dto.Status = status;
        Assert.False(Create().TryNormalize(dto, Now, out _, out var error));
        Assert.Contains("status", error);
    }

    [Fact]
    public void Rejects_empty_run_id_and_report()
    {
        var noId = Valid();
        noId.RunId = Guid.Empty;
        Assert.False(Create().TryNormalize(noId, Now, out _, out _));

        var noReport = Valid();
        noReport.Report = " ";
        Assert.False(Create().TryNormalize(noReport, Now, out _, out _));
    }

    [Fact]
    public void Keeps_late_events_from_offline_queue()
    {
        var dto = Valid();
        dto.Ts = Now.AddDays(-3);
        Assert.True(Create().TryNormalize(dto, Now, out var row, out _, clientSentAt: Now));
        Assert.Equal(Now.AddDays(-3), row!.Ts);
    }

    [Fact]
    public void Corrects_client_clock_skew_by_send_time()
    {
        // Часы клиента отстают на 2 часа: событие было 5 минут назад, но по его часам - 2ч05м назад.
        var dto = Valid();
        dto.Ts = Now.AddHours(-2).AddMinutes(-5);
        Assert.True(Create().TryNormalize(dto, Now, out var row, out _, clientSentAt: Now.AddHours(-2)));
        Assert.Equal(Now.AddMinutes(-5), row!.Ts);
    }

    [Fact]
    public void Ignores_small_skew()
    {
        var dto = Valid();
        Assert.True(Create().TryNormalize(dto, Now, out var row, out _, clientSentAt: Now.AddSeconds(-30)));
        Assert.Equal(dto.Ts, row!.Ts);
    }

    [Theory]
    [InlineData(1)]      // из будущего
    [InlineData(-100)]   // слишком старое
    public void Falls_back_to_receive_time_for_impossible_timestamps(int days)
    {
        var dto = Valid();
        dto.Ts = Now.AddDays(days);
        Assert.True(Create().TryNormalize(dto, Now, out var row, out _));
        Assert.Equal(Now, row!.Ts);
    }

    [Fact]
    public void Drops_negative_numbers_and_clips_long_strings()
    {
        var dto = Valid();
        dto.DurationMs = -5;
        dto.Pages = -1;
        dto.ErrorMessage = new string('x', 5000);
        Assert.True(Create().TryNormalize(dto, Now, out var row, out _));
        Assert.Null(row!.DurationMs);
        Assert.Null(row.Pages);
        Assert.Equal(2000, row.ErrorMessage!.Length);
    }

    [Theory]
    [InlineData("https://pilot-stage.example.com/db", "stage")]
    [InlineData("http://pilot-test.example.com:5545/db", "test")]
    [InlineData("http://pilot-prod.example.com:5545/db", "prod")]
    [InlineData(null, "prod")]
    public void Resolves_env_by_first_matching_rule(string? server, string env)
    {
        Assert.Equal(env, new EnvResolver("stage=stage;test=test;*=prod").Resolve(server));
    }

    [Fact]
    public void Env_is_unknown_without_fallback()
    {
        Assert.Equal("unknown", new EnvResolver("test=test").Resolve("http://prod"));
        Assert.Equal("unknown", new EnvResolver(null).Resolve("http://prod"));
    }

    [Fact]
    public void Rejects_null_event()
    {
        Assert.False(Create().TryNormalize(null, Now, out var row, out var error));
        Assert.Null(row);
        Assert.NotNull(error);
    }

    [Fact]
    public void Error_text_clips_and_flattens_foreign_status()
    {
        var dto = Valid();
        dto.Status = new string('x', 500) + "\r\nfake log line";
        Assert.False(Create().TryNormalize(dto, Now, out _, out var error));
        Assert.True(error!.Length < 120);
        Assert.DoesNotContain('\n', error);
        Assert.DoesNotContain('\r', error);
    }
}
