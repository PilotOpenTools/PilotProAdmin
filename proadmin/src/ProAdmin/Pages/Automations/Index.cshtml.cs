using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using ProAdmin.Automations;
using ProAdmin.Pilot;
using ProAdmin.Settings;
using Microsoft.AspNetCore.Mvc;

namespace ProAdmin.Pages.Automations;

public class IndexModel : AppPageModel
{
    private readonly AutomationCatalog _catalog;
    private readonly PilotGateway _pilot;

    public IndexModel(AutomationCatalog catalog, PilotGateway pilot)
    {
        _catalog = catalog;
        _pilot = pilot;
    }

    /// <summary>Старые ссылки вида /Automations/test - контур больше не выбирается, в таблице все сразу.</summary>
    [BindProperty(SupportsGet = true)] public string? Env { get; set; }

    [BindProperty] public string? CredEnv { get; set; }
    [BindProperty] public string? EnvLogin { get; set; }
    [BindProperty] public string? EnvPassword { get; set; }

    public CatalogResult? Catalog { get; private set; }
    public string? CredentialError { get; private set; }

    /// <summary>Кириллица остаётся как есть, чтобы страница не раздувалась, а &lt; и &gt; экранируются: текст безопасен внутри тега script.</summary>
    private static readonly JsonSerializerOptions SearchJson = new() { Encoder = JavaScriptEncoder.Create(UnicodeRanges.BasicLatin, UnicodeRanges.Cyrillic) };

    /// <summary>Тексты для поиска в браузере: имя в нижнем регистре / JSON и C# по источникам, одинаковые тексты объединены.</summary>
    public string SearchIndexJson()
    {
        static string Normalize(string text) => text.Replace("\r\n", "\n");

        var index = new Dictionary<string, object[]>(StringComparer.Ordinal);
        foreach (var row in Catalog?.Rows ?? new())
        {
            var texts = row.Texts().ToList();
            var parts = texts.Select(t => (Kind: "JSON", t.Source, Text: t.Triggers))
                .Concat(texts.Where(t => !string.IsNullOrWhiteSpace(t.CSharp)).Select(t => (Kind: "C#", t.Source, Text: t.CSharp!)))
                .GroupBy(p => (p.Kind, Text: Normalize(p.Text)))
                .Select(g => (object)new { kind = g.Key.Kind, where = string.Join(", ", g.Select(p => p.Source)), text = g.Key.Text })
                .ToArray();
            index.TryAdd(row.Name.ToLowerInvariant(), parts);
        }
        return JsonSerializer.Serialize(index, SearchJson);
    }

    /// <summary>Тест (ветка dev) и прод (ветка main) - к ним ведут кнопки «Изменить».</summary>
    public EnvironmentSettings? DevEnv => AppSettings.Environments.FirstOrDefault(e => e.HasServer && e.Branch == AutomationRepositories.DevBranch);
    public EnvironmentSettings? MainEnv => AppSettings.Environments.FirstOrDefault(e => e.HasServer && e.Branch == AutomationRepositories.MainBranch);

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        if (Prepare() is { } redirect)
            return redirect;

        await LoadAutomationsAccessAsync(ct);
        if (AppSettings.HasAnyServer)
            await LoadAsync(ct);
        return Page();
    }

    /// <summary>Отдельный логин/пароль администратора для сервера контура.</summary>
    public async Task<IActionResult> OnPostCredentialsAsync(CancellationToken ct)
    {
        if (Prepare() is { } redirect)
            return redirect;
        if (UsesServiceAccounts)
            return NotFound(); // при входе через GitLab учётки Pilot - только служебные из «Настроек»
        if (BlockedCredentialAttempt() is { } blocked)
            return blocked;
        var env = AppSettings.Find(CredEnv);
        if (env is not { HasServer: true })
            return RedirectToPage(new { env = (string?)null });

        var login = new PilotLogin(EnvLogin?.Trim() ?? "", EnvPassword ?? "");
        try
        {
            await _pilot.VerifyLoginAsync(env.PilotUrl, login, ct);
            Vault.SetEnvironmentCredential(CurrentSession, env.Key, login);
            return RedirectToPage(new { env = (string?)null });
        }
        catch (PilotGatewayException ex)
        {
            CredentialAttemptFailed(env.Key);
            CredentialError = $"{env.Title}: {ex.Message}";
            await LoadAutomationsAccessAsync(ct);
            await LoadAsync(ct);
            return Page();
        }
    }

    private IActionResult? Prepare()
    {
        ViewData["Section"] = "automations";
        return GitLabConnected ? null : RedirectToGitLab();
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        Catalog = await _catalog.LoadAsync(AppSettings, env => Vault.GetPilotLogin(CurrentSession, env), CreateGitLabClient(), ct);
    }
}
