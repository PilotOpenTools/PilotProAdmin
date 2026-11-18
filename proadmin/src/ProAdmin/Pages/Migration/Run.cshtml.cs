using ProAdmin.Migration;
using Microsoft.AspNetCore.Mvc;

namespace ProAdmin.Pages.Migration;

public class RunModel : AppPageModel
{
    private readonly MigrationClient _client;

    public RunModel(MigrationClient client) => _client = client;

    public MigrationRunDto Run { get; private set; } = null!;

    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken ct)
    {
        ViewData["Section"] = "migration";
        try
        {
            var run = await _client.GetRunAsync(id, ct);
            if (run is null)
                return NotFound();
            Run = run;
            return Page();
        }
        catch (MigrationServiceException ex)
        {
            TempData["FlashError"] = ex.Message;
            return RedirectToPage("/Migration/Index");
        }
    }

    /// <summary>Опрос со страницы, пока перенос не закончится.</summary>
    public async Task<IActionResult> OnGetStatusAsync(Guid id, CancellationToken ct)
    {
        var run = await _client.GetRunAsync(id, ct);
        if (run is null)
            return NotFound();
        return new JsonResult(new
        {
            status = run.Status,
            statusTitle = MigrationTexts.Status(run.Status),
            statusCss = MigrationTexts.StatusCss(run.Status),
            finished = run.IsFinished,
            phase = run.Phase,
            error = run.Error,
            stats = run.Stats,
            log = run.Log.Select(e => new { time = e.Time.ToLocalTime().ToString("HH:mm:ss"), level = LogCss(e.Level), phase = e.Level == "phase", text = e.Text }),
        });
    }

    public async Task<IActionResult> OnPostCancelAsync(Guid id, CancellationToken ct)
    {
        try
        {
            await _client.CancelAsync(id, ct);
            TempData["Flash"] = "Отмена запрошена: незафиксированные изменения будут откачены, база приёмника запущена.";
        }
        catch (MigrationServiceException ex)
        {
            TempData["FlashError"] = ex.Message;
        }
        return RedirectToPage(new { id });
    }

    public static string LogCss(string level) => level switch
    {
        "error" => "error",
        "warn" => "warn",
        "ok" => "ok",
        _ => "info",
    };
}
