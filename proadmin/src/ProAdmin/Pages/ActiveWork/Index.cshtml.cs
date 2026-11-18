using ProAdmin.ActiveWork;
using ProAdmin.Modules;
using Microsoft.AspNetCore.Mvc;

namespace ProAdmin.Pages.ActiveWork;

/// <summary>
/// Таблица «Активные работы» для вкладок Автоматизации / Отчёты / Модули / Серверные модули: репозитории группы раздела
/// и ветки, в которых за период появились коммиты. Вкладки подгружают её фрагментом (partial=1), чтобы не ждать GitLab.
/// </summary>
public class IndexModel : AppPageModel
{
    public const int DefaultDays = 14;

    private readonly ActiveWorkService _service;

    public IndexModel(ActiveWorkService service) => _service = service;

    /// <summary>automations | reports | modules | servermodules.</summary>
    [BindProperty(SupportsGet = true)] public string? Area { get; set; }

    /// <summary>Последние N дней (по умолчанию 14); игнорируется, если задан From.</summary>
    [BindProperty(SupportsGet = true)] public int? Days { get; set; }

    [BindProperty(SupportsGet = true)] public DateOnly? From { get; set; }
    [BindProperty(SupportsGet = true)] public DateOnly? To { get; set; }

    /// <summary>Смещение часового пояса браузера, как Date.getTimezoneOffset() (минуты, UTC − местное).</summary>
    [BindProperty(SupportsGet = true)] public int? Tz { get; set; }

    /// <summary>Этап (ветка): test | stage | prod | hotfix | work; пусто - все ветки.</summary>
    [BindProperty(SupportsGet = true)] public string? Stage { get; set; }

    [BindProperty(SupportsGet = true)] public bool Fragment { get; set; }
    [BindProperty(SupportsGet = true)] public bool Fresh { get; set; }

    public string AreaTitle { get; private set; } = "";
    public string? AreaGroupPath { get; private set; }
    public string? Problem { get; private set; }
    public ActiveWorkResult? Result { get; private set; }
    public TimeSpan Offset { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        ViewData["Section"] = Area;
        (AreaTitle, AreaGroupPath) = (Area ?? "").ToLowerInvariant() switch
        {
            "automations" => ("Автоматизации", GroupPath),
            "reports" => ("Отчёты", ReportsGroupPath),
            "modules" => ("Модули", ModulesGroupPath),
            "servermodules" => ("Серверные модули", AppSettings.ServerModulesGroupPath),
            _ => ("", null),
        };
        if (AreaTitle.Length == 0)
            return NotFound();

        Offset = TimeSpan.FromMinutes(-Math.Clamp(Tz ?? 0, -14 * 60, 14 * 60));
        var now = DateTimeOffset.UtcNow;
        DateTimeOffset since, until;
        if (From is { } from)
        {
            var to = To is { } t && t >= from ? t : DateOnly.FromDateTime(now.ToOffset(Offset).Date);
            since = new DateTimeOffset(from.ToDateTime(TimeOnly.MinValue), Offset);
            until = new DateTimeOffset(to.ToDateTime(TimeOnly.MaxValue), Offset);
            From = from;
            To = to;
        }
        else
        {
            Days = Math.Clamp(Days ?? DefaultDays, 1, 366);
            since = now.AddDays(-Days.Value);
            until = now;
        }

        if (AreaGroupPath is null)
            Problem = Area == "servermodules"
                ? "Группа GitLab с серверными модулями не задана - укажите её в настройках."
                : "Группа GitLab этого раздела не задана в настройках.";
        else if (!GitLabConnected)
            Problem = "GitLab не подключён.";
        else
            Result = await _service.LoadAsync(CreateGitLabClient(), CurrentSession.GitLabUser?.Id ?? 0, AreaGroupPath, since, until,
                AppSettings.TaskPattern, Fresh, ct);

        return Page();
    }

    public string Local(DateTimeOffset? value, string format = "dd.MM.yyyy HH:mm") =>
        value is { } v ? v.ToOffset(Offset).ToString(format) : "-";

    public string? TaskUrl(string task) => ModuleBranches.TaskUrl(task, AppSettings.TaskUrlTemplate);

    /// <summary>Ветка относится к выбранному этапу (без выбора - любая).</summary>
    public bool InStage(string branch) => string.IsNullOrEmpty(Stage) || BranchClass(branch) == "branch-" + Stage.ToLowerInvariant();

    /// <summary>Класс бейджа ветки: main - прод, release/* - стейдж, dev - тест, прочие - рабочие.</summary>
    public static string BranchClass(string branch) => branch switch
    {
        "main" or "master" => "branch-prod",
        "dev" or "develop" => "branch-test",
        _ when branch.StartsWith("release/", StringComparison.OrdinalIgnoreCase) => "branch-stage",
        _ when branch.StartsWith("hotfix/", StringComparison.OrdinalIgnoreCase) => "branch-hotfix",
        _ => "branch-work",
    };
}
