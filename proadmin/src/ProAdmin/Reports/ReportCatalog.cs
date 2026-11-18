using System.Collections.Concurrent;
using ProAdmin.Automations;
using ProAdmin.GitLab;
using ProAdmin.Pilot;
using ProAdmin.Settings;

namespace ProAdmin.Reports;

/// <summary>Состояние отчёта в одном контуре: сервер Pilot против ветки контура.</summary>
public sealed class ReportEnvState
{
    public required EnvironmentSettings Env { get; init; }
    public PilotReport? Pilot { get; init; }
    public string? BranchBlob { get; init; }
    public SyncState State { get; init; }
}

/// <summary>В какой ветке менялся .repx с момента расхождения dev и main.</summary>
public enum ReportDrift
{
    /// <summary>Ветки совпадают (или сравнивать нечего).</summary>
    None,

    /// <summary>Отчёт менялся в dev - обычный перенос на прод.</summary>
    DevAhead,

    /// <summary>Отчёт менялся только в main (хотфикс на проде) - dev и тест надо подтянуть с прода.</summary>
    MainAhead,

    /// <summary>Отчёт менялся и в dev, и в main - нужно объединить вручную.</summary>
    Diverged,
}

public sealed class ReportRow
{
    public required string Name { get; init; }
    public AutomationRepo? Repo { get; init; }
    public Dictionary<string, ReportEnvState> Envs { get; } = new(StringComparer.OrdinalIgnoreCase);
    public string? DevBlob { get; init; }
    public string? MainBlob { get; init; }

    /// <summary>Содержимое dev и main совпадает (с точностью до переводов строк).</summary>
    public bool DevEqualsMain { get; init; }

    public ReportDrift Drift { get; init; }

    /// <summary>Релиз, на который откатили прод (main ушёл вперёд dev именно откатом); null - не откат.</summary>
    public string? RollbackTo { get; set; }

    /// <summary>В dev есть отчёт, которого нет в main (или он другой) - ждёт переноса на прод.</summary>
    public bool PendingPromotion => DevBlob is not null && !DevEqualsMain && Drift == ReportDrift.DevAhead;

    /// <summary>В main хотфикс, которого нет в dev, - ждёт переноса с прода в dev и на тест.</summary>
    public bool PendingBackport => Drift is ReportDrift.MainAhead or ReportDrift.Diverged;

    public string? Folder => Repo?.Folder;

    /// <summary>Папка отчёта на сервере Pilot (первая найденная) - подсказка при создании репозитория.</summary>
    public string? PilotFolder => Envs.Values.Select(e => e.Pilot?.Folder).FirstOrDefault(f => f is not null);
}

public sealed class ReportCatalogResult
{
    public List<ReportRow> Rows { get; } = new();
    public List<EnvironmentSettings> Envs { get; } = new();
    public Dictionary<string, (string Message, PilotErrorKind Kind)> PilotErrors { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, PilotReportList> Pilot { get; } = new(StringComparer.OrdinalIgnoreCase);
    public string? GitError { get; set; }
}

/// <summary>
/// Сводка отчётов: серверы Pilot (контуры с веткой) против репозиториев группы отчётов GitLab.
/// Отчёт и репозиторий сопоставляются по имени (имя проекта GitLab = имя отчёта в Pilot, без учёта
/// регистра и символов, недопустимых в имени проекта).
/// </summary>
public sealed class ReportCatalog
{
    private const int GitParallelism = 8;

    private readonly PilotReportGateway _pilot;

    // Нормализованный хеш git-блоба: блоб неизменяем, поэтому кэш без срока жизни.
    private readonly ConcurrentDictionary<string, string> _normalizedBlob = new();

    public ReportCatalog(PilotReportGateway pilot) => _pilot = pilot;

    /// <summary>
    /// Контуры отчётов: правка (dev) и прод (main). Правка - тестовый сервер или, по настройке
    /// <see cref="AdminSettings.ReportsEditOnStage"/>, сервер стейджа.
    /// </summary>
    public static IEnumerable<EnvironmentSettings> ReportEnvs(AdminSettings settings)
    {
        var envs = settings.Environments.Where(e => e.HasServer && e.Branch.Length > 0);
        if (!settings.ReportsEditOnStage || string.IsNullOrWhiteSpace(settings.StagePilotUrl))
            return envs;
        return envs.Select(e => e.Branch != AutomationRepositories.DevBranch ? e : new EnvironmentSettings
        {
            Key = ProAdmin.Content.ContentReleases.StageKey,
            Title = "Стейдж",
            PilotUrl = settings.StagePilotUrl,
            Branch = AutomationRepositories.DevBranch,
            RequireTaskReference = e.RequireTaskReference,
        });
    }

    /// <summary>Контур отчётов по ключу (стейдж - тоже, если отчёты правят на нём).</summary>
    public static EnvironmentSettings? FindEnv(AdminSettings settings, string? key) =>
        ReportEnvs(settings).FirstOrDefault(e => e.Key == key) ?? settings.Find(key);

    public static EnvironmentSettings? DevEnv(AdminSettings settings) =>
        ReportEnvs(settings).FirstOrDefault(e => e.Branch == AutomationRepositories.DevBranch);

    public static EnvironmentSettings? MainEnv(AdminSettings settings) =>
        ReportEnvs(settings).FirstOrDefault(e => e.Branch == AutomationRepositories.MainBranch);

    public async Task<ReportCatalogResult> LoadAsync(AdminSettings settings, Func<string, PilotLogin> login, GitLabClient git, CancellationToken ct)
    {
        var result = new ReportCatalogResult();
        result.Envs.AddRange(ReportEnvs(settings));

        var pilotTasks = result.Envs.Select(async env =>
        {
            try
            {
                return (env, list: await _pilot.ListAsync(env.PilotUrl, login(env.Key), settings.ReportsLicenseType, withContent: true, ct), error: (PilotGatewayException?)null);
            }
            catch (PilotGatewayException ex)
            {
                return (env, list: (PilotReportList?)null, error: ex);
            }
        }).ToList();

        var branches = result.Envs.Select(e => e.Branch)
            .Concat(new[] { AutomationRepositories.DevBranch, AutomationRepositories.MainBranch })
            .Distinct().ToList();
        var gitTask = LoadGitAsync(git, settings.ReportsGroupPath!, branches, result, ct);

        foreach (var (env, list, error) in await Task.WhenAll(pilotTasks))
        {
            if (list is not null) result.Pilot[env.Key] = list;
            if (error is not null) result.PilotErrors[env.Key] = (error.Message, error.Kind);
        }
        var repos = await gitTask;

        // Сопоставление по ключу имени.
        var repoByKey = (repos ?? new()).GroupBy(r => ProjectNames.Key(r.Repo.Name)).ToDictionary(g => g.Key, g => g.First());
        var keys = new List<(string Key, string Name)>();
        foreach (var r in repos ?? new())
            keys.Add((ProjectNames.Key(r.Repo.Name), r.Repo.Name));
        foreach (var list in result.Pilot.Values)
            foreach (var p in list.Reports)
                keys.Add((ProjectNames.Key(p.Name), p.Name));

        var pilotByEnv = result.Pilot.ToDictionary(
            kv => kv.Key,
            kv => kv.Value.Reports.GroupBy(r => ProjectNames.Key(r.Name)).ToDictionary(g => g.Key, g => g.First()),
            StringComparer.OrdinalIgnoreCase);

        foreach (var (key, name) in keys.GroupBy(k => k.Key).Select(g => g.First()))
        {
            repoByKey.TryGetValue(key, out var repo);
            var devBlob = repo?.Blobs.GetValueOrDefault(AutomationRepositories.DevBranch);
            var mainBlob = repo?.Blobs.GetValueOrDefault(AutomationRepositories.MainBranch);
            var devEqualsMain = devBlob is not null && mainBlob is not null &&
                                (devBlob == mainBlob || await SameNormalizedAsync(git, repo!.Repo.ProjectPath, devBlob, mainBlob, ct));

            var drift = ReportDrift.None;
            if (repo is not null && devBlob is not null && !devEqualsMain)
            {
                try
                {
                    drift = await DriftAsync(git, repo.Repo.ProjectPath, repo.Paths.GetValueOrDefault(AutomationRepositories.DevBranch),
                        repo.Paths.GetValueOrDefault(AutomationRepositories.MainBranch), ct);
                }
                catch (GitLabApiException)
                {
                    drift = ReportDrift.DevAhead;
                }
            }

            var row = new ReportRow
            {
                Name = repo?.Repo.Name ?? name,
                Repo = repo?.Repo,
                DevBlob = devBlob,
                MainBlob = mainBlob,
                DevEqualsMain = devEqualsMain,
                Drift = drift,
            };

            foreach (var env in result.Envs)
            {
                PilotReport? pilot = null;
                if (pilotByEnv.TryGetValue(env.Key, out var map))
                    map.TryGetValue(key, out pilot);
                var branchKnown = repo is not null && repo.Branches.Contains(env.Branch);
                var blob = repo?.Blobs.GetValueOrDefault(env.Branch);

                SyncState state;
                if (!result.Pilot.ContainsKey(env.Key) || (repos is null)) state = SyncState.Unknown;
                else if (repo is null) state = pilot is null ? SyncState.Unknown : SyncState.OnlyPilot;
                else if (!branchKnown) state = SyncState.NoBranch;
                else if (blob is null) state = pilot is null ? SyncState.Unknown : SyncState.OnlyPilot;
                else if (pilot is null) state = SyncState.OnlyGit;
                else state = await MatchesAsync(git, repo.Repo.ProjectPath, pilot, blob, ct) ? SyncState.Synced : SyncState.Differs;

                row.Envs[env.Key] = new ReportEnvState { Env = env, Pilot = pilot, BranchBlob = blob, State = state };
            }
            if (repo is not null && row.PendingBackport)
            {
                try { row.RollbackTo = (await ProAdmin.Modules.ContentNextSteps.RollbackAtMainAsync(git, repo.Repo.ProjectPath, ct))?.Target; }
                catch (GitLabApiException) { }
            }
            result.Rows.Add(row);
        }

        result.Rows.Sort((x, y) =>
        {
            var fx = x.Folder ?? "￿";
            var fy = y.Folder ?? "￿";
            var c = string.Compare(fx, fy, StringComparison.OrdinalIgnoreCase);
            return c != 0 ? c : string.Compare(x.Name, y.Name, StringComparison.CurrentCultureIgnoreCase);
        });
        return result;
    }

    /// <summary>
    /// Где менялся .repx с момента расхождения dev и main (diff от merge-base в каждую сторону).
    /// Хотфикс, отправленный модулем прямо в main, даёт <see cref="ReportDrift.MainAhead"/>.
    /// Если по истории не понять (например, ветки без общего предка), считается, что впереди dev - как раньше.
    /// </summary>
    public static async Task<ReportDrift> DriftAsync(GitLabClient git, string project, string? devPath, string? mainPath, CancellationToken ct)
    {
        if (mainPath is null)
            return ReportDrift.DevAhead;
        bool Touches(List<GitLabDiff> diffs, string? path) => path is not null &&
            diffs.Any(d => string.Equals(d.NewPath, path, StringComparison.OrdinalIgnoreCase) || string.Equals(d.OldPath, path, StringComparison.OrdinalIgnoreCase));

        var inDev = await git.CompareDiffsAsync(project, AutomationRepositories.MainBranch, AutomationRepositories.DevBranch, ct);
        var inMain = await git.CompareDiffsAsync(project, AutomationRepositories.DevBranch, AutomationRepositories.MainBranch, ct);
        var devChanged = Touches(inDev, devPath) || Touches(inDev, mainPath);
        var mainChanged = Touches(inMain, mainPath) || Touches(inMain, devPath);
        return (devChanged, mainChanged) switch
        {
            (true, true) => ReportDrift.Diverged,
            (false, true) => ReportDrift.MainAhead,
            _ => ReportDrift.DevAhead,
        };
    }

    /// <summary>Отчёт сервера совпадает с блобом ветки: точно или с точностью до BOM и переводов строк.</summary>
    public async Task<bool> MatchesAsync(GitLabClient git, string project, PilotReport pilot, string blob, CancellationToken ct)
    {
        if (pilot.Sha == blob)
            return true;
        if (pilot.NormalizedSha is null)
            return false;
        return await NormalizedShaAsync(git, project, blob, ct) == pilot.NormalizedSha;
    }

    private async Task<bool> SameNormalizedAsync(GitLabClient git, string project, string a, string b, CancellationToken ct) =>
        await NormalizedShaAsync(git, project, a, ct) == await NormalizedShaAsync(git, project, b, ct);

    private async Task<string> NormalizedShaAsync(GitLabClient git, string project, string blob, CancellationToken ct)
    {
        if (_normalizedBlob.TryGetValue(blob, out var sha))
            return sha;
        sha = ReportFiles.BlobSha(ReportFiles.Normalize(await git.GetBlobRawAsync(project, blob, ct)));
        _normalizedBlob[blob] = sha;
        return sha;
    }

    private static async Task<List<RepoBranches>?> LoadGitAsync(GitLabClient git, string groupPath, List<string> branches,
        ReportCatalogResult result, CancellationToken ct)
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
                    var state = new RepoBranches(repo);
                    foreach (var branch in branches)
                    {
                        var tree = await git.ListTreeAsync(repo.ProjectPath, branch, ct);
                        if (tree is null)
                            continue;
                        state.Branches.Add(branch);
                        if (ReportFiles.FindPath(tree, repo.Name) is { } path)
                        {
                            state.Paths[branch] = path;
                            state.Blobs[branch] = tree.First(i => i.Type == "blob" && i.Path == path).Id;
                        }
                    }
                    return state;
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

    internal sealed class RepoBranches
    {
        public RepoBranches(AutomationRepo repo) => Repo = repo;

        public AutomationRepo Repo { get; }
        public HashSet<string> Branches { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> Blobs { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> Paths { get; } = new(StringComparer.Ordinal);
    }
}
