using ProAdmin.Migration;
using Microsoft.AspNetCore.Mvc;

namespace ProAdmin.Pages.Migration;

/// <summary>Подключения сервиса миграции к контурам: Pilot, PostgreSQL, settings.xml, файловые архивы.</summary>
public class ConnectionsModel : AppPageModel
{
    private readonly MigrationClient _client;

    public ConnectionsModel(MigrationClient client) => _client = client;

    [BindProperty]
    public MigrationConfigDto Config { get; set; } = new();

    public List<ConnectionCheckDto> CheckResults { get; private set; } = new();
    public string? Error { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        ViewData["Section"] = "migration";
        try
        {
            Config = await _client.GetConfigAsync(ct);
        }
        catch (MigrationServiceException ex)
        {
            TempData["FlashError"] = ex.Message;
            return RedirectToPage("/Migration/Index");
        }
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        ViewData["Section"] = "migration";
        Clean();
        try
        {
            Config = await _client.SaveConfigAsync(Config, CurrentSession.Actor, ct);
            TempData["Flash"] = "Подключения сохранены.";
            return RedirectToPage();
        }
        catch (MigrationServiceException ex)
        {
            Error = ex.Message;
            return Page();
        }
    }

    /// <summary>Сохраняет и проверяет каждый настроенный контур.</summary>
    public async Task<IActionResult> OnPostCheckAsync(CancellationToken ct)
    {
        ViewData["Section"] = "migration";
        Clean();
        try
        {
            Config = await _client.SaveConfigAsync(Config, CurrentSession.Actor, ct);
            CheckResults = await _client.CheckConfigAsync(ct);
        }
        catch (MigrationServiceException ex)
        {
            Error = ex.Message;
        }
        return Page();
    }

    private void Clean()
    {
        foreach (var env in Config.Environments)
        {
            env.PathMappings = env.PathMappings.Where(m => !string.IsNullOrWhiteSpace(m.ServerPrefix) && !string.IsNullOrWhiteSpace(m.LocalPath)).ToList();
            env.S3 = env.S3.Where(s => !string.IsNullOrWhiteSpace(s.ServerUrl)).ToList();
        }
    }
}
