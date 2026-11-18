using System.Collections.Concurrent;
using ProAdmin.GitLab;
using ProAdmin.Modules;

namespace ProAdmin.ActiveWork;

/// <summary>Ветка, в которой за период появились коммиты.</summary>
public sealed class ActiveBranch
{
    public required GitLabProject Project { get; init; }
    public required string Branch { get; init; }
    public required int Commits { get; init; }

    /// <summary>Коммитов больше, чем вернул GitLab за один запрос (100) - показываем «100+».</summary>
    public bool More { get; init; }

    public required List<string> Authors { get; init; }
    public required List<string> Tasks { get; init; }
    public required GitLabCommit Last { get; init; }
    public required DateTimeOffset First { get; init; }

    /// <summary>Подгруппа относительно корневой группы раздела ("" - сама группа).</summary>
    public required string Folder { get; init; }
}

public sealed class ActiveWorkResult
{
    public required DateTimeOffset Since { get; init; }
    public required DateTimeOffset Until { get; init; }
    public List<ActiveBranch> Branches { get; init; } = new();
    public int ProjectsScanned { get; init; }
    public string? Error { get; init; }
    public List<string> Warnings { get; init; } = new();
    public DateTimeOffset LoadedAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// «Активные работы»: в каких репозиториях группы GitLab и в каких ветках появились коммиты за период.
/// Проекты отбираются по last_activity_after, ветки - по дате последнего коммита, затем коммиты ветки за период.
/// </summary>
public sealed class ActiveWorkService
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(2);
    private const int Parallelism = 6;

    private readonly ConcurrentDictionary<string, ActiveWorkResult> _cache = new();

    public async Task<ActiveWorkResult> LoadAsync(GitLabClient git, long userId, string groupPath, DateTimeOffset since, DateTimeOffset until,
        string taskPattern, bool fresh, CancellationToken ct)
    {
        var key = $"{userId}|{groupPath}|{since:O}|{until:O}";
        if (!fresh && _cache.TryGetValue(key, out var cached) && DateTimeOffset.UtcNow - cached.LoadedAt < CacheTtl)
            return cached;

        var result = await LoadCoreAsync(git, groupPath, since, until, taskPattern, ct);
        if (result.Error is null)
        {
            foreach (var old in _cache.Where(p => DateTimeOffset.UtcNow - p.Value.LoadedAt > CacheTtl).Select(p => p.Key).ToList())
                _cache.TryRemove(old, out _);
            _cache[key] = result;
        }
        return result;
    }

    private static async Task<ActiveWorkResult> LoadCoreAsync(GitLabClient git, string groupPath, DateTimeOffset since, DateTimeOffset until,
        string taskPattern, CancellationToken ct)
    {
        List<GitLabProject>? projects;
        try
        {
            // last_activity_at GitLab обновляет не на каждый push - берём запас в сутки.
            projects = await git.ListActiveGroupProjectsAsync(groupPath, since.AddDays(-1), ct);
        }
        catch (GitLabApiException ex)
        {
            return new ActiveWorkResult { Since = since, Until = until, Error = ex.Message };
        }
        if (projects is null)
            return new ActiveWorkResult { Since = since, Until = until, Error = $"Группа «{groupPath}» не найдена в GitLab или нет доступа." };

        var branches = new ConcurrentBag<ActiveBranch>();
        var warnings = new ConcurrentBag<string>();
        await Parallel.ForEachAsync(projects, new ParallelOptions { MaxDegreeOfParallelism = Parallelism, CancellationToken = ct }, async (project, token) =>
        {
            try
            {
                var all = await git.ListBranchesAsync(project.PathWithNamespace, null, token) ?? new();
                // Последний коммит ветки старше начала периода - значит, за период в ней ничего не появилось.
                foreach (var branch in all.Where(b => b.Commit?.CommittedDate is { } d && d >= since))
                {
                    var commits = await git.ListCommitsAsync(project.PathWithNamespace, branch.Name, since, until, token);
                    if (commits.Count == 0)
                        continue;
                    branches.Add(new ActiveBranch
                    {
                        Project = project,
                        Branch = branch.Name,
                        Commits = commits.Count,
                        More = commits.Count >= 100,
                        Authors = commits.Select(c => c.AuthorName ?? "?").Distinct().OrderBy(a => a).ToList(),
                        Tasks = commits.Select(c => ModuleBranches.Task(c.Title, taskPattern)).Append(ModuleBranches.Task(branch.Name, taskPattern))
                            .OfType<string>().Distinct().OrderBy(t => t).ToList(),
                        Last = commits[0],
                        First = commits.Min(c => c.CommittedDate ?? until),
                        Folder = Folder(project, groupPath),
                    });
                }
            }
            catch (GitLabApiException ex)
            {
                warnings.Add($"{project.PathWithNamespace}: {ex.Message}");
            }
        });

        return new ActiveWorkResult
        {
            Since = since,
            Until = until,
            ProjectsScanned = projects.Count,
            Branches = branches
                .OrderByDescending(b => b.Last.CommittedDate)
                .ToList(),
            Warnings = warnings.OrderBy(w => w).ToList(),
        };
    }

    private static string Folder(GitLabProject project, string groupPath)
    {
        var ns = project.Namespace?.FullPath ?? "";
        return ns.Length > groupPath.Length && ns.StartsWith(groupPath + "/", StringComparison.OrdinalIgnoreCase)
            ? ns[(groupPath.Length + 1)..]
            : "";
    }
}
