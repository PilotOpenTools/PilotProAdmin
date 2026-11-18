using ProAdmin.Automations;
using ProAdmin.GitLab;
using ProAdmin.Modules;
using ProAdmin.Pilot;
using ProAdmin.Sessions;
using Microsoft.AspNetCore.Mvc;

namespace ProAdmin.Pages.Modules;

/// <summary>
/// Модули: серверы тест / стейдж / прод против веток dev / release/X.Y.Z / main репозиториев группы модулей.
/// ?kind=server - серверные модули (группа серверных модулей GitLab, выкладка в ServerHost).
/// </summary>
public class IndexModel : AppPageModel
{
    private readonly ModuleCatalog _catalog;
    private readonly ModuleTargets _targets;
    private readonly AssemblySpecStore _libraries;
    private readonly AssemblyScanService _scan;
    private readonly ModuleJobRunner _runner;
    private readonly ModuleAvailabilityService _availability;
    private readonly DisabledModuleStore _disabled;

    public IndexModel(ModuleCatalog catalog, ModuleTargets targets, AssemblySpecStore libraries, AssemblyScanService scan, ModuleJobRunner runner,
        ModuleAvailabilityService availability, DisabledModuleStore disabled)
    {
        _availability = availability;
        _disabled = disabled;
        _runner = runner;
        _catalog = catalog;
        _targets = targets;
        _libraries = libraries;
        _scan = scan;
    }

    /// <summary>Режим и список библиотек модулей этого вида.</summary>
    public AssemblySpec Libraries { get; private set; } = new();

    /// <summary>Режим «запретить доп. библиотеки»: общий модуль на каждом контуре (или ошибка чтения).</summary>
    public Dictionary<string, (SharedModuleState? State, string? Error)> SharedByEnv { get; } = new();

    /// <summary>Строка таблицы библиотек общего модуля: dll и её версия на каждом контуре.</summary>
    public sealed record SharedLibraryRow(string Name, Dictionary<string, PackageAssembly?> ByEnv);

    public List<SharedLibraryRow> SharedLibraries(IEnumerable<ModuleEnv> envs) =>
        SharedByEnv.Values.Where(v => v.State is not null)
            .SelectMany(v => v.State!.Assemblies.Where(a => !a.IsMain).Select(a => a.Name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .Select(n => new SharedLibraryRow(n, envs.ToDictionary(e => e.Key, e =>
                SharedByEnv.TryGetValue(e.Key, out var v)
                    ? v.State?.Assemblies.FirstOrDefault(a => !a.IsMain && string.Equals(a.Name, n, StringComparison.OrdinalIgnoreCase))
                    : null)))
            .ToList();

    /// <summary>Строка редактируемого списка разрешённых библиотек (режимы 2 и 3).</summary>
    public sealed class LibraryForm
    {
        public string? Name { get; set; }
        public string? AssemblyVersion { get; set; }
        public string? FileVersion { get; set; }
        public string? Modules { get; set; }
        public string? Package { get; set; }
        public string? Comment { get; set; }
        public bool Delete { get; set; }
    }

    [BindProperty] public List<LibraryForm> LibraryRows { get; set; } = new();

    [BindProperty(SupportsGet = true)] public string? Kind { get; set; }
    public ModuleKind ModuleKind => ModuleKinds.Parse(Kind);
    public bool IsServer => ModuleKind == ModuleKind.Server;
    public override string? ModulesGroupPath => ModuleKind.GroupPath(AppSettings);
    protected override object? ModulesListRoute => new { kind = ModuleKind.Query() };

    [BindProperty] public string? CredEnv { get; set; }
    [BindProperty] public string? EnvLogin { get; set; }
    [BindProperty] public string? EnvPassword { get; set; }

    public ModuleCatalogResult? Catalog { get; private set; }
    public string? SetupProblem { get; private set; }
    public string? CredentialError { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        if (Prepare() is { } redirect)
            return redirect;
        if (SetupProblem is null)
        {
            await LoadModulesAccessAsync(ct);
            await LoadAsync(ct);
        }
        return Page();
    }

    /// <summary>Причина, по которой модуль нельзя отключить (общий, лежит в Preload); null - можно.</summary>
    public string? ProtectedReason(PilotModule module) => _availability.ProtectedReason(module);

    // Модули без репозитория: страницы модуля у них нет, отключаются и удаляются из этого списка.
    [BindProperty] public string? AvailabilityEnv { get; set; }
    [BindProperty] public Guid ModuleId { get; set; }
    [BindProperty] public string? AvailabilityReason { get; set; }
    [BindProperty] public string? AvailabilityConfirm { get; set; }

    public Task<IActionResult> OnPostDisableModuleAsync(CancellationToken ct) => AvailabilityAsync(ct, async (env, login, license, actor) =>
    {
        var list = await _targets.Get(ModuleKind.Client).ListAsync(env.PilotUrl, login, license, ct);
        var module = list.Modules.FirstOrDefault(m => m.Id == ModuleId);
        if (module is null)
            return "Модуль на сервере не найден.";
        var error = await _availability.DisableAsync(env, login, license, module.Name, module, AvailabilityReason, actor, ct);
        return error ?? Done($"«{module.Name}» отключён на сервере {env.Title}.");
    });

    public Task<IActionResult> OnPostEnableModuleAsync(CancellationToken ct) => AvailabilityAsync(ct, async (env, login, license, actor) =>
    {
        if (_disabled.Find(env.Key, ModuleId) is not { } module)
            return "Запись об отключении не найдена.";
        var error = await _availability.EnableAsync(env, login, license, module.Name, module, actor, ct);
        return error ?? Done($"«{module.Name}» включён на сервере {env.Title}.");
    });

    public Task<IActionResult> OnPostDeleteModuleAsync(CancellationToken ct) => AvailabilityAsync(ct, async (env, login, license, actor) =>
    {
        if (_disabled.Find(env.Key, ModuleId) is not { } module)
            return "Запись об отключении не найдена.";
        var error = await _availability.DeleteAsync(env, login, license, module.Name, module, AvailabilityConfirm, AvailabilityReason, actor, ct);
        return error ?? Done($"«{module.Name}» удалён с сервера {env.Title}.");
    });

    private string? Done(string message)
    {
        TempData["Flash"] = message;
        return null;
    }

    private async Task<IActionResult> AvailabilityAsync(CancellationToken ct, Func<ModuleEnv, PilotLogin, int, ModuleActor, Task<string?>> action)
    {
        if (Prepare() is { } redirect)
            return redirect;
        if (await DenyModulesChangeAsync(ct) is { } deny)
            return deny;
        var env = AvailabilityEnv is null ? null : ModuleEnv.Find(AppSettings, AvailabilityEnv);
        string? error;
        if (ModuleKind != ModuleKind.Client || env is null)
            error = "Действие доступно только для клиентских модулей на настроенном контуре.";
        else
            try
            {
                error = await action(env, ModuleLogin(env.Key), AppSettings.ReportsLicenseType, new ModuleActor(CurrentSession.PilotLogin, CurrentSession.GitLabUser?.Username));
            }
            catch (PilotGatewayException ex)
            {
                error = $"{env.Title}: {ex.Message}";
            }
        if (error is not null)
            TempData["FlashError"] = error;
        return RedirectToPage(new { kind = ModuleKind.Query() });
    }

    /// <summary>Учётка пользователя базы с правами на папку расширений на сервере контура.</summary>
    public async Task<IActionResult> OnPostCredentialsAsync(CancellationToken ct)
    {
        if (Prepare() is { } redirect)
            return redirect;
        if (BlockedCredentialAttempt() is { } blocked)
            return blocked;
        var env = CredEnv is null ? null : ModuleEnv.Find(AppSettings, CredEnv);
        if (env is null || SetupProblem is not null)
            return RedirectToPage(new { kind = ModuleKind.Query() });

        var login = new PilotLogin(EnvLogin?.Trim() ?? "", EnvPassword ?? "");
        try
        {
            await _targets.Get(ModuleKind).ListAsync(env.PilotUrl, login, AppSettings.ReportsLicenseType, ct);
            Vault.SetEnvironmentCredential(CurrentSession, SessionVault.ModulesPrefix + env.Key, login);
            return RedirectToPage(new { kind = ModuleKind.Query() });
        }
        catch (PilotGatewayException ex)
        {
            CredentialAttemptFailed(env.Key);
            CredentialError = $"{env.Title}: {ex.Message}";
            await LoadAsync(ct);
            return Page();
        }
    }

    /// <summary>Сохранить список разрешённых библиотек (режимы 2 и 3). Запрещённые и «есть в Pilot» не меняются.</summary>
    public async Task<IActionResult> OnPostLibrariesAsync(CancellationToken ct)
    {
        if (Prepare() is { } redirect)
            return redirect;
        if (await DenyModulesChangeAsync(ct) is { } deny)
            return deny;
        var spec = _libraries.For(ModuleKind);
        var rows = new List<AssemblySpecEntry>();
        var errors = new List<string>();
        foreach (var row in LibraryRows.Where(r => !r.Delete && !string.IsNullOrWhiteSpace(r.Name)))
        {
            var name = AssemblySpec.NormalizeName(row.Name);
            foreach (var (pattern, title) in new[] { (row.AssemblyVersion, "версия"), (row.FileVersion, "версия файла") })
                if (VersionPattern.Validate(pattern) is { } error)
                    errors.Add($"{name}, {title}: {error}");
            var old = spec.Find(name);
            rows.Add(new AssemblySpecEntry
            {
                Name = name,
                Rule = AssemblyRule.Allowed,
                AssemblyVersion = row.AssemblyVersion ?? "",
                FileVersion = row.FileVersion ?? "",
                Modules = (row.Modules ?? "").Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
                Package = row.Package ?? "",
                Comment = row.Comment ?? old?.Comment ?? "",
            });
        }
        var duplicates = rows.GroupBy(e => e.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        if (duplicates.Count > 0)
            errors.Add($"Библиотеки указаны дважды: {string.Join(", ", duplicates)}.");
        if (errors.Count > 0)
        {
            TempData["FlashError"] = "Список библиотек не сохранён: " + string.Join(" ", errors);
            return RedirectToPage(null, null, new { kind = ModuleKind.Query() }, "libraries");
        }
        var names = rows.Select(r => r.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        spec.Entries = spec.Entries.Where(e => e.Rule != AssemblyRule.Allowed && !names.Contains(e.Name)).Concat(rows).ToList();
        _libraries.Save(ModuleKind, spec, CurrentSession.GitLabUser?.Username is { Length: > 0 } u ? u : CurrentSession.PilotLogin);
        TempData["Flash"] = $"Список разрешённых библиотек сохранён ({rows.Count}) - проверяется в CI и при следующей выкладке.";
        return RedirectToPage(null, null, new { kind = ModuleKind.Query() }, "libraries");
    }

    /// <summary>
    /// «Обновить CI» сразу у всех модулей группы, где .gitlab-ci.yml в dev отличается от шаблона:
    /// на каждый - та же операция, что на странице модуля (MR ci/template-… в dev, pipeline, merge).
    /// </summary>
    public async Task<IActionResult> OnPostUpdateAllCiAsync(CancellationToken ct)
    {
        if (Prepare() is { } redirect)
            return redirect;
        if (await DenyModulesChangeAsync(ct) is { } deny)
            return deny;
        var git = CreateGitLabClient();
        var template = ModuleCiTemplate.Content(AppSettings, ModuleKind);
        var started = new List<string>();
        var failed = new List<string>();
        foreach (var repo in await AutomationRepositories.ListAsync(git, ModuleKind.GroupPath(AppSettings)!, ct))
        {
            try
            {
                var state = await _catalog.LoadRepoAsync(git, repo, ct);
                if (state.Dev is null || !state.HasCi)
                    continue;
                var ci = await git.FindFileAsync(repo.ProjectPath, repo.CiConfigPath ?? ".gitlab-ci.yml", ModuleBranches.Dev, ct);
                if (ci is null || ModuleCiTemplate.Same(ci.Text, template))
                    continue;
                _runner.StartCiUpdate(new ModuleContext
                {
                    Settings = AppSettings,
                    GitLabToken = CurrentSession.GitLabToken!,
                    ProjectPath = repo.ProjectPath,
                    ProjectWebUrl = repo.WebUrl,
                    RepoName = repo.Name,
                    CiConfigPath = repo.CiConfigPath,
                    ProjectFile = state.ProjectFile,
                    AssemblyInfoFile = state.AssemblyInfoFile,
                    Logins = new Dictionary<string, PilotLogin>(),
                    Kind = ModuleKind,
                    Assemblies = _libraries.For(ModuleKind),
                }, template, CurrentSession.PilotLogin, CurrentSession.GitLabUser?.Username);
                started.Add(repo.Name);
            }
            catch (Exception ex) when (ex is GitLabApiException or InvalidOperationException)
            {
                failed.Add($"{repo.Name}: {ex.Message}");
            }
        }
        if (failed.Count > 0)
            TempData["FlashError"] = "Не запущено: " + string.Join("; ", failed);
        TempData["Flash"] = started.Count == 0
            ? "У всех модулей шаблон CI актуален."
            : $"Запущено обновление CI ({started.Count}): {string.Join(", ", started)}. Ход - в разделе «Операции».";
        return RedirectToPage(null, null, new { kind = ModuleKind.Query() });
    }

    private async Task LoadSharedLibrariesAsync(CancellationToken ct)
    {
        if (!Libraries.IsShared || Catalog is null)
            return;
        foreach (var env in Catalog.Envs)
        {
            if (Catalog.PilotErrors.ContainsKey(env.Key))
            {
                SharedByEnv[env.Key] = (null, "сервер недоступен");
                continue;
            }
            try
            {
                SharedByEnv[env.Key] = (await _scan.SharedOnEnvAsync(Libraries, env, ModuleLogin(env.Key), AppSettings.ReportsLicenseType, ct, ModuleKind), null);
            }
            catch (PilotGatewayException ex)
            {
                SharedByEnv[env.Key] = (null, ex.Message);
            }
        }
    }

    private IActionResult? Prepare()
    {
        Libraries = _libraries.For(ModuleKind);
        ViewData["Section"] = IsServer ? "servermodules" : "modules";
        if (!GitLabConnected)
            return RedirectToGitLab();
        SetupProblem = CheckSetup();
        return null;
    }

    private string? CheckSetup()
    {
        var groupUrl = ModuleKind.GroupUrl(AppSettings);
        if (string.IsNullOrWhiteSpace(groupUrl))
            return IsServer ? "Не указана группа GitLab с серверными модулями." : "Не указана группа GitLab с модулями.";
        if (!GitLabLocation.TryParse(groupUrl, out var modules, out var error))
            return error;
        if (GitLabLocation.TryParse(AppSettings.GitLabGroupUrl, out var automations, out _) &&
            !string.Equals(modules!.BaseUrl, automations!.BaseUrl, StringComparison.OrdinalIgnoreCase))
            return $"Группа модулей на другом сервере GitLab ({modules.BaseUrl}), чем группа автоматизаций ({automations.BaseUrl}). Вход в GitLab один.";
        if (ModuleEnv.All(AppSettings).Count == 0)
            return "Не задан ни один сервер Pilot (тест, стейдж, прод).";
        return null;
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        Catalog = await _catalog.LoadAsync(AppSettings, ModuleLogin, CreateGitLabClient(), ct, ModuleKind);
        await LoadSharedLibrariesAsync(ct);
    }

    public static string StateLabel(ModuleSync s) => s switch
    {
        ModuleSync.Synced => "совпадает",
        ModuleSync.Behind => "ветка впереди",
        ModuleSync.SameVersion => "та же версия",
        ModuleSync.Differs => "другая версия",
        ModuleSync.Missing => "нет на сервере",
        ModuleSync.NoBranch => "нет релиза",
        ModuleSync.Released => "выпущен в прод",
        ModuleSync.Disabled => "отключён",
        _ => "-",
    };

    public static string StateCss(ModuleSync s) => s switch
    {
        ModuleSync.Synced or ModuleSync.SameVersion or ModuleSync.Released => "badge-synced",
        ModuleSync.Behind => "badge-onlypilot",
        ModuleSync.Differs => "badge-differs",
        ModuleSync.Missing or ModuleSync.Disabled => "badge-onlygit",
        _ => "",
    };

    public static string StateHint(ModuleEnvState st) => st.State switch
    {
        ModuleSync.Synced => $"На сервере коммит {ModuleDeploy.Short(st.Pilot?.DeployedSha ?? "")} - голова {st.Branch?.Name}",
        ModuleSync.Behind => $"На сервере {ModuleDeploy.Short(st.Pilot?.DeployedSha ?? "")}, в {st.Branch?.Name} - {st.Branch?.Commit?.ShortId}",
        ModuleSync.SameVersion => "Модуль выложен вручную (не админкой), версия совпадает с веткой",
        ModuleSync.Differs => "Модуль выложен вручную (не админкой), версия отличается от ветки",
        ModuleSync.Missing => $"Модуля нет на сервере - появится при выкладке {st.Branch?.Name}",
        ModuleSync.NoBranch => "Нет активной ветки release/X.Y.Z",
        ModuleSync.Disabled => $"Отключён {st.Disabled?.At.ToLocalTime():dd.MM HH:mm} ({st.Disabled?.By}): {st.Disabled?.Reason}. Объект в корзине Pilot; выкладка на контур запрещена, пока модуль не включён",
        ModuleSync.Released => "Активного релиза нет: на стейдже последний релиз, он уже на проде. Следующий - «Релиз / стейдж»",
        _ => "Сервер недоступен",
    };
}
