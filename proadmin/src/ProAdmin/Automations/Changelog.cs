using System.Text;
using System.Text.RegularExpressions;
using ProAdmin.GitLab;

namespace ProAdmin.Automations;

/// <summary>Пункт раздела «Не выпущено»: <c>- 1.0.4 - текст _(27.09.2026, ivanov)_</c>.</summary>
public sealed partial record ChangelogEntry(string Text, AutomationVersion? Version = null, string? Meta = null)
{
    public string Render() =>
        "- " + (Version is { } v ? $"{v} - " : "") + Text + (string.IsNullOrWhiteSpace(Meta) ? "" : $" _({Meta})_");

    public static ChangelogEntry Parse(string line)
    {
        var m = EntryRegex().Match(line.Trim());
        if (!m.Success)
            return new ChangelogEntry(line.Trim());
        var version = m.Groups[1].Success ? AutomationVersion.Find("Version: " + m.Groups[1].Value) : null;
        return new ChangelogEntry(m.Groups[2].Value.Trim(), version, m.Groups[3].Success ? m.Groups[3].Value : null);
    }

    [GeneratedRegex(@"^[-*•]\s+(?:(\d+\.\d+\.\d+)\s+[---]\s+)?(.*?)(?:\s+_\((.*)\)_)?$")]
    private static partial Regex EntryRegex();
}

/// <summary>Выпущенный раздел: заголовок без «## » и текст как есть (ручные правки в GitLab сохраняются).</summary>
public sealed partial record ChangelogSection(string Heading, string Body)
{
    /// <summary>Версия из заголовка «[1.0.4] - 2026-09-27».</summary>
    public AutomationVersion? Version => HeadingVersion().Match(Heading) is { Success: true } m
        ? AutomationVersion.Find("Version: " + m.Groups[1].Value)
        : null;

    public IEnumerable<string> Notes => Changelog.SplitNotes(Body);

    public bool SameAs(ChangelogSection other) =>
        Normalize(Heading) == Normalize(other.Heading) && Normalize(Body) == Normalize(other.Body);

    private static string Normalize(string s) => WhiteSpace().Replace(s, " ").Trim();

    [GeneratedRegex(@"^\[v?(\d+\.\d+\.\d+)\]")]
    private static partial Regex HeadingVersion();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhiteSpace();
}

/// <summary>
/// CHANGELOG.md репозитория автоматизации или отчёта (по мотивам keepachangelog.com):
/// «Не выпущено» - изменения в dev (тест), ещё не перенесённые на прод; ниже - разделы переносов в main (прод), новые сверху.
/// Правило: выпущенные разделы берутся из main - main здесь источник правды.
/// </summary>
public sealed partial class Changelog
{
    public const string FileName = "CHANGELOG.md";
    private static readonly string[] UnreleasedTitles = { "не выпущено", "unreleased" };

    public string Preamble { get; private set; } = "";
    public List<ChangelogEntry> Unreleased { get; } = new();
    public List<ChangelogSection> Released { get; private set; } = new();

    public static string DefaultPreamble(string name) =>
        $"# Журнал изменений: {name}\n\n" +
        "Новые записи сверху. «Не выпущено» - изменения в ветке `dev` (тестовый сервер), которые ещё не перенесены на прод. " +
        "Остальные разделы - переносы в `main` (продовый сервер).";

    public static Changelog Parse(string? text, string name)
    {
        var log = new Changelog();
        var lines = AutomationFiles.NormalizeText(text).Split('\n');
        var i = 0;

        var preamble = new StringBuilder();
        for (; i < lines.Length && !lines[i].StartsWith("## "); i++)
            preamble.Append(lines[i]).Append('\n');
        log.Preamble = preamble.ToString().Trim().Length > 0 ? preamble.ToString().Trim() : DefaultPreamble(name);

        while (i < lines.Length)
        {
            var heading = lines[i][3..].Trim();
            var body = new List<string>();
            for (i++; i < lines.Length && !lines[i].StartsWith("## "); i++)
                body.Add(lines[i]);

            if (IsUnreleased(heading))
                log.Unreleased.AddRange(ParseEntries(body));
            else
                log.Released.Add(new ChangelogSection(heading, string.Join("\n", body).Trim()));
        }
        return log;
    }

    public string Render()
    {
        var sb = new StringBuilder(Preamble.TrimEnd()).Append("\n\n");
        if (Unreleased.Count > 0)
        {
            sb.Append("## [Не выпущено]\n\n");
            foreach (var e in Unreleased)
                sb.Append(e.Render()).Append('\n');
            sb.Append('\n');
        }
        foreach (var s in Released)
        {
            sb.Append("## ").Append(s.Heading).Append("\n\n");
            if (s.Body.Length > 0)
                sb.Append(s.Body).Append("\n\n");
        }
        return sb.ToString().TrimEnd() + "\n";
    }

    /// <summary>Новые пункты в начало «Не выпущено» (при отправке в dev).</summary>
    public void AddUnreleased(IEnumerable<string> notes, AutomationVersion? version, string? meta) =>
        Unreleased.InsertRange(0, notes.Select(n => new ChangelogEntry(n, version, meta)));

    /// <summary>Перенос на прод: новый раздел сверху, «Не выпущено» очищается.</summary>
    public void Release(string heading, IEnumerable<string> notes)
    {
        Unreleased.Clear();
        Released.Insert(0, new ChangelogSection(heading, string.Join("\n", notes.Select(n => "- " + n))));
    }

    /// <summary>
    /// Выпущенные разделы - как в main. Разделы, которых в main нет (например, перенос не завершился), возвращаются в «Не выпущено».
    /// Пункты с версией не выше версии main уже на проде - они убираются.
    /// </summary>
    public void SyncReleased(Changelog main, AutomationVersion? mainVersion)
    {
        foreach (var section in Released.Where(s => !main.Released.Any(m => m.SameAs(s))))
            Unreleased.AddRange(section.Notes.Select(n => new ChangelogEntry(n, section.Version)));
        Released = main.Released.ToList();

        if (mainVersion is { } mv)
            Unreleased.RemoveAll(e => e.Version is { } v && v <= mv);

        // Одинаковые пункты (например, после повторной синхронизации) - один раз.
        var seen = new HashSet<string>();
        Unreleased.RemoveAll(e => !seen.Add($"{e.Version}|{e.Text}"));
    }

    /// <summary>Заголовок раздела переноса: «[1.0.4] - 2026-09-27 - TASK-123» (у отчётов без версии - «[2026-09-27]»).</summary>
    public static string ReleaseHeading(AutomationVersion? version, DateTimeOffset date, string? task)
    {
        var day = date.ToLocalTime().ToString("yyyy-MM-dd");
        var heading = version is { } v ? $"[{v}] - {day}" : $"[{day}]";
        return string.IsNullOrWhiteSpace(task) ? heading : $"{heading} - {task.Trim()}";
    }

    /// <summary>Журнал нового репозитория; <paramref name="note"/> - первый раздел (например, импорт версии с прода).</summary>
    public static string Initial(string name, AutomationVersion? version, string? note)
    {
        var log = Parse(null, name);
        if (note is not null)
            log.Release(ReleaseHeading(version, DateTimeOffset.Now, null), new[] { note });
        return log.Render();
    }

    /// <summary>Текст из поля формы / пункты: строка = пункт, маркеры списка убираются.</summary>
    public static List<string> SplitNotes(string? text) =>
        AutomationFiles.NormalizeText(text).Split('\n')
            .Select(l => ListMarker().Replace(l.Trim(), ""))
            .Where(l => l.Length > 0)
            .ToList();

    private static bool IsUnreleased(string heading) =>
        UnreleasedTitles.Contains(heading.Trim('[', ']', ' ').ToLowerInvariant());

    private static IEnumerable<ChangelogEntry> ParseEntries(IEnumerable<string> body)
    {
        var current = new List<string>();
        foreach (var line in body.Where(l => l.Trim().Length > 0))
        {
            if (ListMarker().IsMatch(line) && !char.IsWhiteSpace(line[0]) || current.Count == 0)
                current.Add(line.Trim());
            else
                current[^1] += " " + line.Trim();
        }
        return current.Select(ChangelogEntry.Parse);
    }

    [GeneratedRegex(@"^[-*•]\s+")]
    private static partial Regex ListMarker();
}

/// <summary>Чтение журнала из GitLab: что ждёт переноса на прод.</summary>
public static class ChangelogSource
{
    /// <summary>
    /// Пункты для формы переноса на прод: «Не выпущено» из dev (сверенное с main).
    /// Если журнала нет или он пуст - заголовки коммитов dev после последнего изменения main.
    /// </summary>
    public static async Task<(List<string> Notes, string? Source)> PendingAsync(GitLabClient git, string project, string name,
        AutomationVersion? mainVersion, CancellationToken ct)
    {
        const string dev = AutomationRepositories.DevBranch, main = AutomationRepositories.MainBranch;

        var devFile = await git.FindFileAsync(project, Changelog.FileName, dev, ct);
        if (devFile is not null)
        {
            var mainFile = await git.FindFileAsync(project, Changelog.FileName, main, ct);
            var log = Changelog.Parse(devFile.Text, name);
            log.SyncReleased(Changelog.Parse(mainFile?.Text, name), mainVersion);
            if (log.Unreleased.Count > 0)
                return (log.Unreleased.Select(e => e.Text).Distinct().ToList(), $"раздел «Не выпущено» из {Changelog.FileName} ветки {dev}");
        }

        var since = (await git.FindBranchAsync(project, main, ct))?.Commit?.CommittedDate;
        var commits = await git.ListCommitsAsync(project, dev, since, ct);
        var notes = commits
            .Where(c => c.ParentIds is not { Count: > 1 } && (since is null || c.CommittedDate > since))
            .Select(c => StripName(c.Title ?? "", name))
            .Where(t => t.Length > 0 && !t.Contains(Changelog.FileName, StringComparison.OrdinalIgnoreCase))
            .Distinct()
            .ToList();
        return notes.Count > 0 ? (notes, $"коммиты ветки {dev}, которых нет в {main}") : (notes, null);
    }

    /// <summary>«Имя: TASK-1, текст» / «TASK-1, текст».</summary>
    public static string StripName(string title, string name)
    {
        title = title.Trim();
        return title.StartsWith(name + ":", StringComparison.OrdinalIgnoreCase) ? title[(name.Length + 1)..].Trim() : title;
    }
}
