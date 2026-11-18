using ProAdmin.Migration;
using Microsoft.AspNetCore.Mvc;

namespace ProAdmin.Pages.Migration;

public class IndexModel : AppPageModel
{
    private readonly MigrationClient _client;

    public IndexModel(MigrationClient client) => _client = client;

    [BindProperty]
    public RequestForm Form { get; set; } = new();

    /// <summary>Имя базы приёмника, введённое для подтверждения полной копии.</summary>
    [BindProperty]
    public string? Confirmation { get; set; }

    public bool Configured => _client.IsConfigured;
    public string? ServiceError { get; private set; }
    public MigrationStatus? Status { get; private set; }
    public List<MigrationRunDto> Runs { get; private set; } = new();
    public List<ScheduleDto> Schedules { get; private set; } = new();
    public MigrationPlanDto? Plan { get; private set; }
    public List<MigrationEnvDto> Envs => Status?.Environments ?? new();

    /// <summary>Присланные файловые базы в каталоге импорта сервиса.</summary>
    public ImportSourcesDto? Import { get; private set; }

    /// <summary>Готовые выгрузки в файлы; null - сервис их не поддерживает.</summary>
    public ExportListDto? Exports { get; private set; }

    public async Task OnGetAsync(string? source, string? target, CancellationToken ct)
    {
        if (source is not null) Form.Source = source;
        if (target is not null) Form.Target = target;
        await LoadAsync(ct);
    }

    /// <summary>«Проверить»: что будет сделано и что мешает. Ничего не меняет.</summary>
    public async Task<IActionResult> OnPostPlanAsync(CancellationToken ct)
    {
        await LoadAsync(ct);
        if (ServiceError is not null)
            return Page();
        try
        {
            Plan = await _client.PlanAsync(Form.ToDto(), ct);
        }
        catch (MigrationServiceException ex)
        {
            ServiceError = ex.Message;
        }
        return Page();
    }

    public async Task<IActionResult> OnPostStartAsync(CancellationToken ct)
    {
        var request = Form.ToDto();
        try
        {
            // Перед запуском - повторная проверка: подтверждение сверяется с текущим именем базы приёмника.
            var plan = await _client.PlanAsync(request, ct);
            if (!plan.CanRun)
            {
                await LoadAsync(ct);
                Plan = plan;
                return Page();
            }
            // Выгрузка источник не меняет - подтверждать именем базы нечего.
            if (request.IsFull && !request.IsFileExport && !string.Equals((Confirmation ?? "").Trim(), plan.TargetDatabase, StringComparison.Ordinal))
            {
                await LoadAsync(ct);
                Plan = plan;
                TempData["FlashError"] = $"Для полной копии введите имя базы приёмника «{plan.TargetDatabase}» точно.";
                return Page();
            }
            var run = await _client.StartAsync(request, CurrentSession.PilotLogin, ct);
            return RedirectToPage("/Migration/Run", new { id = run.Id });
        }
        catch (MigrationServiceException ex)
        {
            TempData["FlashError"] = ex.Message;
            return RedirectToPage();
        }
    }

    public async Task<IActionResult> OnPostToggleScheduleAsync(Guid id, CancellationToken ct)
    {
        try
        {
            var s = await _client.GetScheduleAsync(id, ct);
            if (s is not null)
            {
                s.Enabled = !s.Enabled;
                await _client.SaveScheduleAsync(s, CurrentSession.PilotLogin, ct);
                TempData["Flash"] = $"Расписание «{s.Name}» {(s.Enabled ? "включено" : "выключено")}.";
            }
        }
        catch (MigrationServiceException ex)
        {
            TempData["FlashError"] = ex.Message;
        }
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDeleteScheduleAsync(Guid id, CancellationToken ct)
    {
        try
        {
            await _client.DeleteScheduleAsync(id, ct);
            TempData["Flash"] = "Расписание удалено.";
        }
        catch (MigrationServiceException ex)
        {
            TempData["FlashError"] = ex.Message;
        }
        return RedirectToPage();
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        ViewData["Section"] = "migration";
        if (!_client.IsConfigured)
            return;
        try
        {
            Status = await _client.GetStatusAsync(ct);
            Runs = await _client.ListRunsAsync(20, ct);
            Schedules = await _client.ListSchedulesAsync(ct);
        }
        catch (MigrationServiceException ex)
        {
            ServiceError = ex.Message;
            return;
        }
        try
        {
            Import = await _client.GetImportSourcesAsync(ct);
        }
        catch (MigrationServiceException)
        {
            // Старая версия сервиса не знает про импорт - остальная страница работает.
            Import = null;
        }
        try
        {
            Exports = await _client.GetExportsAsync(ct);
        }
        catch (MigrationServiceException)
        {
            Exports = null;
        }
    }
}
