using System.Text;
using ProAdmin.Automations;
using ProAdmin.Compare;
using ProAdmin.GitLab;
using ProAdmin.Pilot;
using ProAdmin.Reports;
using ProAdmin.Settings;
using Microsoft.AspNetCore.Mvc;

namespace ProAdmin.Pages.Compare;

/// <summary>Различия отчёта (.repx) между двумя контурами: на серверах Pilot или в ветках репозитория.</summary>
public class ReportModel : AppPageModel
{
    private readonly PilotReportGateway _pilot;

    public ReportModel(PilotReportGateway pilot) => _pilot = pilot;

    [BindProperty(SupportsGet = true)] public string Name { get; set; } = "";
    [BindProperty(SupportsGet = true)] public string? A { get; set; }
    [BindProperty(SupportsGet = true)] public string? B { get; set; }
    [BindProperty(SupportsGet = true)] public string Source { get; set; } = "servers";

    public EnvironmentSettings? EnvA { get; private set; }
    public EnvironmentSettings? EnvB { get; private set; }
    public AutomationRepo? Repo { get; private set; }
    public ReportSide? SideA { get; private set; }
    public ReportSide? SideB { get; private set; }
    public DiffResult? Diff { get; private set; }
    public List<string> Errors { get; } = new();

    public bool FromBranches => Source == "branches";
    public string LabelA => FromBranches ? EnvA!.Branch : EnvA!.Title;
    public string LabelB => FromBranches ? EnvB!.Branch : EnvB!.Title;

    /// <summary>Отчёт на одной стороне сравнения.</summary>
    public sealed record ReportSide(string FileName, byte[] Content, string? Folder)
    {
        public string Text => Encoding.UTF8.GetString(Content);
        public string Size => Content.Length >= 1024 * 1024 ? $"{Content.Length / 1024.0 / 1024:0.#} МБ" : $"{Math.Max(1, Content.Length / 1024)} КБ";
    }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        if (await PrepareAsync(ct) is { } redirect)
            return redirect;

        var ta = LoadSideAsync(EnvA!, ct);
        var tb = LoadSideAsync(EnvB!, ct);
        await Task.WhenAll(ta, tb);
        SideA = ta.Result;
        SideB = tb.Result;

        if (Errors.Count == 0 && SideA is null && SideB is null)
            Errors.Add($"Отчёта «{Name}» нет ни на {LabelA}, ни на {LabelB}.");
        if (SideA is not null || SideB is not null)
            Diff = LineDiff.Compute(SideA?.Text, SideB?.Text);
        return Page();
    }

    /// <summary>Скачать .repx одной из сторон - чтобы открыть в дизайнере отчётов.</summary>
    public async Task<IActionResult> OnGetDownloadAsync(string side, CancellationToken ct)
    {
        if (await PrepareAsync(ct) is { } redirect)
            return redirect;
        var env = side == "b" ? EnvB! : EnvA!;
        var data = await LoadSideAsync(env, ct);
        if (data is null)
            return NotFound();
        var suffix = FromBranches ? env.Branch : env.Key;
        var name = Path.GetFileNameWithoutExtension(data.FileName) + $" ({suffix}){ReportFiles.Extension}";
        return File(data.Content, "application/xml", name);
    }

    private async Task<IActionResult?> PrepareAsync(CancellationToken ct)
    {
        ViewData["Section"] = "compare";
        if (!GitLabConnected)
            return RedirectToGitLab();

        Name = Name.Trim();
        Source = Source == "branches" ? "branches" : "servers";
        var envs = ReportCatalog.ReportEnvs(AppSettings).ToList();
        var (defA, defB) = (ReportCatalog.DevEnv(AppSettings) ?? envs.FirstOrDefault(), ReportCatalog.MainEnv(AppSettings) ?? envs.LastOrDefault());
        EnvA = envs.FirstOrDefault(e => e.Key == A) ?? defA;
        EnvB = envs.FirstOrDefault(e => e.Key == B) ?? defB;
        if (EnvA is null || EnvB is null)
        {
            Errors.Add("Для сравнения отчётов нужны два контура с сервером Pilot и веткой.");
            return Page();
        }

        if (ReportsGroupPath is not null)
        {
            try
            {
                var key = ProjectNames.Key(Name);
                Repo = (await AutomationRepositories.ListAsync(CreateGitLabClient(), ReportsGroupPath, ct))
                    .FirstOrDefault(r => ProjectNames.Key(r.Name) == key);
            }
            catch (GitLabApiException ex)
            {
                Errors.Add($"GitLab: {ex.Message}");
            }
        }
        if (FromBranches && Repo is null)
        {
            Errors.Add($"Репозитория отчёта «{Name}» нет в GitLab - сравнивать ветки нечего.");
            return Page();
        }
        return null;
    }

    private async Task<ReportSide?> LoadSideAsync(EnvironmentSettings env, CancellationToken ct)
    {
        try
        {
            if (FromBranches)
            {
                var git = CreateGitLabClient();
                var tree = await git.ListTreeAsync(Repo!.ProjectPath, env.Branch, ct);
                var path = ReportFiles.FindPath(tree, Repo.Name);
                if (path is null)
                    return null;
                var blob = tree!.First(i => i.Type == "blob" && i.Path == path).Id;
                return new ReportSide(Path.GetFileName(path), await git.GetBlobRawAsync(Repo.ProjectPath, blob, ct), null);
            }

            var login = ReportLogin(env.Key);
            var list = await _pilot.ListAsync(env.PilotUrl, login, AppSettings.ReportsLicenseType, withContent: false, ct);
            var key = ProjectNames.Key(Name);
            var report = list.Reports.FirstOrDefault(r => ProjectNames.Key(r.Name) == key);
            if (report is null)
                return null;
            var content = await _pilot.DownloadAsync(env.PilotUrl, login, AppSettings.ReportsLicenseType, report.Id, ct);
            return content is null ? null : new ReportSide(report.FileName ?? ReportFiles.DefaultPath(Name), content, report.Folder);
        }
        catch (Exception ex) when (ex is PilotGatewayException or GitLabApiException)
        {
            lock (Errors) Errors.Add($"{(FromBranches ? env.Branch : env.Title)}: {ex.Message}");
            return null;
        }
    }

    public string Link(string? source = null, string? a = null, string? b = null) =>
        $"/Compare/Report?name={Uri.EscapeDataString(Name)}&a={a ?? EnvA?.Key}&b={b ?? EnvB?.Key}&source={source ?? Source}";
}
