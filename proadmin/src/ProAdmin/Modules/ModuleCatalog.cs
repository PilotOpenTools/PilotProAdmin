using System.Collections.Concurrent;
using ProAdmin.Automations;
using ProAdmin.GitLab;
using ProAdmin.Pilot;
using ProAdmin.Settings;

namespace ProAdmin.Modules;

/// <summary>Контур модулей: тест / dev, стейдж / release/X.Y.Z, прод / main.</summary>
public sealed record ModuleEnv(string Key, string Title, string PilotUrl, string BranchLabel)
{
    public const string Test = AdminSettings.Test;
    public const string Stage = "stage";
    public const string Prod = AdminSettings.Prod;

    public static List<ModuleEnv> All(AdminSettings s)
    {
        var list = new List<ModuleEnv>();
        if (s.Find(Test) is { HasServer: true } t) list.Add(new ModuleEnv(Test, "Тест", t.PilotUrl, ModuleBranches.Dev));
        if (s.StagePilotUrl.Length > 0) list.Add(new ModuleEnv(Stage, "Стейдж", s.StagePilotUrl, ModuleBranches.ReleasePrefix + "X.Y.Z"));
        if (s.Find(Prod) is { HasServer: true } p) list.Add(new ModuleEnv(Prod, "Прод", p.PilotUrl, ModuleBranches.Main));
        return list;
    }

    public static ModuleEnv? Find(AdminSettings s, string key) => All(s).FirstOrDefault(e => e.Key == key);
}

/// <summary>Что лежит в GitLab по модулю: ветки, версии, активный релиз.</summary>
public sealed class ModuleRepoState
{
    public required AutomationRepo Repo { get; init; }
    public string? ProjectFile { get; init; }
    public string? AssemblyInfoFile { get; init; }
    public string? AssemblyName { get; init; }
    public string? Product { get; init; }
    public bool HasCi { get; init; }
    public GitLabBranch? Dev { get; init; }
    public GitLabBranch? Main { get; init; }
    public ModuleVersion? MainVersion { get; init; }
    public ModuleVersion? DevVersion { get; init; }

    /// <summary>release/X.Y.Z с версией выше main - то, что сейчас на стейдже/готовится.</summary>
    public GitLabBranch? ActiveRelease { get; init; }
    public ModuleVersion? ActiveReleaseVersion => ModuleBranches.ReleaseVersion(ActiveRelease?.Name);

    public List<GitLabBranch> TaskBranches { get; init; } = new();
    public List<GitLabBranch> HotfixBranches { get; init; } = new();
    public List<GitLabBranch> ReleaseBranches { get; init; } = new();

    /// <summary>Ветки вне схемы (не main/dev/release/hotfix/feature/bugfix/ci/release-prep) - кандидаты на удаление.</summary>
    public List<GitLabBranch> OtherBranches { get; init; } = new();

    /// <summary>Ветка, которая соответствует контуру (null - на контур сейчас нечего выкладывать).</summary>
    public GitLabBranch? BranchFor(string env) => env switch
    {
        ModuleEnv.Test => Dev,
        ModuleEnv.Stage => ActiveRelease,
        ModuleEnv.Prod => Main,
        _ => null,
    };
}

public enum ModuleSync
{
    Unknown,
    /// <summary>На сервере ровно коммит ветки.</summary>
    Synced,
    /// <summary>На сервере другой коммит - ветка ушла вперёд.</summary>
    Behind,
    /// <summary>Модуль выложен не админкой (коммит неизвестен), но версия совпадает с веткой.</summary>
    SameVersion,
    /// <summary>Выложен не админкой, версия другая.</summary>
    Differs,
    /// <summary>Модуля нет на сервере.</summary>
    Missing,
    /// <summary>На контур нечего выкладывать (например, нет активного релиза).</summary>
    NoBranch,
    /// <summary>Стейдж без активного релиза: на нём последний релиз, он уже выпущен в прод.</summary>
    Released,
    /// <summary>Модуль отключён на контуре: объект в корзине Pilot.</summary>
    Disabled,
}

public sealed class ModuleEnvState
{
    public required ModuleEnv Env { get; init; }
    public PilotModule? Pilot { get; init; }
    public GitLabBranch? Branch { get; init; }
    public ModuleSync State { get; init; }
    public DisabledModule? Disabled { get; init; }
}

public sealed class ModuleRow
{
    public required string Name { get; init; }
    public ModuleRepoState? Git { get; init; }
    public Dictionary<string, ModuleEnvState> Envs { get; } = new(StringComparer.OrdinalIgnoreCase);
    public string? Folder => Git?.Repo.Folder;
}

public sealed class ModuleCatalogResult
{
    public List<ModuleRow> Rows { get; } = new();
    public List<ModuleEnv> Envs { get; } = new();
    public Dictionary<string, (string Message, PilotErrorKind Kind)> PilotErrors { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, PilotModuleList> Pilot { get; } = new(StringComparer.OrdinalIgnoreCase);
    public string? GitError { get; set; }

    /// <summary>Модули на серверах, для которых не найден репозиторий (по контурам).</summary>
    public List<(string Name, Dictionary<string, PilotModule> ByEnv)> Unmatched { get; } = new();

    /// <summary>Отключённые модули, для которых не найден репозиторий.</summary>
    public List<DisabledModule> DisabledUnmatched { get; } = new();
}

/// <summary>
/// Сводка модулей: репозитории группы модулей GitLab против объектов Extension на серверах тест/стейдж/прод.
/// Модуль и репозиторий сопоставляются по AssemblyProduct (= «Наименование» в Pilot), имени проекта или коду в скобках
/// («… - (OpenStoradge) - (CICD)» / проект OpenStoradge / сборка 087OpenStoradge.ext2).
/// </summary>
public sealed class ModuleCatalog
{
    private const int GitParallelism = 8;

    private readonly ModuleTargets _targets;
    private readonly DisabledModuleStore _disabled;

    // Файлы по blob-хешу: неизменяемы, кэш без срока жизни.
    private readonly ConcurrentDictionary<string, string> _textByBlob = new();

    public ModuleCatalog(ModuleTargets targets, DisabledModuleStore disabled)
    {
        _targets = targets;
        _disabled = disabled;
    }

    /// <summary>Отключённый на контуре модуль этого репозитория. Живой объект важнее записи: его могли восстановить в самом Pilot.</summary>
    public DisabledModule? FindDisabled(ModuleKind kind, string env, ModuleRepoState repo, PilotModule? alive)
    {
        if (kind != ModuleKind.Client || alive is not null)
            return null;
        var items = _disabled.For(env);
        return Match(repo, items.Select(m => m.ToModule())) is { } found ? items.First(m => m.Id == found.Id) : null;
    }

    public async Task<ModuleCatalogResult> LoadAsync(AdminSettings settings, Func<string, PilotLogin> login, GitLabClient git, CancellationToken ct,
        ModuleKind kind = ModuleKind.Client)
    {
        var target = _targets.Get(kind);
        var result = new ModuleCatalogResult();
        result.Envs.AddRange(ModuleEnv.All(settings));

        var pilotTasks = result.Envs.Select(async env =>
        {
            try { return (env, list: await target.ListAsync(env.PilotUrl, login(env.Key), settings.ReportsLicenseType, ct), error: (PilotGatewayException?)null); }
            catch (PilotGatewayException ex) { return (env, list: (PilotModuleList?)null, error: ex); }
        }).ToList();

        List<ModuleRepoState>? repos = null;
        try
        {
            var list = await AutomationRepositories.ListAsync(git, kind.GroupPath(settings)!, ct);
            using var gate = new SemaphoreSlim(GitParallelism);
            repos = (await Task.WhenAll(list.Select(async r =>
            {
                await gate.WaitAsync(ct);
                try { return await LoadRepoAsync(git, r, ct); }
                finally { gate.Release(); }
            }))).ToList();
        }
        catch (GitLabApiException ex)
        {
            result.GitError = ex.Message;
        }

        foreach (var (env, list, error) in await Task.WhenAll(pilotTasks))
        {
            if (list is not null) result.Pilot[env.Key] = list;
            if (error is not null) result.PilotErrors[env.Key] = (error.Message, error.Kind);
        }

        var matched = new HashSet<Guid>();
        foreach (var repo in repos ?? new())
        {
            var row = new ModuleRow { Name = repo.Repo.Name, Git = repo };
            foreach (var env in result.Envs)
            {
                var pilot = result.Pilot.TryGetValue(env.Key, out var pl) ? Match(repo, pl.Modules) : null;
                if (pilot is not null)
                    matched.Add(pilot.Id);
                var disabled = FindDisabled(kind, env.Key, repo, pilot);
                if (disabled is not null)
                    matched.Add(disabled.Id);
                row.Envs[env.Key] = State(env, repo, pilot, result.Pilot.ContainsKey(env.Key), disabled);
            }
            result.Rows.Add(row);
        }

        // Модули серверов без репозитория - одной строкой на имя.
        var unmatched = new Dictionary<string, Dictionary<string, PilotModule>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (envKey, list) in result.Pilot)
            foreach (var m in list.Modules.Where(m => !matched.Contains(m.Id)))
            {
                if (!unmatched.TryGetValue(m.Name, out var byEnv))
                    unmatched[m.Name] = byEnv = new(StringComparer.OrdinalIgnoreCase);
                byEnv[envKey] = m;
            }
        result.Unmatched.AddRange(unmatched.OrderBy(kv => kv.Key, StringComparer.CurrentCultureIgnoreCase).Select(kv => (kv.Key, kv.Value)));

        if (kind == ModuleKind.Client)
            result.DisabledUnmatched.AddRange(result.Envs.SelectMany(env => _disabled.For(env.Key))
                .Where(m => !matched.Contains(m.Id) && result.Pilot.TryGetValue(m.Env, out var alive) && alive.Modules.All(a => a.Id != m.Id))
                .OrderBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase));

        result.Rows.Sort((x, y) =>
        {
            var c = string.Compare(x.Folder ?? "", y.Folder ?? "", StringComparison.OrdinalIgnoreCase);
            return c != 0 ? c : string.Compare(x.Name, y.Name, StringComparison.CurrentCultureIgnoreCase);
        });
        return result;
    }

    /// <summary>Состояние одного репозитория: ветки, версии, активный релиз, сведения о сборке.</summary>
    public async Task<ModuleRepoState> LoadRepoAsync(GitLabClient git, AutomationRepo repo, CancellationToken ct)
    {
        var branches = await git.ListBranchesAsync(repo.ProjectPath, null, ct) ?? new();
        var dev = branches.FirstOrDefault(b => b.Name == ModuleBranches.Dev);
        var main = branches.FirstOrDefault(b => b.Name == ModuleBranches.Main);

        string? projectFile = null, infoFile = null, csproj = null, info = null, devCsproj = null, devInfo = null;
        var hasCi = false;
        var baseBranch = main ?? dev;
        var tree = baseBranch is null ? new() : await git.ListTreeAsync(repo.ProjectPath, baseBranch.Name, ct) ?? new();
        projectFile = ModuleSources.FindProject(tree);
        // Новый репозиторий: в main только README от GitLab, код пока в dev - сведения о сборке берём из dev.
        var mainHasProject = main is not null && projectFile is not null;
        if (projectFile is null && dev is not null && baseBranch != dev)
        {
            baseBranch = dev;
            tree = await git.ListTreeAsync(repo.ProjectPath, dev.Name, ct) ?? new();
            projectFile = ModuleSources.FindProject(tree);
        }
        if (baseBranch is not null)
        {
            infoFile = ModuleSources.FindAssemblyInfo(tree, projectFile);
            hasCi = ModuleSources.HasCi(tree, repo.CiConfigPath);
            csproj = await ReadAsync(git, repo.ProjectPath, tree, projectFile, ct);
            info = await ReadAsync(git, repo.ProjectPath, tree, infoFile, ct);
        }
        if (dev is not null && mainHasProject && dev.Commit?.Id != main!.Commit?.Id)
        {
            var devTree = await git.ListTreeAsync(repo.ProjectPath, ModuleBranches.Dev, ct) ?? new();
            devCsproj = await ReadAsync(git, repo.ProjectPath, devTree, projectFile, ct);
            devInfo = await ReadAsync(git, repo.ProjectPath, devTree, infoFile, ct);
            // Шаблон CI (создание модуля, «Подключить») кладётся только в dev, в main он придёт с первым релизом:
            // CI в dev - репозиторий уже готов к CI/CD.
            hasCi = hasCi || ModuleSources.HasCi(devTree, repo.CiConfigPath);
        }

        var mainVersion = mainHasProject ? ModuleSources.Version(csproj, info) : null;
        var releases = branches.Where(b => ModuleBranches.IsRelease(b.Name) && ModuleBranches.ReleaseVersion(b.Name) is not null)
            .OrderByDescending(b => ModuleBranches.ReleaseVersion(b.Name)).ToList();
        var active = releases.FirstOrDefault(b => mainVersion is null || ModuleBranches.ReleaseVersion(b.Name)! > mainVersion);

        return new ModuleRepoState
        {
            Repo = repo,
            ProjectFile = projectFile,
            AssemblyInfoFile = infoFile,
            AssemblyName = projectFile is null ? null : ModuleSources.AssemblyName(projectFile, csproj),
            Product = ModuleSources.Product(csproj, info),
            HasCi = hasCi,
            Dev = dev,
            Main = main,
            MainVersion = mainVersion,
            DevVersion = dev is null ? null : ModuleSources.Version(devCsproj ?? csproj, devInfo ?? info),
            ActiveRelease = active,
            ReleaseBranches = releases,
            TaskBranches = branches.Where(b => ModuleBranches.IsTaskBranch(b.Name)).ToList(),
            HotfixBranches = branches.Where(b => ModuleBranches.IsHotfix(b.Name)).ToList(),
            OtherBranches = branches.Where(b => !ModuleBranches.InScheme(b.Name)).OrderBy(b => b.Name).ToList(),
        };
    }

    /// <summary>Модуль сервера, соответствующий репозиторию; null - не найден.</summary>
    public static PilotModule? Match(ModuleRepoState repo, IEnumerable<PilotModule> modules)
    {
        var list = modules.ToList();
        var keys = MatchKeys(repo).ToList();
        foreach (var key in keys)
        {
            var m = list.FirstOrDefault(p => ProjectNames.Key(p.Name) == key) ??
                    list.FirstOrDefault(p => ModulePackage.CodeFromName(p.Name) is { } code && ProjectNames.Key(code) == key);
            if (m is not null)
                return m;
        }
        return null;
    }

    private static IEnumerable<string> MatchKeys(ModuleRepoState repo)
    {
        if (!string.IsNullOrWhiteSpace(repo.Product)) yield return ProjectNames.Key(repo.Product);
        yield return ProjectNames.Key(repo.Repo.Name);
        yield return ProjectNames.Key(repo.Repo.Slug);
        if (repo.AssemblyName is { } asm)
        {
            yield return ProjectNames.Key(asm);
            // 087OpenStoradge.ext2 / OpenStoradge, ApprovalDocumentVersionSaving.servext / ApprovalDocumentVersionSaving
            yield return ProjectNames.Key(ModuleKinds.CoreName(asm));
        }
    }

    /// <summary>Стейдж без активного релиза, а на нём голова release/X.Y.Z последней версии main (или та же версия).</summary>
    private static bool IsReleased(ModuleEnv env, ModuleRepoState repo, PilotModule? pilot)
    {
        if (env.Key != ModuleEnv.Stage || pilot is null || repo.MainVersion is not { } prod)
            return false;
        var released = repo.ReleaseBranches.FirstOrDefault(b => ModuleBranches.ReleaseVersion(b.Name) == prod);
        if (pilot.DeployedSha is { } sha && (ModuleDeploy.SameCommit(sha, released?.Commit?.Id) || ModuleDeploy.SameCommit(sha, repo.Main?.Commit?.Id)))
            return true;
        // Ветку выпущенного релиза админка удаляет - тогда сверяем версию.
        return (released is null || pilot.DeployedSha is null) && ModuleVersion.TryParse(pilot.Version, out var v) && v == prod;
    }

    /// <summary>
    /// Прод: «Выпустить в прод» выкладывает голову release/X.Y.Z, а затем вливает её в main merge-коммитом.
    /// Если на проде вливаемая ветка (второй родитель головы main) - на проде то же, что в main.
    /// </summary>
    private static bool IsMergedRelease(ModuleEnv env, GitLabBranch branch, string deployed) =>
        env.Key == ModuleEnv.Prod &&
        branch.Commit?.ParentIds is { Count: > 1 } parents &&
        parents.Skip(1).Any(p => ModuleDeploy.SameCommit(deployed, p));

    public static ModuleEnvState State(ModuleEnv env, ModuleRepoState repo, PilotModule? pilot, bool serverKnown, DisabledModule? disabled = null)
    {
        var branch = repo.BranchFor(env.Key);
        ModuleSync state;
        if (!serverKnown) state = ModuleSync.Unknown;
        else if (pilot is null && disabled is not null) state = ModuleSync.Disabled;
        else if (branch is null) state = IsReleased(env, repo, pilot) ? ModuleSync.Released : ModuleSync.NoBranch;
        else if (pilot is null) state = ModuleSync.Missing;
        else if (pilot.DeployedSha is { } sha) state = ModuleDeploy.SameCommit(sha, branch.Commit?.Id) || IsMergedRelease(env, branch, sha) ? ModuleSync.Synced : ModuleSync.Behind;
        else
        {
            var branchVersion = env.Key switch
            {
                ModuleEnv.Test => repo.DevVersion,
                ModuleEnv.Stage => repo.ActiveReleaseVersion,
                _ => repo.MainVersion,
            };
            state = branchVersion is not null && ModuleVersion.TryParse(pilot.Version, out var pv) && pv == branchVersion
                ? ModuleSync.SameVersion : ModuleSync.Differs;
        }
        return new ModuleEnvState { Env = env, Pilot = pilot, Branch = branch, State = state, Disabled = serverKnown ? disabled : null };
    }

    private async Task<string?> ReadAsync(GitLabClient git, string project, List<GitLabTreeItem> tree, string? path, CancellationToken ct)
    {
        var item = path is null ? null : tree.FirstOrDefault(i => i.Path == path);
        if (item is null)
            return null;
        if (_textByBlob.TryGetValue(item.Id, out var text))
            return text;
        text = System.Text.Encoding.UTF8.GetString(await git.GetBlobRawAsync(project, item.Id, ct)).TrimStart('﻿');
        _textByBlob[item.Id] = text;
        return text;
    }
}
