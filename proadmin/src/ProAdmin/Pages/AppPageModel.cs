using ProAdmin.GitLab;
using ProAdmin.Sessions;
using ProAdmin.Settings;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace ProAdmin.Pages;

/// <summary>База страниц: текущая сессия, общие настройки, клиент GitLab.</summary>
public abstract class AppPageModel : PageModel
{
    private UserSession? _session;
    private AdminSettings? _settings;

    protected SessionVault Vault => HttpContext.RequestServices.GetRequiredService<SessionVault>();
    protected SettingsStore SettingsStore => HttpContext.RequestServices.GetRequiredService<SettingsStore>();

    public UserSession CurrentSession =>
        _session ??= Vault.Get(User.FindFirst(SessionVault.SidClaim)?.Value)
                     ?? throw new InvalidOperationException("Сессия не найдена - войдите заново.");

    public AdminSettings AppSettings => _settings ??= SettingsStore.Current;

    public bool GitHubTypesMode => HttpContext.RequestServices
        .GetRequiredService<Microsoft.Extensions.Options.IOptions<ProAdmin.Infrastructure.AppOptions>>().Value.GitHubTypes.IsConfigured;

    /// <summary>Ключи выкладки из CI по контурам (MODULES_DEPLOY_KEY_*).</summary>
    protected ProAdmin.Infrastructure.CiDeployOptions CiDeployOptions => HttpContext.RequestServices
        .GetRequiredService<Microsoft.Extensions.Options.IOptions<ProAdmin.Infrastructure.AppOptions>>().Value.CiDeploy;

    public bool GitLabConnected =>!GitHubTypesMode && CurrentSession.IsGitLabConnected(AppSettings);

    /// <summary>Путь группы автоматизаций в GitLab (например «automation»).</summary>
    public string? GroupPath => AppSettings.GitLabGroupPath;

    /// <summary>Путь группы отчётов в GitLab (например «reports»); null - не настроена.</summary>
    public string? ReportsGroupPath => AppSettings.ReportsGroupPath;

    /// <summary>
    /// Путь группы модулей в GitLab (например «modules»); null - не настроена. Страницы серверных модулей
    /// подставляют группу серверных модулей - по ней же проверяется доступ (Developer и выше).
    /// </summary>
    public virtual string? ModulesGroupPath => AppSettings.ModulesGroupPath;

    /// <summary>Куда вернуть пользователя без прав на изменение модулей (параметры страницы списка).</summary>
    protected virtual object? ModulesListRoute => null;

    private static readonly TimeSpan AccessCacheTtl = TimeSpan.FromMinutes(2);

    /// <summary>Доступ к группе автоматизаций; заполняется <see cref="LoadAutomationsAccessAsync"/>.</summary>
    public GitLabGroupAccess? AutomationsAccess { get; private set; }

    /// <summary>Доступ к группе отчётов; заполняется <see cref="LoadReportsAccessAsync"/>.</summary>
    public GitLabGroupAccess? ReportsAccess { get; private set; }

    /// <summary>У пользователя в группе автоматизаций только чтение или доступ не определён (GitLab не подключён, группа не задана) - кнопки изменения скрываются.</summary>
    public bool AutomationsReadOnly => GitLabGroupAccess.CannotChange(AutomationsAccess);

    public bool ReportsReadOnly => GitLabGroupAccess.CannotChange(ReportsAccess);

    /// <summary>Доступ к группе модулей; заполняется <see cref="LoadModulesAccessAsync"/>.</summary>
    public GitLabGroupAccess? ModulesAccess { get; private set; }

    public bool ModulesReadOnly => GitLabGroupAccess.CannotChange(ModulesAccess);

    protected async Task<GitLabGroupAccess?> LoadModulesAccessAsync(CancellationToken ct) =>
        ModulesAccess ??= await GetGroupAccessAsync(ModulesGroupPath, fresh: false, ct);

    /// <summary>Если права на изменение модулей не доказаны (только чтение, GitLab не подключён, ошибка GitLab) - редирект на список с объяснением; иначе null.</summary>
    protected async Task<IActionResult?> DenyModulesChangeAsync(CancellationToken ct)
    {
        if (!GitLabGroupAccess.CannotChange(await LoadModulesAccessAsync(ct)))
            return null;
        TempData["FlashError"] = ChangeDeniedText("модули", ModulesAccess, ModulesGroupPath);
        return RedirectToPage("/Modules/Index", ModulesListRoute);
    }

    /// <summary>
    /// Почему изменения запрещены: проверка прав «fail-closed» - без подключённого GitLab, без заданной группы и при
    /// ошибке определения роли действие, меняющее GitLab или серверы, не выполняется.
    /// </summary>
    protected string ChangeDeniedText(string what, GitLabGroupAccess? access, string? groupPath)
    {
        if (!GitLabConnected)
            return $"Изменять {what} нельзя: GitLab не подключён к этому серверу - права проверить нечем. Войдите в GitLab.";
        if (access is null)
            return $"Изменять {what} нельзя: группа GitLab не задана в «Настройках» - права проверить нечем.";
        if (access.Error is not null)
            return $"Изменять {what} нельзя: не удалось определить ваш доступ к группе «{groupPath}» в GitLab ({access.Error}). Повторите позже.";
        return $"Изменять {what} нельзя: ваш доступ к группе «{groupPath}» в GitLab - {access.LevelTitle}. Нужен Developer или выше.";
    }

    private ProAdmin.Infrastructure.AuthFailureLimiter Limiter =>
        HttpContext.RequestServices.GetRequiredService<ProAdmin.Infrastructure.AuthFailureLimiter>();

    /// <summary>
    /// Ввод логина и пароля администратора для контура проверяется на сервере Pilot, то есть это тоже возможность
    /// перебора: с адреса, где за 5 минут было 10 неудач, ответ 429. Вызывайте до проверки учётки.
    /// </summary>
    protected IActionResult? BlockedCredentialAttempt() =>
        Limiter.IsBlocked(ProAdmin.Infrastructure.AuthFailureLimiter.LoginScope, HttpContext.Connection.RemoteIpAddress?.ToString())
            ? StatusCode(StatusCodes.Status429TooManyRequests)
            : null;

    /// <summary>Учётка контура не принята сервером Pilot: в счётчик перебора и в журнал.</summary>
    protected void CredentialAttemptFailed(string env)
    {
        var address = HttpContext.Connection.RemoteIpAddress?.ToString();
        HttpContext.RequestServices.GetRequiredService<ILogger<AppPageModel>>().LogWarning(
            "Учётка контура {Env} не принята: сессия {Login}, адрес {Address}", env, CurrentSession.Actor, address);
        Limiter.Fail(ProAdmin.Infrastructure.AuthFailureLimiter.LoginScope, address);
    }

    protected Pilot.PilotLogin ModuleLogin(string env) => Vault.GetModuleLogin(CurrentSession, env);

    protected async Task<GitLabGroupAccess?> LoadAutomationsAccessAsync(CancellationToken ct) =>
        AutomationsAccess ??= await GetGroupAccessAsync(GroupPath, fresh: false, ct);

    protected async Task<GitLabGroupAccess?> LoadReportsAccessAsync(CancellationToken ct) =>
        ReportsAccess ??= await GetGroupAccessAsync(ReportsGroupPath, fresh: false, ct);

    /// <summary>Уровень доступа текущего пользователя GitLab к группе; null - GitLab не подключён или группа не задана.</summary>
    protected async Task<GitLabGroupAccess?> GetGroupAccessAsync(string? groupPath, bool fresh, CancellationToken ct)
    {
        if (groupPath is null || !GitLabConnected)
            return null;
        var key = $"{CurrentSession.GitLabUser?.Id}:{groupPath}";
        if (!fresh && CurrentSession.GroupAccessCache.TryGetValue(key, out var cached) && DateTimeOffset.UtcNow - cached.At < AccessCacheTtl)
            return cached.Access;

        var access = await GitLabGroupAccess.LoadAsync(CreateGitLabClient(), groupPath, CurrentSession.GitLabUser, ct);
        if (access.Error is null)
            CurrentSession.GroupAccessCache[key] = (access, DateTimeOffset.UtcNow);
        return access;
    }

    /// <summary>Если права на изменение автоматизаций не доказаны - редирект на список с объяснением; иначе null.</summary>
    protected async Task<IActionResult?> DenyAutomationsChangeAsync(CancellationToken ct)
    {
        if (!GitLabGroupAccess.CannotChange(await LoadAutomationsAccessAsync(ct)))
            return null;
        TempData["FlashError"] = ChangeDeniedText("автоматизации", AutomationsAccess, GroupPath);
        return RedirectToPage("/Automations/Index");
    }

    /// <summary>Если права на изменение отчётов не доказаны - редирект на список с объяснением; иначе null.</summary>
    protected async Task<IActionResult?> DenyReportsChangeAsync(CancellationToken ct)
    {
        if (!GitLabGroupAccess.CannotChange(await LoadReportsAccessAsync(ct)))
            return null;
        TempData["FlashError"] = ChangeDeniedText("отчёты", ReportsAccess, ReportsGroupPath);
        return RedirectToPage("/Reports/Index");
    }

    /// <summary>
    /// Администратор админки (<see cref="Sessions.UserSession.IsAdmin"/>): только он меняет общие настройки, всё в разделе
    /// «Миграция» и настройки серверов. Остальные видят эти страницы только для чтения.
    /// </summary>
    public bool IsAdmin => CurrentSession.IsAdmin(AppSettings);

    /// <summary>С Pilot работаем служебными учётками из настроек (вход через GitLab), а не своим входом в Pilot.</summary>
    public bool UsesServiceAccounts => CurrentSession.UsesServiceAccounts;

    protected Pilot.PilotLogin ReportLogin(string env) => Vault.GetReportLogin(CurrentSession, env);

    protected GitLabClient CreateGitLabClient() =>
        HttpContext.RequestServices.GetRequiredService<GitLabClientFactory>().Create(CurrentSession.GitLabToken!);

    protected IActionResult RedirectToGitLab() =>
        RedirectToPage("/Login", new { returnUrl = Request.Path + Request.QueryString });
}
