using ProAdmin.Compare;
using ProAdmin.Settings;
using Microsoft.AspNetCore.Mvc;

namespace ProAdmin.Pages.Compare;

/// <summary>Различия между двумя контурами (по умолчанию тест / прод): автоматизации и отчёты.</summary>
public class IndexModel : AppPageModel
{
    private readonly CompareService _compare;

    public IndexModel(CompareService compare) => _compare = compare;

    [BindProperty(SupportsGet = true)] public string Kind { get; set; } = "automations";
    [BindProperty(SupportsGet = true)] public string? A { get; set; }
    [BindProperty(SupportsGet = true)] public string? B { get; set; }

    public EnvironmentSettings? EnvA { get; private set; }
    public EnvironmentSettings? EnvB { get; private set; }
    public CompareResult? Result { get; private set; }
    public string? Problem { get; private set; }
    public bool IsReports => Kind == "reports";

    public IEnumerable<EnvironmentSettings> Servers => AppSettings.Environments.Where(e => e.HasServer);

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        ViewData["Section"] = "compare";
        if (!GitLabConnected)
            return RedirectToGitLab();

        Kind = Kind == "reports" ? "reports" : "automations";
        var (defA, defB) = CompareService.DefaultPair(AppSettings);
        EnvA = AppSettings.Find(A) is { HasServer: true } a ? a : defA;
        EnvB = AppSettings.Find(B) is { HasServer: true } b ? b : defB;

        if (EnvA is null || EnvB is null || EnvA.Key == EnvB.Key)
        {
            Problem = "Для сравнения нужны два разных контура с серверами Pilot - проверьте настройки.";
            return Page();
        }
        if (IsReports && ReportsGroupPath is null)
        {
            Problem = "Не указана группа GitLab с отчётами (Настройки).";
            return Page();
        }

        var git = CreateGitLabClient();
        Result = IsReports
            ? await _compare.ReportsAsync(AppSettings, EnvA, EnvB, ReportLogin, git, ct)
            : await _compare.AutomationsAsync(AppSettings, EnvA, EnvB, env => Vault.GetPilotLogin(CurrentSession, env), git, ct);
        return Page();
    }

    public string Link(string? kind = null, string? a = null, string? b = null) =>
        $"/Compare?kind={kind ?? Kind}&a={a ?? EnvA?.Key}&b={b ?? EnvB?.Key}";
}
