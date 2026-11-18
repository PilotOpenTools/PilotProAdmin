using ProAdmin.GitLab;

namespace ProAdmin.Jobs;

/// <summary>
/// Общие проверки автоматизаций и отчётов лежат в отдельном проекте: у репозитория настройка ci_config_path указывает на них.
/// Репозиторий, созданный в обход админки (например, модулем отчётов), получает её при первой публикации -
/// до создания Merge Request, иначе его pipeline не запустится.
/// Собственный .gitlab-ci.yml репозитория допустим, только если он подключает общие проверки (include проекта проверок):
/// иначе job вида «script: true» «проверит» что угодно, и общие проверки заменяют его.
/// </summary>
public static class CentralCi
{
    /// <summary>Действующий путь к конфигурации CI проекта; null - путь по умолчанию (.gitlab-ci.yml).</summary>
    public static async Task<string?> EnsureAsync(GitLabClient git, string project, string? current, string? configured, string branch,
        Action<string, string> log, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(current) || string.IsNullOrWhiteSpace(configured))
            return current;
        if (await git.FindFileAsync(project, ".gitlab-ci.yml", branch, ct) is { } own)
        {
            // Путь без «@» - локальный файл репозитория: подключить к нему свой CI нечем, свой файл не перекрывается.
            var central = configured.Contains('@') ? configured[(configured.IndexOf('@') + 1)..] : "";
            if (central.Length == 0 || IncludesProject(own.Text, central))
                return current;
            log("warn", $"В репозитории свой .gitlab-ci.yml без подключения общих проверок ({central}): путь конфигурации CI заменяется на {configured}.");
        }
        try
        {
            await git.SetCiConfigPathAsync(project, configured, ct);
            log("ok", $"Репозиторий подключён к общим проверкам: {configured}.");
            return configured;
        }
        catch (GitLabApiException ex)
        {
            log("warn", $"Не удалось подключить репозиторий к общим проверкам {configured}: {ex.Message}");
            return current;
        }
    }

    /// <summary>
    /// Есть ли в CI-файле строка <c>project: &lt;путь&gt;</c> (ключ include): комментарии и упоминание пути в чужих значениях не считаются.
    /// Разбор построчный: полноценный YAML-разборщик ради одной проверки в проект не тянем.
    /// </summary>
    internal static bool IncludesProject(string yaml, string project) =>
        yaml.Split('\n').Any(line =>
        {
            var t = line.Trim();
            if (t.StartsWith('-'))
                t = t[1..].TrimStart();
            if (!t.StartsWith("project:", StringComparison.Ordinal))
                return false;
            var value = t["project:".Length..];
            var hash = value.IndexOf(" #", StringComparison.Ordinal);
            if (hash >= 0)
                value = value[..hash];
            return string.Equals(value.Trim().Trim('\'', '"'), project, StringComparison.OrdinalIgnoreCase);
        });
}
