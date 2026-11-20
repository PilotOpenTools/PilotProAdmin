using ProAdmin.Jobs;
using ProAdmin.Modules;
using ProAdmin.Pilot;
using Microsoft.AspNetCore.Mvc;

namespace ProAdmin.Pages.OpenTools;

/// <summary>
/// Модули PilotOpenTools (github.com/PilotOpenTools): версия на тесте, стейдже и проде против последнего релиза GitHub,
/// установка и обновление из релиза. ServerHost - только администратору админки, вступает в силу после перезапуска сервера.
/// </summary>
public class IndexModel : AppPageModel
{
    private readonly OpenToolsService _service;
    private readonly ILogger<IndexModel> _logger;

    public IndexModel(OpenToolsService service, ILogger<IndexModel> logger)
    {
        _service = service;
        _logger = logger;
    }

    [BindProperty(SupportsGet = true)] public bool Refresh { get; set; }
    [BindProperty] public string? Tool { get; set; }
    [BindProperty] public string? TargetEnv { get; set; }

    public OpenToolsState? State { get; private set; }
    public string? SetupProblem { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        if (Prepare() is { } redirect)
            return redirect;
        if (SetupProblem is null)
        {
            await LoadModulesAccessAsync(ct);
            State = await _service.LoadAsync(AppSettings, ModuleLogin, Refresh, ct);
        }
        return Page();
    }

    /// <summary>Один модуль на одном сервере.</summary>
    public Task<IActionResult> OnPostInstallAsync(CancellationToken ct) =>
        RunAsync(ct, state =>
        {
            var tool = OpenToolsCatalog.Find(Tool);
            var row = state.Rows.FirstOrDefault(r => r.Tool == tool);
            return row is null || TargetEnv is null ? Enumerable.Empty<(OpenToolRow, string)>() : new[] { (row, TargetEnv) };
        });

    /// <summary>Один модуль на всех серверах, где его нет или он старше релиза.</summary>
    public Task<IActionResult> OnPostInstallRowAsync(CancellationToken ct) =>
        RunAsync(ct, state => state.Rows.Where(r => r.Tool == OpenToolsCatalog.Find(Tool)).SelectMany(r => r.Pending.Select(e => (r, e))));

    /// <summary>Все модули на всех серверах: сначала PilotClientSdk, потом модули на нём.</summary>
    public Task<IActionResult> OnPostInstallAllAsync(CancellationToken ct) =>
        RunAsync(ct, state => state.Rows.SelectMany(r => r.Pending.Select(e => (r, e))));

    private async Task<IActionResult> RunAsync(CancellationToken ct, Func<OpenToolsState, IEnumerable<(OpenToolRow Row, string Env)>> select)
    {
        if (Prepare() is { } redirect)
            return redirect;
        if (SetupProblem is not null)
            return RedirectToPage();
        if (await DenyModulesChangeAsync(ct) is { } deny)
            return deny;

        var state = await _service.LoadAsync(AppSettings, ModuleLogin, refresh: false, ct);
        var targets = select(state).ToList();
        if (targets.Count == 0)
        {
            TempData["Flash"] = "Устанавливать нечего: на всех доступных серверах версии не старше релизов GitHub.";
            return RedirectToPage();
        }

        var done = new List<string>();
        var failed = new List<string>();
        foreach (var (row, envKey) in targets)
        {
            var env = state.Envs.FirstOrDefault(e => e.Key == envKey);
            if (env is null || !row.CanInstall(envKey))
            {
                failed.Add($"{row.Tool.Title} на {env?.Title ?? envKey}: устанавливать нечего");
                continue;
            }
            if (row.Tool.Kind == OpenToolKind.ServerHost && !IsAdmin)
            {
                _logger.LogWarning("Отказ: обновление ServerHost из PilotOpenTools - {Login} не администратор админки", CurrentSession.Actor);
                failed.Add($"ServerHost на {env.Title}: обновлять центральный серверный модуль может только администратор админки");
                continue;
            }
            try
            {
                done.Add(await _service.InstallAsync(AppSettings, row.Tool, env, ModuleLogin(env.Key), CurrentSession.PilotLogin,
                    CurrentSession.GitLabUser?.Username, ct));
            }
            catch (Exception ex) when (ex is WorkflowException or PilotGatewayException)
            {
                failed.Add($"{row.Tool.Title} на {env.Title}: {ex.Message}");
            }
        }

        if (done.Count > 0)
            TempData["Flash"] = string.Join(" ", done);
        if (failed.Count > 0)
            TempData["FlashError"] = string.Join("; ", failed);
        return RedirectToPage();
    }

    private IActionResult? Prepare()
    {
        ViewData["Section"] = "opentools";
        if (!GitLabConnected)
            return RedirectToGitLab();
        SetupProblem = ModuleEnv.All(AppSettings).Count == 0 ? "Не задан ни один сервер Pilot (тест, стейдж, прод)." : null;
        return null;
    }

    public static string SyncLabel(OpenToolCell cell, OpenToolRow row) => cell.Sync switch
    {
        OpenToolSync.Missing when row.Tool.Kind == OpenToolKind.ServerHost => "не установлен",
        OpenToolSync.Missing => "нет на сервере",
        OpenToolSync.Outdated => "есть новее",
        OpenToolSync.Current => "актуален",
        OpenToolSync.Newer => "новее релиза",
        OpenToolSync.Disabled => "отключён",
        _ => cell.Error is not null ? "сервер недоступен" : row.Release.Package is null ? "нет релиза" : "версия не прочитана",
    };

    public static string SyncCss(OpenToolSync sync) => sync switch
    {
        OpenToolSync.Current => "badge-synced",
        OpenToolSync.Newer => "badge-onlypilot",
        OpenToolSync.Outdated => "badge-differs",
        OpenToolSync.Missing or OpenToolSync.Disabled => "badge-onlygit",
        _ => "",
    };

    public static string SyncHint(OpenToolCell cell, OpenToolRow row) => cell.Sync switch
    {
        OpenToolSync.Missing when row.Tool.Kind == OpenToolKind.ServerHost =>
            "Объекта ServerHost в базе нет: первую установку хоста делают по инструкции, дальше его можно обновлять отсюда",
        OpenToolSync.Missing => "Модуля нет в папке расширений: установка создаст объект, назначить его пользователям нужно в Pilot-myAdmin",
        OpenToolSync.Outdated => $"На сервере {cell.Version}, в релизе GitHub {row.Release.Version}",
        OpenToolSync.Current => "Версия на сервере совпадает с последним релизом GitHub",
        OpenToolSync.Newer => "На сервере версия новее последнего релиза GitHub - выложена из GitLab или вручную; админка её не трогает",
        OpenToolSync.Disabled => "Модуль отключён на этом контуре (объект в корзине Pilot) - включите его на странице «Модули»",
        _ => cell.Error ?? row.Release.Error ?? "Версия модуля на сервере не записана в атрибуте «Additional»",
    };
}
