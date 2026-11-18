using ProAdmin.Automations;
using Microsoft.AspNetCore.Mvc;

namespace ProAdmin.Pages.Automations;

public class UsageModel : AppPageModel
{
    /// <summary>Сколько найденных имён раскрывать сразу: больше - список становится нечитаемым.</summary>
    public const int OpenLimit = 3;

    private readonly AutomationCatalog _catalog;

    public UsageModel(AutomationCatalog catalog) => _catalog = catalog;

    [BindProperty(SupportsGet = true)] public UsageKind? Kind { get; set; }
    [BindProperty(SupportsGet = true)] public string? Name { get; set; }

    public CatalogResult? Catalog { get; private set; }
    public AutomationUsageIndex? Index { get; private set; }
    public List<UsageEntry> Found { get; private set; } = new();

    public static string KindTitle(UsageKind kind) => kind switch
    {
        UsageKind.Type => "тип",
        UsageKind.Attribute => "атрибут",
        UsageKind.State => "состояние",
        UsageKind.Activity => "действие",
        _ => "текст C#",
    };

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        ViewData["Section"] = "automations";
        if (!GitLabConnected)
            return RedirectToGitLab();

        if (AppSettings.HasAnyServer)
        {
            Catalog = await _catalog.LoadAsync(AppSettings, env => Vault.GetPilotLogin(CurrentSession, env), CreateGitLabClient(), ct);
            Index = AutomationUsageIndex.Build(Catalog.Rows);
            Found = Index.Find(Kind, Name);
        }
        return Page();
    }
}
