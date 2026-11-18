using ReportMetrics.Collector;
using Xunit;

namespace ReportMetrics.Collector.Tests;

public class ModuleEventNormalizerTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 27, 12, 0, 0, TimeSpan.Zero);

    private static ModuleEventNormalizer Create() => new(new EnvResolver(":5549=stage;:5547=test;*=prod"));

    private static ModuleEventDto Valid() => new()
    {
        EventId = Guid.NewGuid(),
        Ts = Now.AddSeconds(-5),
        Kind = "action",
        Module = " 087OpenStoradge.ext2 ",
        ModuleTitle = "01.087 - Открыть Storadge - (OpenStoradge) - (CICD)",
        ModuleVersion = "1.0.1.0",
        Action = " Открыть ",
        Status = "Error",
        DurationMs = 800,
        Items = 12,
        ErrorType = "System.IO.IOException",
        Server = "http://localhost:5547/pilot-ice_ru",
    };

    [Fact]
    public void Normalizes_action()
    {
        Assert.True(Create().TryNormalize(Valid(), Now, out var row, out _));
        Assert.Equal("087OpenStoradge.ext2", row!.Module);
        Assert.Equal("Открыть", row.Action);
        Assert.Equal("error", row.Status);
        Assert.Equal("test", row.Env);
        Assert.Equal(12, row.Items);
    }

    [Fact]
    public void Load_event_needs_no_action_and_defaults_title()
    {
        var dto = Valid();
        dto.Kind = "load";
        dto.Action = null;
        dto.Status = null;
        dto.ModuleTitle = null;
        dto.Server = "http://localhost:5549/pilot-ice_ru";

        Assert.True(Create().TryNormalize(dto, Now, out var row, out _));
        Assert.Equal("load", row!.Kind);
        Assert.Null(row.Action);
        Assert.Equal("success", row.Status);
        Assert.Equal("087OpenStoradge.ext2", row.ModuleTitle);
        Assert.Equal("stage", row.Env);
    }

    [Theory]
    [InlineData("module")]
    [InlineData("action")]
    [InlineData("kind")]
    [InlineData("status")]
    [InlineData("id")]
    public void Rejects_invalid(string broken)
    {
        var dto = Valid();
        switch (broken)
        {
            case "module": dto.Module = " "; break;
            case "action": dto.Action = null; break;
            case "kind": dto.Kind = "click"; break;
            case "status": dto.Status = "ok"; break;
            case "id": dto.EventId = Guid.Empty; break;
        }
        Assert.False(Create().TryNormalize(dto, Now, out _, out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void Negative_numbers_are_dropped()
    {
        var dto = Valid();
        dto.DurationMs = -1;
        dto.Items = -3;
        Assert.True(Create().TryNormalize(dto, Now, out var row, out _));
        Assert.Null(row!.DurationMs);
        Assert.Null(row.Items);
    }

    [Fact]
    public void Rejects_null_event()
    {
        Assert.False(Create().TryNormalize(null, Now, out var row, out var error));
        Assert.Null(row);
        Assert.NotNull(error);
    }

    [Fact]
    public void Error_text_clips_and_flattens_foreign_kind()
    {
        var dto = Valid();
        dto.Kind = new string('k', 500) + "\nfake";
        Assert.False(Create().TryNormalize(dto, Now, out _, out var error));
        Assert.True(error!.Length < 120);
        Assert.DoesNotContain('\n', error);
    }
}
