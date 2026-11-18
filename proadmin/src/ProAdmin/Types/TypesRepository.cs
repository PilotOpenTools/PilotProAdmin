using System.Text.RegularExpressions;
using ProAdmin.GitLab;
using ProAdmin.Settings;

namespace ProAdmin.Types;

/// <summary>Ветки репозитория типов и контуры, которым они соответствуют.</summary>
public static class TypesBranches
{
    public const string Dev = "dev";
    public const string Release = "release";
    public const string Main = "main";

    /// <summary>Ветка изменения: types/KTN-123-opisanie от dev, правки редактора копятся в ней и уходят в dev одним коммитом.</summary>
    public const string TaskPrefix = "types/";

    /// <summary>Личные черновики прежней схемы - показываются как ветки изменений.</summary>
    public const string DraftPrefix = "types-draft/";

    /// <summary>Временная ветка переноса: от целевой ветки + cherry-pick выбранных коммитов / MR.</summary>
    public const string PromotePrefix = "types-promote/";

    public static bool IsTask(string? branch) =>
        branch is not null && (branch.StartsWith(TaskPrefix, StringComparison.Ordinal) || branch.StartsWith(DraftPrefix, StringComparison.Ordinal));

    /// <summary>types/ + номер задачи и/или описание латиницей.</summary>
    public static string Task(string? task, string? slug) => Modules.ModuleBranches.TaskBranch(TaskPrefix, task, slug);

    public static string Promote(string target) => $"{PromotePrefix}{target}-{DateTime.Now:yyyyMMdd-HHmmss}";

    /// <summary>Откуда переносятся коммиты в ветку: release / dev, main / release.</summary>
    public static string? SourceOf(string target) => target switch
    {
        Release => Dev,
        Main => Release,
        _ => null,
    };

    public static string Env(string branch) => branch switch
    {
        Dev => TypesEnv.Test,
        Release => TypesEnv.Stage,
        Main => TypesEnv.Prod,
        _ => throw new ArgumentOutOfRangeException(nameof(branch), branch, null),
    };

    public static string ProdTag(Modules.ModuleVersion version) => "v" + version;
}

/// <summary>Контур для типов: сервер Pilot и его ветка.</summary>
public sealed record TypesEnv(string Key, string Title, string PilotUrl, string Branch)
{
    public const string Test = AdminSettings.Test;
    public const string Stage = "stage";
    public const string Prod = AdminSettings.Prod;

    public static List<TypesEnv> All(AdminSettings s)
    {
        var list = new List<TypesEnv>();
        list.Add(new TypesEnv(Test, "Тест", s.Find(Test)?.PilotUrl ?? "", TypesBranches.Dev));
        list.Add(new TypesEnv(Stage, "Стейдж", s.StagePilotUrl, TypesBranches.Release));
        list.Add(new TypesEnv(Prod, "Прод", s.Find(Prod)?.PilotUrl ?? "", TypesBranches.Main));
        return list;
    }

    public static TypesEnv? Find(AdminSettings s, string? key) => All(s).FirstOrDefault(e => e.Key == key);

    public static TypesEnv? ForBranch(AdminSettings s, string branch) => All(s).FirstOrDefault(e => e.Branch == branch);

    public bool HasServer => !string.IsNullOrWhiteSpace(PilotUrl);

    /// <summary>Ключ операций в журнале.</summary>
    public string JobKey => "types:" + Key;
}

/// <summary>Коммит ветки-источника, которого ещё нет в целевой ветке (ни сам, ни его cherry-pick).</summary>
public sealed class PendingCommit
{
    public required GitLabCommit Commit { get; init; }
    public List<string> Items { get; } = new();
}

/// <summary>Чтение и запись репозитория типов через GitLab API.</summary>
public static partial class TypesRepository
{
    /// <summary>Пометка в сообщении коммита, перенесённого cherry-pick'ом (как у git cherry-pick -x).</summary>
    [GeneratedRegex(@"\(cherry picked from commit ([0-9a-f]{7,40})\)")]
    private static partial Regex PickedPattern();

    public const string ReleaseCommitPrefix = "Релиз типов ";
    public const string InitCommitPrefix = "Типы: начальное состояние";

    /// <summary>Группа, в которой лежит проект (для проверки уровня доступа).</summary>
    public static string? GroupOf(string? projectPath) =>
        projectPath is null || projectPath.LastIndexOf('/') <= 0 ? null : projectPath[..projectPath.LastIndexOf('/')];

    public static string PickMessage(GitLabCommit commit) =>
        (commit.Message ?? commit.Title ?? "").TrimEnd() + $"\n\n(cherry picked from commit {commit.Id})";

    public static IEnumerable<string> PickedFrom(string? message) =>
        message is null ? Enumerable.Empty<string>() : PickedPattern().Matches(message).Select(m => m.Groups[1].Value);

    public static bool IsServiceCommit(GitLabCommit c) =>
        c.ParentIds is { Count: > 1 } ||
        c.Title is { } t && (t.StartsWith(ReleaseCommitPrefix, StringComparison.Ordinal) || t.StartsWith(InitCommitPrefix, StringComparison.Ordinal) ||
                             t.StartsWith("Merge branch", StringComparison.Ordinal));

    /// <summary>Содержимое ветки или коммита.</summary>
    public static async Task<TypesSnapshot> LoadAsync(GitLabClient git, string project, string reference, CancellationToken ct) =>
        TypesSnapshot.FromArchive(await git.DownloadArchiveAsync(project, reference, ct));

    /// <summary>
    /// Коммиты <paramref name="source"/>, которые ещё не перенесены в <paramref name="target"/> (старые первыми).
    /// Перенесённый коммит узнаётся по пометке «cherry picked from commit …» в коммитах целевой ветки.
    /// </summary>
    public static async Task<List<PendingCommit>> PendingAsync(GitLabClient git, string project, string source, string target, CancellationToken ct)
    {
        var candidates = await git.CompareCommitsAsync(project, target, source, ct);
        var targetOnly = await git.CompareCommitsAsync(project, source, target, ct);
        var picked = targetOnly.SelectMany(c => PickedFrom(c.Message)).ToHashSet(StringComparer.Ordinal);

        return candidates
            .Where(c => !IsServiceCommit(c))
            .Where(c => !picked.Any(p => c.Id.StartsWith(p, StringComparison.Ordinal)))
            .Select(c => new PendingCommit { Commit = c })
            .ToList();
    }

    /// <summary>Какие элементы меняет коммит: «тип document», «+ состояние Draft»…</summary>
    public static async Task<List<string>> ItemsAsync(GitLabClient git, string project, string sha, CancellationToken ct)
    {
        var result = new List<string>();
        foreach (var d in await git.GetCommitDiffAsync(project, sha, ct))
        {
            var path = d.DeletedFile ? d.OldPath : d.NewPath;
            if (TypesSnapshot.Describe(path) is not { } item)
            {
                if (path is not null)
                    result.Add(path);
                continue;
            }
            var mark = d.NewFile ? "+ " : d.DeletedFile ? "− " : "";
            result.Add($"{mark}{item.Kind} {item.Name}");
        }
        return result;
    }

    /// <summary>
    /// Коммит изменений в ветку изменения types/… (создаётся от dev, если её нет). Возвращает коммит.
    /// </summary>
    public static async Task<GitLabCommit> CommitToDraftAsync(GitLabClient git, string project, string draft, string message,
        IReadOnlyCollection<GitLabCommitAction> actions, CancellationToken ct)
    {
        var exists = await git.FindBranchAsync(project, draft, ct) is not null;
        return await git.CreateCommitAsync(project, draft, exists ? null : TypesBranches.Dev, message, actions, ct);
    }

    /// <summary>Действия коммита, чтобы файлы элементов стали как в <paramref name="target"/> (только types/, states/, state-machines/).</summary>
    public static List<GitLabCommitAction> DiffActions(TypesSnapshot current, TypesSnapshot target, bool includeDeletes)
    {
        target.AdoptPaths(current);
        var before = current.Render();
        var after = target.Render();
        before.Remove(TypesSnapshot.ConfigFile);
        after.Remove(TypesSnapshot.ConfigFile);
        var actions = new List<GitLabCommitAction>();
        foreach (var (path, text) in after)
        {
            if (!before.TryGetValue(path, out var old))
                actions.Add(GitLabCommitAction.Create(path, text));
            else if (old != text)
                actions.Add(GitLabCommitAction.Update(path, text));
        }
        if (includeDeletes)
            foreach (var path in before.Keys.Where(p => !after.ContainsKey(p)))
                actions.Add(GitLabCommitAction.Delete(path));
        return actions;
    }
}
