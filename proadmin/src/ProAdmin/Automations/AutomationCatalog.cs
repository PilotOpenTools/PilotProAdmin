using ProAdmin.GitLab;
using ProAdmin.Pilot;
using ProAdmin.Settings;

namespace ProAdmin.Automations;

public enum SyncState
{
    Synced,
    Differs,
    OnlyPilot,
    OnlyGit,
    NoBranch,
    Unknown,
}

/// <summary>Автоматизация в одном контуре: сервер Pilot против ветки контура.</summary>
public sealed class CatalogEnvState
{
    public required EnvironmentSettings Env { get; init; }
    public AutomationSnapshot? Pilot { get; init; }
    public SyncState State { get; init; }
}

/// <summary>Текст автоматизации в одном источнике: на сервере контура или в ветке (C# из веток каталог не читает).</summary>
public sealed record AutomationText(string Source, string Triggers, string? CSharp);

public sealed class CatalogRow
{
    public string Name { get; init; } = "";
    public AutomationRepo? Repo { get; init; }
    public bool GitHasCSharp { get; init; }
    public AutomationVersion? DevVersion { get; init; }
    public AutomationVersion? MainVersion { get; init; }
    public Dictionary<string, CatalogEnvState> Envs { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>JSON сценария в ветках dev и main.</summary>
    public Dictionary<string, string> BranchJson { get; } = new(StringComparer.Ordinal);

    /// <summary>Все тексты автоматизации: сначала серверы, затем ветки.</summary>
    public IEnumerable<AutomationText> Texts()
    {
        foreach (var state in Envs.Values)
            if (state.Pilot is { } a)
                yield return new AutomationText(state.Env.Title, a.Triggers, a.CSharpCode);
        foreach (var (branch, json) in BranchJson)
            yield return new AutomationText(branch, json, null);
    }

    public bool HasCSharp => GitHasCSharp || Envs.Values.Any(e => e.Pilot?.HasCSharp == true);

    /// <summary>В dev версия выше, чем в main - ждёт переноса на прод.</summary>
    public bool PendingPromotion => DevVersion is { } d && (MainVersion is null || d > MainVersion.Value);

    /// <summary>В main версия выше, чем в dev - прод меняли мимо теста.</summary>
    /// <summary>Релиз, на который откатили прод; null - main ушёл вперёд не откатом.</summary>
    public string? RollbackTo { get; set; }

    public bool MainAhead => DevVersion is { } d && MainVersion is { } m && m > d;

    /// <summary>Папка репозитория; null - репозитория нет (автоматизация только на серверах).</summary>
    public string? Folder => Repo?.Folder;

    public CatalogEnvState? For(EnvironmentSettings? env) => env is null ? null : Envs.GetValueOrDefault(env.Key);
}

public sealed class CatalogResult
{
    public List<CatalogRow> Rows { get; } = new();

    /// <summary>Контуры с сервером - по группе столбцов на каждый.</summary>
    public List<EnvironmentSettings> Envs { get; } = new();
    public Dictionary<string, (string Message, PilotErrorKind Kind)> PilotErrors { get; } = new(StringComparer.OrdinalIgnoreCase);
    public string? GitError { get; set; }
}

/// <summary>Сводный список: автоматизации всех серверов Pilot против репозиториев группы GitLab (ветка каждого контура).</summary>
public class AutomationCatalog
{
    private const int GitParallelism = 8;

    private readonly PilotGateway _pilot;

    public AutomationCatalog(PilotGateway pilot) => _pilot = pilot;

    public virtual async Task<CatalogResult> LoadAsync(AdminSettings settings, Func<string, PilotLogin> login, GitLabClient git, CancellationToken ct)
    {
        var result = new CatalogResult();
        result.Envs.AddRange(settings.Environments.Where(e => e.HasServer));

        var pilotTasks = result.Envs.ToDictionary(e => e.Key, e => LoadPilotAsync(e, login(e.Key), result, ct), StringComparer.OrdinalIgnoreCase);
        var branches = result.Envs.Select(e => e.Branch).Where(b => b.Length > 0).Distinct().ToList();
        var gitTask = settings.GitLabGroupPath is { } group
            ? LoadGitAsync(git, group, branches, result, ct)
            : Task.FromResult<List<RepoInfo>?>(null);

        await Task.WhenAll(pilotTasks.Values.Append<Task>(gitTask));
        var repos = gitTask.Result;
        var pilot = pilotTasks.ToDictionary(p => p.Key,
            p => p.Value.Result?.GroupBy(a => a.Name, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal),
            StringComparer.OrdinalIgnoreCase);

        // Имя проекта уникально только внутри подгруппы - при повторе берём первый, остальные покажутся отдельными строками.
        var byName = (repos ?? new()).GroupBy(r => r.Repo.Name, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var matched = new HashSet<string>(StringComparer.Ordinal); // ProjectPath сопоставленных репозиториев

        var names = pilot.Values.Where(l => l is not null).SelectMany(l => l!.Keys).Distinct(StringComparer.Ordinal).ToList();
        foreach (var name in names)
        {
            var repo = byName.GetValueOrDefault(name);
            if (repo is not null) matched.Add(repo.Repo.ProjectPath);
            result.Rows.Add(BuildRow(name, repo, repos is not null, result.Envs, pilot));
        }
        foreach (var repo in (repos ?? new()).Where(r => !matched.Contains(r.Repo.ProjectPath)))
            result.Rows.Add(BuildRow(repo.Repo.Name, repo, true, result.Envs, pilot));

        foreach (var row in result.Rows.Where(r => r.MainAhead && r.Repo is not null))
        {
            try { row.RollbackTo = (await ProAdmin.Modules.ContentNextSteps.RollbackAtMainAsync(git, row.Repo!.ProjectPath, ct))?.Target; }
            catch (GitLabApiException) { }
        }

        result.Rows.Sort((x, y) =>
        {
            // Папки по алфавиту, корень первым, «без репозитория» в конце.
            var fx = x.Folder ?? "￿";
            var fy = y.Folder ?? "￿";
            var c = string.Compare(fx, fy, StringComparison.OrdinalIgnoreCase);
            return c != 0 ? c : string.Compare(x.Name, y.Name, StringComparison.CurrentCultureIgnoreCase);
        });
        return result;
    }

    private static CatalogRow BuildRow(string name, RepoInfo? repo, bool gitLoaded, List<EnvironmentSettings> envs,
        Dictionary<string, Dictionary<string, AutomationSnapshot>?> pilot)
    {
        var row = new CatalogRow
        {
            Name = name,
            Repo = repo?.Repo,
            GitHasCSharp = repo?.Branches.Values.Any(b => b?.CsBlob is not null) == true,
            DevVersion = repo?.DevVersion,
            MainVersion = repo?.MainVersion,
        };
        foreach (var (branch, json) in repo?.BranchJson ?? new())
            row.BranchJson[branch] = json;
        foreach (var env in envs)
        {
            var list = pilot[env.Key];
            var a = list?.GetValueOrDefault(name);
            var branch = repo is not null && env.Branch.Length > 0 ? repo.Branches.GetValueOrDefault(env.Branch) : null;
            SyncState state;
            if (list is null || !gitLoaded || env.Branch.Length == 0) state = SyncState.Unknown;
            else if (repo is null) state = a is null ? SyncState.Unknown : SyncState.OnlyPilot;
            else if (branch is null) state = SyncState.NoBranch;
            else if (branch.JsonBlob is null) state = a is null ? SyncState.Unknown : SyncState.OnlyPilot;
            else if (a is null) state = SyncState.OnlyGit;
            else state = AutomationFiles.MatchesBlob(a.Triggers, branch.JsonBlob) && AutomationFiles.MatchesBlob(a.CSharpCode, branch.CsBlob)
                ? SyncState.Synced
                : SyncState.Differs;
            row.Envs[env.Key] = new CatalogEnvState { Env = env, Pilot = a, State = state };
        }
        return row;
    }

    private async Task<IReadOnlyList<AutomationSnapshot>?> LoadPilotAsync(EnvironmentSettings env, PilotLogin login, CatalogResult result, CancellationToken ct)
    {
        try
        {
            return await _pilot.GetAutomationsAsync(env.PilotUrl, login, ct);
        }
        catch (PilotGatewayException ex)
        {
            lock (result.PilotErrors)
                result.PilotErrors[env.Key] = (ex.Message, ex.Kind);
            return null;
        }
    }

    private static async Task<List<RepoInfo>?> LoadGitAsync(GitLabClient git, string groupPath, List<string> branches, CatalogResult result, CancellationToken ct)
    {
        try
        {
            var repos = await AutomationRepositories.ListAsync(git, groupPath, ct);
            using var gate = new SemaphoreSlim(GitParallelism);
            var tasks = repos.Select(async repo =>
            {
                await gate.WaitAsync(ct);
                try
                {
                    var info = new RepoInfo(repo);
                    foreach (var branch in branches)
                    {
                        var tree = await git.ListTreeAsync(repo.ProjectPath, branch, ct);
                        info.Branches[branch] = tree is null ? null : new RepoState(repo, tree);
                    }
                    var json = AutomationFiles.JsonPath(repo.Name);
                    var dev = (await git.FindFileAsync(repo.ProjectPath, json, AutomationRepositories.DevBranch, ct))?.Text;
                    var main = (await git.FindFileAsync(repo.ProjectPath, json, AutomationRepositories.MainBranch, ct))?.Text;
                    info.DevVersion = AutomationVersion.Find(dev);
                    info.MainVersion = AutomationVersion.Find(main);
                    if (dev is not null) info.BranchJson[AutomationRepositories.DevBranch] = dev;
                    if (main is not null) info.BranchJson[AutomationRepositories.MainBranch] = main;
                    return info;
                }
                finally { gate.Release(); }
            });
            return (await Task.WhenAll(tasks)).ToList();
        }
        catch (GitLabApiException ex)
        {
            result.GitError = ex.Message;
            return null;
        }
    }

    private sealed class RepoInfo
    {
        public RepoInfo(AutomationRepo repo) => Repo = repo;

        public AutomationRepo Repo { get; }

        /// <summary>Ветка / её файлы; null - такой ветки в репозитории нет.</summary>
        public Dictionary<string, RepoState?> Branches { get; } = new(StringComparer.Ordinal);
        public AutomationVersion? DevVersion { get; set; }
        public AutomationVersion? MainVersion { get; set; }
        public Dictionary<string, string> BranchJson { get; } = new(StringComparer.Ordinal);
    }

    /// <summary>Файлы автоматизации в одной ветке репозитория.</summary>
    internal sealed class RepoState
    {
        public RepoState(AutomationRepo repo, List<GitLabTreeItem>? tree)
        {
            Repo = repo;
            Tree = tree;
            if (tree is null) return;
            JsonBlob = tree.FirstOrDefault(i => i.Type == "blob" && i.Path == AutomationFiles.JsonPath(repo.Name))?.Id;
            CsBlob = tree.FirstOrDefault(i => i.Type == "blob" && i.Path == AutomationFiles.CsPath(repo.Name))?.Id;
        }

        public AutomationRepo Repo { get; }

        /// <summary>null - в репозитории нет этой ветки.</summary>
        public List<GitLabTreeItem>? Tree { get; }
        public string? JsonBlob { get; }
        public string? CsBlob { get; }
    }
}
