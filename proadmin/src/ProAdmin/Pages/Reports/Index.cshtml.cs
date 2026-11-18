using ProAdmin.GitLab;
using ProAdmin.Pilot;
using ProAdmin.Reports;
using ProAdmin.Sessions;
using Microsoft.AspNetCore.Mvc;

namespace ProAdmin.Pages.Reports;

/// <summary>
/// Отчёты: серверы Pilot (тест / dev, прод / main) против репозиториев группы отчётов.
/// Отчёты здесь не редактируются - только перенос dev / main / прод и создание репозитория.
/// </summary>
public class IndexModel : AppPageModel
{
    private readonly ReportCatalog _catalog;
    private readonly PilotReportGateway _pilot;

    public IndexModel(ReportCatalog catalog, PilotReportGateway pilot)
    {
        _catalog = catalog;
        _pilot = pilot;
    }

    [BindProperty] public string? CredEnv { get; set; }
    [BindProperty] public string? EnvLogin { get; set; }
    [BindProperty] public string? EnvPassword { get; set; }

    public ReportCatalogResult? Catalog { get; private set; }
    public string? SetupProblem { get; private set; }
    public string? CredentialError { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        if (Prepare() is { } redirect)
            return redirect;
        if (SetupProblem is null)
        {
            await LoadReportsAccessAsync(ct);
            await LoadAsync(ct);
        }
        return Page();
    }

    /// <summary>Логин/пароль пользователя базы для отчётов на сервере контура.</summary>
    public async Task<IActionResult> OnPostCredentialsAsync(CancellationToken ct)
    {
        if (Prepare() is { } redirect)
            return redirect;
        if (BlockedCredentialAttempt() is { } blocked)
            return blocked;
        var env = ReportCatalog.FindEnv(AppSettings, CredEnv);
        if (env is null || SetupProblem is not null)
            return RedirectToPage();

        var login = new PilotLogin(EnvLogin?.Trim() ?? "", EnvPassword ?? "");
        try
        {
            await _pilot.ListAsync(env.PilotUrl, login, AppSettings.ReportsLicenseType, withContent: false, ct);
            Vault.SetEnvironmentCredential(CurrentSession, SessionVault.ReportsPrefix + env.Key, login);
            return RedirectToPage();
        }
        catch (PilotGatewayException ex)
        {
            CredentialAttemptFailed(env.Key);
            CredentialError = $"{env.Title}: {ex.Message}";
            await LoadAsync(ct);
            return Page();
        }
    }

    private IActionResult? Prepare()
    {
        ViewData["Section"] = "reports";
        if (!GitLabConnected)
            return RedirectToGitLab();
        SetupProblem = CheckSetup();
        return null;
    }

    /// <summary>Причина, по которой вкладка не может работать; null - всё настроено.</summary>
    private string? CheckSetup()
    {
        if (string.IsNullOrWhiteSpace(AppSettings.ReportsGitLabGroupUrl))
            return "Не указана группа GitLab с отчётами.";
        if (!GitLabLocation.TryParse(AppSettings.ReportsGitLabGroupUrl, out var reports, out var error))
            return error;
        if (GitLabLocation.TryParse(AppSettings.GitLabGroupUrl, out var automations, out _) &&
            !string.Equals(reports!.BaseUrl, automations!.BaseUrl, StringComparison.OrdinalIgnoreCase))
            return $"Группа отчётов на другом сервере GitLab ({reports.BaseUrl}), чем группа автоматизаций ({automations.BaseUrl}). Вход в GitLab один - укажите группы на одном сервере.";
        if (!ReportCatalog.ReportEnvs(AppSettings).Any())
            return "Нет ни одного контура с сервером Pilot и веткой.";
        return null;
    }

    private async Task LoadAsync(CancellationToken ct) =>
        Catalog = await _catalog.LoadAsync(AppSettings, ReportLogin, CreateGitLabClient(), ct);
}
