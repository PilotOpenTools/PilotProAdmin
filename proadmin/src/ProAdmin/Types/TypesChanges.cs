using ProAdmin.GitLab;
using ProAdmin.Settings;

namespace ProAdmin.Types;

/// <summary>Где изменение типов: ветка types/… / dev (тест) / release (стейдж) / main (прод).</summary>
public enum TypesChangeStage
{
    Branch,
    Test,
    Stage,
    Prod,
}

/// <summary>Изменение типов: ветка в работе или коммит dev (squash ветки), где оно сейчас и с чем связано.</summary>
public sealed class TypesChangeEntry
{
    public GitLabCommit? Commit { get; init; }
    public GitLabBranch? Branch { get; init; }
    public TypesChangeStage Stage { get; init; }

    /// <summary>Новых коммитов в ветке относительно dev.</summary>
    public int Ahead { get; init; }

    public GitLabMergeRequest? Mr { get; init; }
    public List<string> Items { get; } = new();
    public List<TypesChangeLink> Links { get; } = new();

    public string Key => Commit?.Id ?? Branch!.Name;
    public string Title => Commit?.Title ?? Branch?.Commit?.Title ?? Branch?.Name ?? "";
    public DateTimeOffset? Date => Commit?.CommittedDate ?? Branch?.Commit?.CommittedDate;
    public string? Author => Commit?.AuthorName ?? Branch?.Commit?.AuthorName;
}

/// <summary>
/// Какие коммиты dev уже перенесены в release и main: у перенесённых cherry-pick'ом коммитов в сообщении есть
/// «(cherry picked from commit …)», у перенесённых в main - и sha dev, и sha release.
/// </summary>
public sealed class TypesRepoState
{
    private readonly HashSet<string> _inRelease;
    private readonly HashSet<string> _inMain;

    private TypesRepoState(HashSet<string> inRelease, HashSet<string> inMain)
    {
        _inRelease = inRelease;
        _inMain = inMain;
    }

    public static async Task<TypesRepoState> LoadAsync(GitLabClient git, string project, CancellationToken ct)
    {
        var release = await git.CompareCommitsAsync(project, TypesBranches.Dev, TypesBranches.Release, ct);
        var main = await git.CompareCommitsAsync(project, TypesBranches.Dev, TypesBranches.Main, ct);
        return new TypesRepoState(
            release.SelectMany(c => TypesRepository.PickedFrom(c.Message)).ToHashSet(StringComparer.Ordinal),
            main.SelectMany(c => TypesRepository.PickedFrom(c.Message)).ToHashSet(StringComparer.Ordinal));
    }

    public TypesChangeStage StageOf(string devSha) =>
        Has(_inMain, devSha) ? TypesChangeStage.Prod : Has(_inRelease, devSha) ? TypesChangeStage.Stage : TypesChangeStage.Test;

    private static bool Has(HashSet<string> picked, string sha) => picked.Any(p => sha.StartsWith(p, StringComparison.Ordinal));
}

/// <summary>Ветки изменений и коммиты dev с их этапами - для вкладки «Типы» и выбора при связывании.</summary>
public static class TypesChanges
{
    /// <summary>Ветки types/… (новые сверху) и коммиты dev, которых нет в main как есть (новые сверху).</summary>
    public static async Task<List<TypesChangeEntry>> LoadAsync(GitLabClient git, string project, TypesStore store, bool withItems, CancellationToken ct)
    {
        var state = await TypesRepoState.LoadAsync(git, project, ct);
        var result = new List<TypesChangeEntry>();
        var links = store.ChangeLinks();

        var branches = (await git.ListBranchesAsync(project, null, ct) ?? new()).Where(b => TypesBranches.IsTask(b.Name)).ToList();
        var openMrs = await git.ListOpenMergeRequestsToAsync(project, TypesBranches.Dev, ct) ?? new();
        foreach (var b in branches)
        {
            var ahead = (await git.CompareCommitsAsync(project, TypesBranches.Dev, b.Name, ct)).Count(c => c.ParentIds is not { Count: > 1 });
            var change = new TypesChangeEntry
            {
                Branch = b, Stage = TypesChangeStage.Branch, Ahead = ahead, Mr = openMrs.FirstOrDefault(m => m.SourceBranch == b.Name),
            };
            change.Links.AddRange(links.Where(l => l.IsChange(null, b.Name)));
            if (withItems && ahead > 0)
                change.Items.AddRange(ItemsOf(await git.CompareDiffsAsync(project, TypesBranches.Dev, b.Name, ct)));
            result.Add(change);
        }

        var commits = (await git.CompareCommitsAsync(project, TypesBranches.Main, TypesBranches.Dev, ct))
            .Where(c => !TypesRepository.IsServiceCommit(c))
            .Reverse()
            .ToList();
        foreach (var c in commits)
        {
            var change = new TypesChangeEntry { Commit = c, Stage = state.StageOf(c.Id) };
            change.Links.AddRange(links.Where(l => l.IsChange(c.Id, null)));
            result.Add(change);
        }
        if (withItems)
            await Task.WhenAll(result.Where(c => c.Commit is not null && c.Stage != TypesChangeStage.Prod).Take(40)
                .Select(async c => c.Items.AddRange(await TypesRepository.ItemsAsync(git, project, c.Commit!.Id, ct))));

        result.Sort((a, b) => Nullable.Compare(b.Date, a.Date));
        return result;
    }

    /// <summary>«тип document», «+ состояние Draft» по изменённым файлам.</summary>
    public static IEnumerable<string> ItemsOf(IEnumerable<GitLabDiff> diffs)
    {
        foreach (var d in diffs)
        {
            var path = d.DeletedFile ? d.OldPath : d.NewPath;
            if (TypesSnapshot.Describe(path) is not { } item)
            {
                if (path is not null)
                    yield return path;
                continue;
            }
            yield return $"{(d.NewFile ? "+ " : d.DeletedFile ? "− " : "")}{item.Kind} {item.Name}";
        }
    }
}

/// <summary>Модули и автоматизации, с которыми связываются изменения типов: группа GitLab, страница, название.</summary>
public static class TypesTargets
{
    public static string Key(this TypesTargetKind kind) => kind switch
    {
        TypesTargetKind.Server => "server",
        TypesTargetKind.Automation => "automation",
        _ => "client",
    };

    public static TypesTargetKind? Parse(string? key) => key switch
    {
        "client" => TypesTargetKind.Client,
        "server" => TypesTargetKind.Server,
        "automation" => TypesTargetKind.Automation,
        _ => null,
    };

    public static string Title(this TypesTargetKind kind) => kind switch
    {
        TypesTargetKind.Server => "Серверный модуль",
        TypesTargetKind.Automation => "Автоматизация",
        _ => "Клиентский модуль",
    };

    public static string? GroupPath(this TypesTargetKind kind, AdminSettings s) => kind switch
    {
        TypesTargetKind.Server => s.ServerModulesGroupPath,
        TypesTargetKind.Automation => s.GitLabGroupPath,
        _ => s.ModulesGroupPath,
    };

    public static string PageUrl(this TypesTargetKind kind, string name) => kind switch
    {
        TypesTargetKind.Server => $"/Modules/Module?name={Uri.EscapeDataString(name)}&kind=server",
        TypesTargetKind.Automation => $"/Automations/Item?name={Uri.EscapeDataString(name)}",
        _ => $"/Modules/Module?name={Uri.EscapeDataString(name)}",
    };

    /// <summary>Коммит в dev, которым влита ветка (squash или merge-коммит её MR); null - ещё не влита.</summary>
    public static async Task<string?> MergedShaAsync(GitLabClient git, string project, string branch, CancellationToken ct)
    {
        var mrs = await git.ListMergedMergeRequestsFromAsync(project, branch, ct) ?? new();
        var mr = mrs.FirstOrDefault(m => m.TargetBranch == TypesBranches.Dev);
        return mr?.SquashCommitSha ?? mr?.MergeCommitSha;
    }

    /// <summary>
    /// Есть ли коммит <paramref name="sha"/> в ветке <paramref name="reference"/>: в её истории или перенесён cherry-pick'ом
    /// (выборочный релиз модуля собирается от main переносом коммитов задач).
    /// </summary>
    public static async Task<bool> ContainsAsync(GitLabClient git, string project, string reference, string sha, CancellationToken ct)
    {
        if ((await git.CompareCommitsAsync(project, reference, sha, ct)).Count == 0)
            return true;
        var own = await git.CompareCommitsAsync(project, TypesBranches.Main, reference, ct);
        return own.SelectMany(c => TypesRepository.PickedFrom(c.Message)).Any(p => sha.StartsWith(p, StringComparison.Ordinal));
    }
}
