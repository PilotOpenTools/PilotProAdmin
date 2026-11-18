using Microsoft.AspNetCore.Mvc;

namespace ProAdmin.Pages;

/// <summary>Маршрутизация после входа: GitLab / настройки / автоматизации.</summary>
public class IndexModel : AppPageModel
{
    public IActionResult OnGet()
    {
        if (GitHubTypesMode)
            return RedirectToPage("/Types/GitHub");
        if (!GitLabConnected)
            return RedirectToPage("/Login");
        if (!AppSettings.HasAnyServer)
            return RedirectToPage("/Settings");
        return RedirectToPage("/Automations/Index");
    }
}
