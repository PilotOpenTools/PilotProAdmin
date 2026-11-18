using ProAdmin.Automations;

namespace ProAdmin.Tests;

public class ChangelogTests
{
    private static readonly AutomationVersion V103 = new(1, 0, 3);
    private static readonly AutomationVersion V104 = new(1, 0, 4);
    private static readonly DateTimeOffset Day = new(2026, 9, 27, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void NewFileGetsPreambleAndUnreleased()
    {
        var log = Changelog.Parse(null, "Уведомление");
        log.AddUnreleased(new[] { "Исправлен пустой «Шифр»" }, V103, "27.09.2026, ivanov");
        var text = log.Render();

        Assert.StartsWith("# Журнал изменений: Уведомление", text);
        Assert.Contains("## [Не выпущено]\n\n- 1.0.3 - Исправлен пустой «Шифр» _(27.09.2026, ivanov)_\n", text);
    }

    [Fact]
    public void RoundTripKeepsManualSections()
    {
        const string text = "# Мой журнал\n\nСвой текст.\n\n## [Не выпущено]\n\n- 1.0.4 - Правка _(27.09.2026, a)_\n- без версии\n  продолжение\n\n" +
                            "## [1.0.2] - 2026-09-20\n\n- Прод\n\nРучной абзац.\n";
        var log = Changelog.Parse(text, "X");

        Assert.Equal("# Мой журнал\n\nСвой текст.", log.Preamble);
        Assert.Equal(2, log.Unreleased.Count);
        Assert.Equal(V104, log.Unreleased[0].Version);
        Assert.Equal("Правка", log.Unreleased[0].Text);
        Assert.Equal("без версии продолжение", log.Unreleased[1].Text);
        Assert.Contains("Ручной абзац.", log.Render());
        Assert.Equal(log.Render(), Changelog.Parse(log.Render(), "X").Render());
    }

    [Fact]
    public void ReleaseMovesNotesToVersionSection()
    {
        var log = Changelog.Parse("## [Не выпущено]\n\n- 1.0.4 - a\n\n## [1.0.2] - 2026-09-20\n\n- old\n", "X");
        log.Release(Changelog.ReleaseHeading(V104, Day, "TASK-1"), new[] { "Итог для бизнеса" });
        var text = log.Render();

        Assert.DoesNotContain("## [Не выпущено]", text);
        Assert.Contains("## [1.0.4] - 2026-09-27 - TASK-1\n\n- Итог для бизнеса\n\n## [1.0.2] - 2026-09-20", text);
    }

    [Fact]
    public void SyncTakesReleasedFromMainAndDropsShippedVersions()
    {
        var dev = Changelog.Parse("## [Не выпущено]\n\n- 1.0.4 - new\n- 1.0.3 - shipped\n\n## [1.0.1] - 2026-09-01\n\n- x\n", "X");
        var main = Changelog.Parse("## [1.0.3] - 2026-09-27\n\n- Итог\n\n## [1.0.1] - 2026-09-01\n\n- x\n", "X");

        dev.SyncReleased(main, V103);

        Assert.Equal(new[] { "new" }, dev.Unreleased.Select(e => e.Text));
        Assert.Equal(new[] { "[1.0.3] - 2026-09-27", "[1.0.1] - 2026-09-01" }, dev.Released.Select(s => s.Heading));
    }

    [Fact]
    public void SectionMissingInMainReturnsToUnreleased()
    {
        // Отчёт: раздел закоммичен в dev, но перенос не завершился - в main его нет.
        var dev = Changelog.Parse("## [2026-09-27]\n\n- Новая колонка\n- Фильтр\n\n## [2026-09-01]\n\n- old\n", "R");
        var main = Changelog.Parse("## [2026-09-01]\n\n- old\n", "R");

        dev.SyncReleased(main, null);

        Assert.Equal(new[] { "Новая колонка", "Фильтр" }, dev.Unreleased.Select(e => e.Text));
        Assert.Single(dev.Released);
    }

    [Fact]
    public void SplitNotesStripsMarkersAndBlankLines()
    {
        Assert.Equal(new[] { "a", "b", "c" }, Changelog.SplitNotes("- a\r\n\n* b\n  • c  \n"));
    }

    [Fact]
    public void ReportHeadingHasNoVersion() =>
        Assert.Equal("[2026-09-27]", Changelog.ReleaseHeading(null, Day, null));

    [Fact]
    public void StripNameRemovesAutomationPrefix() =>
        Assert.Equal("TASK-1, текст", ChangelogSource.StripName("Имя: TASK-1, текст", "Имя"));
}
