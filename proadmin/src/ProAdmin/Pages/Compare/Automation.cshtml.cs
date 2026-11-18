using ProAdmin.Automations;
using ProAdmin.Compare;
using ProAdmin.GitLab;
using ProAdmin.Pilot;
using ProAdmin.Settings;
using Microsoft.AspNetCore.Mvc;

namespace ProAdmin.Pages.Compare;

/// <summary>Различия автоматизации между двумя контурами: на серверах Pilot или в ветках контуров.</summary>
public class AutomationModel : AppPageModel
{
    private readonly PilotGateway _pilot;

    public AutomationModel(PilotGateway pilot) => _pilot = pilot;

    [BindProperty(SupportsGet = true)] public string Name { get; set; } = "";
    [BindProperty(SupportsGet = true)] public string? A { get; set; }
    [BindProperty(SupportsGet = true)] public string? B { get; set; }

    /// <summary>servers - что работает на серверах; branches - что лежит в ветках контуров.</summary>
    [BindProperty(SupportsGet = true)] public string Source { get; set; } = "servers";

    public EnvironmentSettings? EnvA { get; private set; }
    public EnvironmentSettings? EnvB { get; private set; }
    public AutomationRepo? Repo { get; private set; }
    public AutomationSnapshot? SideA { get; private set; }
    public AutomationSnapshot? SideB { get; private set; }
    public DiffResult? JsonDiff { get; private set; }
    public DiffResult? CsDiff { get; private set; }
    public List<string> Errors { get; } = new();

    public bool FromBranches => Source == "branches";
    public string LabelA => FromBranches ? EnvA!.Branch : EnvA!.Title;
    public string LabelB => FromBranches ? EnvB!.Branch : EnvB!.Title;

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        ViewData["Section"] = "compare";
        if (!GitLabConnected)
            return RedirectToGitLab();

        Name = Name.Trim();
        Source = Source == "branches" ? "branches" : "servers";
        var (defA, defB) = CompareService.DefaultPair(AppSettings);
        EnvA = AppSettings.Find(A) is { HasServer: true } a ? a : defA;
        EnvB = AppSettings.Find(B) is { HasServer: true } b ? b : defB;
        if (EnvA is null || EnvB is null)
        {
            Errors.Add("Для сравнения нужны два контура с серверами Pilot.");
            return Page();
        }

        try
        {
            if (GroupPath is not null)
                Repo = await AutomationRepositories.FindAsync(CreateGitLabClient(), GroupPath, Name, ct);
        }
        catch (GitLabApiException ex)
        {
            Errors.Add($"GitLab: {ex.Message}");
        }

        if (FromBranches)
        {
            if (Repo is null)
            {
                Errors.Add($"Репозитория «{Name}» нет в GitLab - сравнивать ветки нечего.");
                return Page();
            }
            if (EnvA.Branch.Length == 0 || EnvB.Branch.Length == 0)
            {
                Errors.Add("У обоих контуров должны быть ветки.");
                return Page();
            }
            var git = CreateGitLabClient();
            SideA = await AutomationOperations.ReadBranchAsync(git, Repo, EnvA.Branch, ct);
            SideB = await AutomationOperations.ReadBranchAsync(git, Repo, EnvB.Branch, ct);
        }
        else
        {
            var ta = Load(EnvA, ct);
            var tb = Load(EnvB, ct);
            await Task.WhenAll(ta, tb);
            SideA = ta.Result;
            SideB = tb.Result;
        }

        if (Errors.Count == 0 && SideA is null && SideB is null)
            Errors.Add($"Автоматизации «{Name}» нет ни на {LabelA}, ни на {LabelB}.");

        JsonDiff = LineDiff.Compute(SideA?.Triggers, SideB?.Triggers);
        CsDiff = LineDiff.Compute(SideA?.CSharpCode, SideB?.CSharpCode);
        return Page();
    }

    private async Task<AutomationSnapshot?> Load(EnvironmentSettings env, CancellationToken ct)
    {
        try
        {
            return await _pilot.GetAutomationAsync(env.PilotUrl, Vault.GetPilotLogin(CurrentSession, env.Key), Name, ct);
        }
        catch (PilotGatewayException ex)
        {
            lock (Errors) Errors.Add($"{env.Title}: {ex.Message}");
            return null;
        }
    }

    public string Link(string? source = null, string? a = null, string? b = null) =>
        $"/Compare/Automation?name={Uri.EscapeDataString(Name)}&a={a ?? EnvA?.Key}&b={b ?? EnvB?.Key}&source={source ?? Source}";
}
