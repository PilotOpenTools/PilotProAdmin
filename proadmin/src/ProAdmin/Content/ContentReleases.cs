using ProAdmin.Automations;
using ProAdmin.GitLab;
using ProAdmin.Reports;
using ProAdmin.Settings;

namespace ProAdmin.Content;

/// <summary>
/// Релизы автоматизаций - та же схема, что у модулей (у отчётов стейджа нет; их release-ветки - от прежней схемы, только к удалению): <c>release/&lt;версия&gt;</c> от dev выкладывается на стейдж,
/// «На прод» переносит её в main и на продовый сервер. Версия: у автоматизаций - X.Y.Z из dev (её поднимает редактор),
/// у отчётов - дата (<see cref="ReportVersion"/>). Выпущенный релиз - тот, чья версия уже на проде (тег vX / версия main).
/// </summary>
public static class ContentReleases
{
    public const string Prefix = "release/";
    public const string StageKey = "stage";

    /// <summary>Сервер стейджа (настройка «Стейдж» - тот же, что у модулей); null - стейдж не задан, перенос dev / прод напрямую.</summary>
    public static EnvironmentSettings? StageEnv(AdminSettings settings) =>
        string.IsNullOrWhiteSpace(settings.StagePilotUrl) ? null
            : new EnvironmentSettings { Key = StageKey, Title = "Стейдж", PilotUrl = settings.StagePilotUrl, Branch = "" };

    /// <summary>
    /// Коммиты ветки, которых ещё нет на проде. Перенос на прод копирует файл в main, а не сливает ветки,
    /// поэтому compare main...dev видит и давно выпущенные коммиты - оставляем только сделанные после последнего коммита в main.
    /// </summary>
    public static List<GitLabCommit> NotOnProd(IEnumerable<GitLabCommit> commits, IEnumerable<GitLabBranch> branches)
    {
        var since = branches.FirstOrDefault(b => b.Name == AutomationRepositories.MainBranch)?.Commit?.CommittedDate;
        return commits.Where(c => since is null || c.CommittedDate is null || c.CommittedDate > since).ToList();
    }

    public static string Branch(string version) => Prefix + version;

    public static string? Version(string? branch) =>
        branch is not null && branch.StartsWith(Prefix, StringComparison.Ordinal) && branch.Length > Prefix.Length ? branch[Prefix.Length..] : null;

    /// <summary>Активный релиз автоматизации: release/X.Y.Z с версией выше main (самый новый).</summary>
    public static GitLabBranch? ActiveAutomationRelease(IEnumerable<GitLabBranch> branches, AutomationVersion? main) =>
        branches.Select(b => (Branch: b, Version: AutomationReleaseVersion(b.Name)))
            .Where(x => x.Version is { } v && (main is not { } m || v > m))
            .OrderByDescending(x => x.Version)
            .Select(x => x.Branch)
            .FirstOrDefault();

    /// <summary>Выпущенные release/X.Y.Z автоматизации (версия ≤ main) - можно удалить.</summary>
    public static List<GitLabBranch> ReleasedAutomationReleases(IEnumerable<GitLabBranch> branches, AutomationVersion? main) =>
        branches.Where(b => AutomationReleaseVersion(b.Name) is { } v && main is { } m && v <= m).ToList();

    /// <summary>release/1.4.0 / 1.4.0.</summary>
    public static AutomationVersion? AutomationReleaseVersion(string? branch) =>
        AutomationVersion.TryParseTag("v" + Version(branch), out var v) && Version(branch) is not null ? v : null;

    /// <summary>Активный релиз отчёта: release/&lt;дата&gt;, для которой ещё нет тега v&lt;дата&gt; (самый новый).</summary>
    public static GitLabBranch? ActiveReportRelease(IEnumerable<GitLabBranch> branches, IEnumerable<string> releasedVersions)
    {
        var released = releasedVersions.ToHashSet(StringComparer.Ordinal);
        return branches.Where(b => Version(b.Name) is { } v && ReportVersion.TryParseTag("v" + v, out _) && !released.Contains(v))
            .OrderByDescending(b => Version(b.Name)!, Comparer<string>.Create(ReportVersion.Compare))
            .FirstOrDefault();
    }

    /// <summary>Выпущенные release/&lt;дата&gt; отчёта (есть тег) - можно удалить.</summary>
    public static List<GitLabBranch> ReleasedReportReleases(IEnumerable<GitLabBranch> branches, IEnumerable<string> releasedVersions)
    {
        var released = releasedVersions.ToHashSet(StringComparer.Ordinal);
        return branches.Where(b => Version(b.Name) is { } v && released.Contains(v)).ToList();
    }
}
