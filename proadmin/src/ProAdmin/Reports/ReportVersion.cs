using System.Text.RegularExpressions;
using ProAdmin.GitLab;

namespace ProAdmin.Reports;

/// <summary>
/// Номер версии отчёта - дата переноса на прод: <c>2026.09.27</c>, второй перенос за день - <c>2026.09.27.2</c>.
/// Номер нигде не хранится вручную: админка присваивает его при переносе (и откате) и пишет в тег/Release
/// <c>v2026.09.27</c> на коммите main, в заголовок раздела CHANGELOG.md и в причину версии отчёта в Pilot.
/// </summary>
public static partial class ReportVersion
{
    public static string TagName(string version) => "v" + version;

    /// <summary>Тег вида v2026.09.27 или v2026.09.27.2 / номер версии.</summary>
    public static bool TryParseTag(string? tag, out string version)
    {
        var m = tag is null ? Match.Empty : TagPattern().Match(tag);
        version = m.Success ? tag![1..] : "";
        return m.Success;
    }

    /// <summary>Следующий номер: дата (местная) и порядковый номер переноса за этот день, если он не первый.</summary>
    public static string Next(DateTimeOffset now, IEnumerable<string> tagNames)
    {
        var day = now.ToLocalTime().ToString("yyyy.MM.dd");
        var max = 0;
        foreach (var name in tagNames)
        {
            if (!TryParseTag(name, out var v))
                continue;
            if (v == day)
                max = Math.Max(max, 1);
            else if (v.StartsWith(day + ".", StringComparison.Ordinal) && int.TryParse(v[(day.Length + 1)..], out var n))
                max = Math.Max(max, n);
        }
        return max == 0 ? day : $"{day}.{max + 1}";
    }

    /// <summary>Сравнение номеров по частям: 2026.09.27.2 &gt; 2026.09.27 &gt; 2026.09.26.</summary>
    public static int Compare(string a, string b)
    {
        var x = Parts(a);
        var y = Parts(b);
        for (var i = 0; i < 4; i++)
            if (x[i] != y[i])
                return x[i].CompareTo(y[i]);
        return 0;
    }

    /// <summary>Год, месяц, день, номер за день (без номера - первый).</summary>
    private static int[] Parts(string version)
    {
        var p = version.Split('.');
        var result = new int[4];
        for (var i = 0; i < 4; i++)
            result[i] = i < p.Length && int.TryParse(p[i], out var n) ? n : i == 3 ? 1 : 0;
        return result;
    }

    /// <summary>Заголовок раздела CHANGELOG.md: «[2026.09.27] - TASK-1».</summary>
    public static string Heading(string version, string? task) =>
        string.IsNullOrWhiteSpace(task) ? $"[{version}]" : $"[{version}] - {task.Trim()}";

    /// <summary>Причина версии отчёта в Pilot: «2026.09.27 - пункт; пункт (TASK-1)».</summary>
    public static string Reason(string version, IEnumerable<string> notes, string? task)
    {
        var text = $"{version} - {string.Join("; ", notes)}" + (string.IsNullOrWhiteSpace(task) ? "" : $" ({task.Trim()})");
        return text.Length > 1000 ? text[..1000] + "…" : text;
    }

    /// <summary>Описание GitLab Release: что изменилось, задача, сервер, кто и когда.</summary>
    public static string ReleaseDescription(IEnumerable<string> notes, string? task, Settings.EnvironmentSettings server, Jobs.PublishJob job) =>
        "## Что изменилось\n\n" + string.Join("\n", notes.Select(n => "- " + n)) + "\n\n" +
        (string.IsNullOrWhiteSpace(task) ? "" : $"- Задача: {task.Trim()}\n") +
        $"- Сервер: {server.Title} ({server.PilotUrl})\n" +
        $"- Выполнил: {job.GitLabUser ?? job.PilotUser}\n" +
        $"- Дата: {DateTimeOffset.Now.ToLocalTime():dd.MM.yyyy HH:mm}";

    /// <summary>Релизы отчёта (теги с номером-датой), новые первыми.</summary>
    public static async Task<List<(GitLabTag Tag, string Version)>> ListReleasesAsync(GitLabClient git, string project, CancellationToken ct)
    {
        var result = new List<(GitLabTag, string)>();
        foreach (var tag in await git.ListVersionTagsAsync(project, ct) ?? new())
            if (TryParseTag(tag.Name, out var v))
                result.Add((tag, v));
        result.Sort((a, b) => Compare(b.Item2, a.Item2));
        return result;
    }

    /// <summary>
    /// Тег v{версия} и GitLab Release на текущем коммите main. Если коммит уже помечен тегом-датой
    /// (например, повторная запись того же содержимого на сервер), новый тег не создаётся.
    /// <paramref name="report"/> - .repx, записанный на прод: прикрепляется к релизу (раздел Assets).
    /// </summary>
    public static async Task<(string Level, string Text, string? Url)> TagReleaseAsync(GitLabClient git, string project,
        IReadOnlyList<(GitLabTag Tag, string Version)> releases, string version, string title, string description, CancellationToken ct,
        (string FileName, byte[] Content)? report = null)
    {
        var head = (await git.FindBranchAsync(project, "main", ct))?.Commit?.Id;
        if (head is null)
            return ("warn", "Тег не создан: не удалось прочитать ветку main.", null);
        var webUrl = (await git.FindProjectAsync(project, ct))?.WebUrl ?? "";

        var onHead = releases.FirstOrDefault(r => r.Tag.Commit?.Id == head);
        if (onHead.Tag is not null)
            return ("info", $"Коммит main уже помечен релизом {onHead.Tag.Name} - новый тег не нужен.", $"{webUrl}/-/releases/{onHead.Tag.Name}");

        var tag = TagName(version);
        await git.CreateTagAsync(project, tag, head, title, ct);
        await git.CreateReleaseAsync(project, tag, $"{tag} - {title}", description, ct);
        var text = $"Создан релиз {tag} (тег на коммите main) - к нему можно откатиться.";
        if (report is { } file)
        {
            try
            {
                await git.AddReleaseFileAsync(project, tag, file.FileName, file.Content, ct, "application/octet-stream", "other");
                text += $" Прикреплён {file.FileName}.";
            }
            catch (GitLabApiException ex)
            {
                return ("warn", $"{text} Файл {file.FileName} не прикреплён к релизу: {ex.Message}", $"{webUrl}/-/releases/{tag}");
            }
        }
        return ("ok", text, $"{webUrl}/-/releases/{tag}");
    }

    [GeneratedRegex(@"^v\d{4}\.\d{2}\.\d{2}(\.\d+)?$")]
    private static partial Regex TagPattern();
}
