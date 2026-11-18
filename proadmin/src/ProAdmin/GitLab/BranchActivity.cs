using ProAdmin.Modules;

namespace ProAdmin.GitLab;

/// <summary>Ветка с коммитами сверх своей базы (или только что созданная): что это, куда идёт, сколько новых коммитов, её MR.</summary>
public sealed record ActiveBranch(GitLabBranch Branch, string Kind, string Base, int Ahead, GitLabMergeRequest? Mr);

/// <summary>Активные ветки репозитория и ветки, которые можно удалить.</summary>
public sealed class BranchActivityResult
{
    /// <summary>Свежие сверху.</summary>
    public List<ActiveBranch> Active { get; } = new();

    /// <summary>Без своих коммитов, из них уже влит MR - работа закончена.</summary>
    public List<GitLabBranch> Removable { get; } = new();
}

/// <summary>
/// В каких ветках есть новые коммиты. Для каждой ветки - база (куда она вливается): коммиты, которых нет в базе (без merge-коммитов),
/// и открытый MR. Ветка без своих коммитов считается влитой, только если из неё уже был влит MR; иначе она только что создана и в работе.
/// </summary>
public static class BranchActivity
{
    /// <param name="classify">Вид ветки и её база; null - ветку не показывать (main, dev и т.п. страница считает сама).</param>
    public static async Task<BranchActivityResult> LoadAsync(GitLabClient git, string project, IEnumerable<GitLabBranch> branches,
        IReadOnlyList<GitLabMergeRequest> openMrs, Func<GitLabBranch, (string Kind, string Base)?> classify, CancellationToken ct)
    {
        var result = new BranchActivityResult();
        foreach (var b in branches)
        {
            if (classify(b) is not { } c)
                continue;
            var mr = openMrs.FirstOrDefault(m => m.SourceBranch == b.Name);
            var target = mr?.TargetBranch ?? c.Base;
            var ahead = (await git.CompareCommitsAsync(project, target, b.Name, ct)).Count(x => x.ParentIds is not { Count: > 1 });
            if (ahead > 0 || mr is not null || !await git.IsBranchMergedAsync(project, b, ct))
                result.Active.Add(new ActiveBranch(b, c.Kind, target, ahead, mr));
            else
                result.Removable.Add(b);
        }
        Sort(result.Active);
        return result;
    }

    public static void Sort(List<ActiveBranch> branches) =>
        branches.Sort((a, b) => Nullable.Compare(b.Branch.Commit?.CommittedDate, a.Branch.Commit?.CommittedDate));

    /// <summary>«5 мин назад», «3 ч назад», «2 дня назад», дальше - дата.</summary>
    public static string Ago(DateTimeOffset? date)
    {
        if (date is not { } d) return "";
        var span = DateTimeOffset.Now - d;
        return span.TotalMinutes < 60 ? $"{Math.Max(1, (int)span.TotalMinutes)} мин назад"
             : span.TotalHours < 24 ? $"{(int)span.TotalHours} ч назад"
             : span.TotalDays < 30 ? ModuleNextSteps.Plural((int)span.TotalDays, "день", "дня", "дней") + " назад"
             : d.ToLocalTime().ToString("dd.MM.yyyy");
    }
}

/// <summary>Группа веток в «Подробнее» под «Можно удалить»; Deletable = false - только показать (удаляют в GitLab).</summary>
public sealed record BranchGroup(string Label, IReadOnlyList<GitLabBranch> Branches, bool Deletable = true);

/// <summary>
/// Таблица «Активные ветки» и строка «Можно удалить: N» (Pages/Shared/_BranchesPanel).
/// Кнопка «Удалить» шлёт POST в обработчик CleanupBranches текущей страницы с RouteValues.
/// </summary>
public sealed record BranchesPanel(IReadOnlyList<ActiveBranch> Active, IReadOnlyList<BranchGroup> Groups, string RepoUrl,
    bool CanDelete, IDictionary<string, string> RouteValues, Func<string, string?>? TaskUrl = null)
{
    public int Deletable => Groups.Where(g => g.Deletable).Sum(g => g.Branches.Count);
}
