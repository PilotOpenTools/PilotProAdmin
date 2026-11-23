using ProAdmin.Automations;
using ProAdmin.GitLab;
using ProAdmin.Modules;
using ProAdmin.Pilot;
using ProAdmin.Settings;
using Microsoft.AspNetCore.Mvc;

namespace ProAdmin.Pages;

public class SettingsModel : AppPageModel
{
    private readonly PilotGateway _pilot;
    private readonly AssemblySpecStore _libraries;

    public SettingsModel(PilotGateway pilot, AssemblySpecStore libraries)
    {
        _pilot = pilot;
        _libraries = libraries;
    }

    /// <summary>Режим библиотек модулей: клиентские и серверные (форма «Библиотеки модулей»).</summary>
    public sealed class LibrariesForm
    {
        public AssemblySpecMode Mode { get; set; }
        public string? SharedModule { get; set; }
    }

    [BindProperty] public LibrariesForm ClientLibraries { get; set; } = new();
    [BindProperty] public LibrariesForm ServerLibraries { get; set; } = new();

    /// <summary>Репозитории групп модулей - подсказки для выбора общего модуля.</summary>
    public Dictionary<ModuleKind, List<string>> ModuleRepos { get; } = new();

    public AssemblySpec LibrariesSpec(ModuleKind kind) => _libraries.For(kind);

    private string Who => CurrentSession.Actor;

    [BindProperty] public AdminSettings Form { get; set; } = new();
    [BindProperty] public List<DeployAccountInput> Deploy { get; set; } = [];
    public List<DeployAccountInput> DeployPrefill =>
        HttpContext.RequestServices.GetRequiredService<DeployAccounts>().Prefill(AppSettings);

    public async Task<IActionResult> OnPostDeployAsync([FromServices] DeployAccounts accounts, CancellationToken ct)
    {
        var notes = new List<string>();
        if (await SaveDeployAsync(accounts, notes, ct) is { } error)
            TempData["FlashError"] = string.Join(" ", notes.Append(error));
        else
            TempData["Flash"] = string.Join(" ", notes.Prepend("Служебные учётные записи Pilot сохранены."));
        return RedirectToPage(null, null, null, "deploy-accounts");
    }

    /// <summary>Служебные учётные записи из общей формы; null - сохранено.</summary>
    private async Task<string?> SaveDeployAsync(DeployAccounts accounts, List<string> notes, CancellationToken ct)
    {
        var before = SettingsStore.Current;
        var updated = before.Clone();
        if (await accounts.ApplyAsync(updated, Deploy, _pilot, ct, notes) is { } error)
            return error;
        SettingsStore.UpdateDeployLogins(before, updated);
        return null;
    }

    public List<(string Title, string Level, string Text)> CheckResults { get; } = new();
    public List<string> Errors { get; } = new();

    /// <summary>Ваш уровень доступа к группам из формы (null - GitLab не подключён или группа не задана).</summary>
    public GitLabGroupAccess? GroupAccess { get; private set; }
    public GitLabGroupAccess? ReportsGroupAccess { get; private set; }
    public GitLabGroupAccess? ModulesGroupAccess { get; private set; }
    public GitLabGroupAccess? ServerModulesGroupAccess { get; private set; }

    /// <summary>Доступ к группе, в которой лежит репозиторий типов.</summary>
    public GitLabGroupAccess? TypesProjectAccess { get; private set; }

    public async Task OnGetAsync(CancellationToken ct)
    {
        ViewData["Section"] = "settings";
        Form = AppSettings.Clone();
        await LoadFormAccessAsync(ct);
        await LoadLibrariesAsync(ct);
    }

    private async Task LoadLibrariesAsync(CancellationToken ct)
    {
        foreach (var (kind, form) in new[] { (ModuleKind.Client, ClientLibraries), (ModuleKind.Server, ServerLibraries) })
        {
            var spec = _libraries.For(kind);
            form.Mode = spec.Mode;
            form.SharedModule = spec.SharedModule;
            ModuleRepos[kind] = new();
            if (kind.GroupPath(AppSettings) is not { } group || CurrentSession.GitLabToken is null)
                continue;
            try
            {
                ModuleRepos[kind] = (await CreateGitLabClient().ListGroupProjectsAsync(group, null, ct) ?? new())
                    .Select(p => p.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
            }
            catch (GitLabApiException)
            {
                // подсказки необязательны
            }
        }
    }

    /// <summary>Сохранить режим библиотек: менять его может Developer и выше в группе модулей этого вида.</summary>
    public async Task<IActionResult> OnPostLibrariesAsync(CancellationToken ct)
    {
        var saved = new List<string>();
        if (await SaveLibrariesAsync(saved, ct) is { } error)
            TempData["FlashError"] = error;
        else
            TempData["Flash"] = saved.Count == 0 ? "Режим библиотек не изменился." : "Библиотеки модулей: " + string.Join("; ", saved) + ".";
        return RedirectToPage(null, null, null, "libraries");
    }

    /// <summary>Режим библиотек из общей формы; null - сохранено, в <paramref name="saved"/> - что изменилось.</summary>
    private async Task<string?> SaveLibrariesAsync(List<string> saved, CancellationToken ct)
    {
        var who = Who;
        foreach (var (kind, form, title) in new[] { (ModuleKind.Client, ClientLibraries, "клиентские"), (ModuleKind.Server, ServerLibraries, "серверные") })
        {
            var spec = _libraries.For(kind);
            var shared = form.SharedModule?.Trim() ?? "";
            if (spec.Mode == form.Mode && (form.Mode != AssemblySpecMode.SharedModule || spec.SharedModule == shared))
                continue;
            if (form.Mode == AssemblySpecMode.SharedModule && shared.Length == 0)
                return $"Модули ({title}): для режима «{AssemblySpec.ModeTitle(form.Mode)}» выберите общий модуль.";
            var access = await GetGroupAccessAsync(kind.GroupPath(AppSettings), fresh: false, ct);
            if (GitLabGroupAccess.CannotChange(access))
                return $"Модули ({title}): менять режим библиотек может Developer и выше в группе «{kind.GroupPath(AppSettings)}» (GitLab должен быть подключён, а доступ определён).";
            spec.Mode = form.Mode;
            if (form.Mode == AssemblySpecMode.SharedModule)
                spec.SharedModule = shared;
            _libraries.Save(kind, spec, who);
            saved.Add($"{title} - {AssemblySpec.ModeTitle(spec.Mode)}{(spec.IsShared ? $" (общий модуль «{spec.SharedModule}»)" : "")}");
        }
        return null;
    }

    private async Task LoadFormAccessAsync(CancellationToken ct)
    {
        GroupAccess = await GetGroupAccessAsync(Form.GitLabGroupPath, fresh: true, ct);
        ReportsGroupAccess = await GetGroupAccessAsync(Form.ReportsGroupPath, fresh: true, ct);
        ModulesGroupAccess = await GetGroupAccessAsync(Form.ModulesGroupPath, fresh: true, ct);
        ServerModulesGroupAccess = await GetGroupAccessAsync(Form.ServerModulesGroupPath, fresh: true, ct);
        TypesProjectAccess = await GetGroupAccessAsync(ProAdmin.Types.TypesRepository.GroupOf(Form.TypesProjectPath), fresh: true, ct);
    }

    /// <summary>Одна кнопка «Сохранить» внизу: общие настройки, служебные учётные записи (если их меняли) и режим библиотек.</summary>
    public async Task<IActionResult> OnPostAsync([FromServices] DeployAccounts accounts, CancellationToken ct)
    {
        ViewData["Section"] = "settings";
        Validate();
        if (Form.GrafanaUrl.Length > 0 && !SetupModel.TryWebUrl(Form.GrafanaUrl, out _))
            Errors.Add("Проверьте адрес Grafana.");
        if (Errors.Count > 0)
            return Page();

        GitLabLocation.TryParse(Form.GitLabGroupUrl, out var parsed, out _);
        var location = parsed!;
        var connected = CurrentSession.GitLabToken is { } token &&
                        string.Equals(token.BaseUrl, location.BaseUrl, StringComparison.OrdinalIgnoreCase);

        // Если GitLab подключён - убеждаемся, что адрес указывает на группу, а не на проект.
        if (connected)
        {
            try
            {
                var git = CreateGitLabClient();
                if (await git.FindGroupAsync(location.Path, ct) is null)
                {
                    Errors.Add(await git.FindProjectAsync(location.Path, ct) is not null
                        ? $"«{location.Path}» - это проект. Укажите группу, в которой лежат репозитории автоматизаций."
                        : $"Группа «{location.Path}» не найдена в GitLab (или нет доступа).");
                    return Page();
                }
                if (Form.ReportsGroupPath is { } reportsPath && await git.FindGroupAsync(reportsPath, ct) is null)
                {
                    Errors.Add($"Группа отчётов «{reportsPath}» не найдена в GitLab (или нет доступа).");
                    return Page();
                }
                if (Form.TypesProjectPath is { } typesPath && await git.FindProjectAsync(typesPath, ct) is null)
                {
                    Errors.Add(await git.FindGroupAsync(typesPath, ct) is not null
                        ? $"«{typesPath}» - это группа. Укажите проект (репозиторий) с типами, например {typesPath}/types."
                        : $"Репозиторий типов «{typesPath}» не найден в GitLab (или нет доступа). Создайте пустой проект - админка заполнит его на вкладке «Типы».");
                    return Page();
                }
                if (Form.ModulesGroupPath is { } modulesPath && await git.FindGroupAsync(modulesPath, ct) is null)
                {
                    Errors.Add($"Группа модулей «{modulesPath}» не найдена в GitLab (или нет доступа).");
                    return Page();
                }
            }
            catch (GitLabApiException ex)
            {
                Errors.Add($"GitLab: {ex.Message}");
                return Page();
            }
        }

        var before = AppSettings.Clone();
        Form.UpdatedAt = DateTimeOffset.UtcNow;
        Form.UpdatedBy = Who;
        SettingsStore.SaveGeneral(Form);
        // Значения не пишем (там адреса и пути), только что изменилось и кем.
        HttpContext.RequestServices.GetRequiredService<ILogger<SettingsModel>>().LogInformation(
            "Общие настройки изменены: {Login} с адреса {Address}; изменено: {Fields}", Who,
            HttpContext.Connection.RemoteIpAddress, ChangedFields(before, Form));
        var messages = new List<string> { connected ? "Настройки сохранены." : "Настройки сохранены. Группа будет проверена после подключения GitLab к этому серверу." };
        var problems = new List<string>();
        if (accounts.HasChanges(SettingsStore.Current, Deploy))
        {
            var notes = new List<string>();
            if (await SaveDeployAsync(accounts, notes, ct) is { } deployError)
                problems.AddRange(notes.Append("Служебные учётные записи не сохранены: " + deployError));
            else
                messages.AddRange(notes.Prepend("Служебные учётные записи Pilot сохранены."));
        }
        var libraries = new List<string>();
        if (await SaveLibrariesAsync(libraries, ct) is { } librariesError)
            problems.Add(librariesError);
        else if (libraries.Count > 0)
            messages.Add("Библиотеки модулей: " + string.Join("; ", libraries) + ".");
        TempData["Flash"] = string.Join(" ", messages);
        if (problems.Count > 0)
            TempData["FlashError"] = string.Join(" ", problems);

        return connected ? RedirectToPage() : RedirectToPage("/GitLab/Index", new { returnUrl = "/Settings" });
    }

    public async Task<IActionResult> OnPostCheckAsync(CancellationToken ct)
    {
        ViewData["Section"] = "settings";
        Validate();
        if (Errors.Count > 0)
            return Page();
        var groupPath = Form.GitLabGroupPath;
        GitLabLocation.TryParse(Form.GitLabGroupUrl, out var location, out _);
        var gitConnected = CurrentSession.GitLabToken is { } token &&
                           string.Equals(token.BaseUrl, location!.BaseUrl, StringComparison.OrdinalIgnoreCase);

        if (Form.StagePilotUrl.Length > 0)
            CheckResults.Add(("Стейдж", "info", $"Pilot {Form.StagePilotUrl}: проверяется на вкладке «Модули» (вход пользователем базы)."));

        var envChecks = Form.Environments.Where(e => e.HasServer).Select(async env =>
        {
            try
            {
                var list = await _pilot.GetAutomationsAsync(env.PilotUrl, Vault.GetPilotLogin(CurrentSession, env.Key), ct);
                return (env.Title, "ok", $"Pilot {env.PilotUrl}: доступен, автоматизаций - {list.Count}.");
            }
            catch (PilotCredentialsRequiredException)
            {
                return (env.Title, "warn", $"Pilot {env.PilotUrl}: не задана служебная учётная запись контура - проверка пропущена.");
            }
            catch (PilotGatewayException ex)
            {
                return (env.Title, "error", $"Pilot {env.PilotUrl}: {ex.Message}");
            }
        }).ToList();

        foreach (var r in await Task.WhenAll(envChecks))
            CheckResults.Add(r);

        if (!gitConnected || groupPath is null)
        {
            CheckResults.Add(("GitLab", "warn", "GitLab не подключён к этому серверу - проверка группы пропущена."));
            return Page();
        }

        await LoadFormAccessAsync(ct);
        foreach (var (title, access) in new[] { ("GitLab, доступ к автоматизациям", GroupAccess), ("GitLab, доступ к отчётам", ReportsGroupAccess), ("GitLab, доступ к модулям", ModulesGroupAccess) })
            if (access is not null)
                CheckResults.Add((title, access.Error is not null ? "warn" : access.ReadOnly || !access.GroupFound ? "error" : "ok", $"Группа {access.GroupPath}: {access.Summary}."));

        if (Form.ReportsGroupPath is { } reportsGroup)
        {
            try
            {
                var git = CreateGitLabClient();
                var reportFolders = await AutomationRepositories.ListFoldersAsync(git, reportsGroup, ct);
                var reportRepos = await AutomationRepositories.ListAsync(git, reportsGroup, ct);
                CheckResults.Add(("GitLab, отчёты", "ok", $"Группа {reportsGroup}: репозиториев отчётов - {reportRepos.Count}, папок - {reportFolders.Count - 1}."));
            }
            catch (GitLabApiException ex)
            {
                CheckResults.Add(("GitLab, отчёты", "error", ex.Message));
            }
        }

        try
        {
            var git = CreateGitLabClient();
            var folders = await AutomationRepositories.ListFoldersAsync(git, groupPath, ct);
            var repos = await AutomationRepositories.ListAsync(git, groupPath, ct);
            CheckResults.Add(("GitLab", "ok",
                $"Группа {groupPath}: репозиториев автоматизаций - {repos.Count}, папок - {folders.Count - 1}" +
                (folders.Count > 1 ? $" ({string.Join(", ", folders.Skip(1).Select(f => f.Folder))})." : ".")));
        }
        catch (GitLabApiException ex)
        {
            CheckResults.Add(("GitLab", "error", ex.Message));
        }

        return Page();
    }

    private static string ChangedFields(AdminSettings before, AdminSettings after)
    {
        var changed = typeof(AdminSettings).GetProperties()
            .Where(p => p.CanWrite && p.Name is not (nameof(AdminSettings.UpdatedAt) or nameof(AdminSettings.UpdatedBy)))
            .Where(p => System.Text.Json.JsonSerializer.Serialize(p.GetValue(before)) != System.Text.Json.JsonSerializer.Serialize(p.GetValue(after)))
            .Select(p => p.Name)
            .ToList();
        return changed.Count == 0 ? "ничего" : string.Join(", ", changed);
    }

    private void Validate()
    {
        // Значения, которые попадают в CI-конфигурацию репозиториев, проверяются до Normalize (он не должен их «чинить» молча).
        if (!AdminSettings.IsValidJobName(Form.ModulesBuildJob?.Trim()) && !string.IsNullOrWhiteSpace(Form.ModulesBuildJob))
            Errors.Add("Имя job сборки модуля: латинские буквы, цифры, «_» и «-», начинается с буквы или «_» (до 64 символов).");
        foreach (var (title, path) in new[] { ("Конфигурация CI автоматизаций", Form.AutomationsCiConfigPath), ("Конфигурация CI отчётов", Form.ReportsCiConfigPath) })
            if (!AdminSettings.IsValidCiConfigPath(path))
                Errors.Add($"{title}: путь вида «файл.yml» или «файл.yml@группа/проект» (латинские буквы, цифры, «_», «-», «.», «/»).");
        Form.Normalize();

        foreach (var env in Form.Environments.Where(e => e.HasServer))
        {
            if (!PilotAddress.TryParse(env.PilotUrl, out var address, out var error))
                Errors.Add($"{env.Title}: {error}");
            else if (address!.Database.Length == 0)
                Errors.Add($"{env.Title}: укажите базу в адресе - http://host:порт/имя_базы.");
        }

        var branches = Form.Environments.Where(e => e.Branch.Length > 0).GroupBy(e => e.Branch).Where(g => g.Count() > 1);
        foreach (var g in branches)
            Errors.Add($"Ветка «{g.Key}» указана для нескольких контуров: {string.Join(", ", g.Select(e => e.Title))}.");

        if (!GitLabLocation.TryParse(Form.GitLabGroupUrl, out var automationsLocation, out var gitError))
            Errors.Add(gitError!);

        if (Form.StagePilotUrl.Length > 0)
        {
            if (!PilotAddress.TryParse(Form.StagePilotUrl, out var stage, out var stageError))
                Errors.Add($"Стейдж: {stageError}");
            else if (stage!.Database.Length == 0)
                Errors.Add("Стейдж: укажите базу в адресе - http://host:порт/имя_базы.");
        }

        try
        {
            _ = new System.Text.RegularExpressions.Regex(Form.TaskPattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
        }
        catch (ArgumentException ex)
        {
            Errors.Add($"Номер задачи: некорректное регулярное выражение ({ex.Message}).");
        }

        if (Form.TaskUrlTemplate.Length > 0 &&
            ProAdmin.Infrastructure.SafeUrl.Http(Form.TaskUrlTemplate.Replace("{task}", "KTN-1").Replace("{number}", "1")) is null)
            Errors.Add("Ссылка на задачу: адрес должен начинаться с http:// или https://.");

        if (Form.ModulesGitLabGroupUrl.Length > 0)
        {
            if (!GitLabLocation.TryParse(Form.ModulesGitLabGroupUrl, out var modulesLocation, out var modulesError))
                Errors.Add($"Группа модулей: {modulesError}");
            else if (automationsLocation is not null && !string.Equals(modulesLocation!.BaseUrl, automationsLocation.BaseUrl, StringComparison.OrdinalIgnoreCase))
                Errors.Add("Группа модулей должна быть на том же сервере GitLab, что и группа автоматизаций.");
        }

        if (Form.ServerModulesGitLabGroupUrl.Length > 0)
        {
            if (!GitLabLocation.TryParse(Form.ServerModulesGitLabGroupUrl, out var serverModulesLocation, out var serverModulesError))
                Errors.Add($"Группа серверных модулей: {serverModulesError}");
            else if (automationsLocation is not null && !string.Equals(serverModulesLocation!.BaseUrl, automationsLocation.BaseUrl, StringComparison.OrdinalIgnoreCase))
                Errors.Add("Группа серверных модулей должна быть на том же сервере GitLab, что и группа автоматизаций.");
        }

        if (Form.TypesGitLabProjectUrl.Length > 0)
        {
            if (!GitLabLocation.TryParse(Form.TypesGitLabProjectUrl, out var typesLocation, out var typesError))
                Errors.Add($"Репозиторий типов: {typesError}");
            else if (automationsLocation is not null && !string.Equals(typesLocation!.BaseUrl, automationsLocation.BaseUrl, StringComparison.OrdinalIgnoreCase))
                Errors.Add("Репозиторий типов должен быть на том же сервере GitLab, что и группа автоматизаций.");
            else if (!typesLocation!.Path.Contains('/'))
                Errors.Add("Репозиторий типов: укажите проект внутри группы, например http://localhost:8080/pilot-config/types.");
        }

        if (Form.ReportsGitLabGroupUrl.Length > 0)
        {
            if (!GitLabLocation.TryParse(Form.ReportsGitLabGroupUrl, out var reportsLocation, out var reportsError))
                Errors.Add($"Группа отчётов: {reportsError}");
            else if (automationsLocation is not null && !string.Equals(reportsLocation!.BaseUrl, automationsLocation.BaseUrl, StringComparison.OrdinalIgnoreCase))
                Errors.Add("Группа отчётов должна быть на том же сервере GitLab, что и группа автоматизаций.");
        }
    }
}
