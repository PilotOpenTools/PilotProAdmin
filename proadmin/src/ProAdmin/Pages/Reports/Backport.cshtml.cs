using ProAdmin.Automations;
using ProAdmin.Compare;
using ProAdmin.GitLab;
using ProAdmin.Pilot;
using ProAdmin.Reports;
using ProAdmin.Settings;
using Microsoft.AspNetCore.Mvc;

namespace ProAdmin.Pages.Reports;

/// <summary>
/// Хотфикс с прода в тест: отчёт, изменённый прямо в main (на проде), возвращается в dev и на тестовый сервер -
/// MR main / dev / pipeline / тестовый сервер / merge. Обратное направление к «На прод /».
/// </summary>
public class BackportModel : AppPageModel
{
    private readonly PilotReportGateway _pilot;
    private readonly ReportJobRunner _runner;

    public BackportModel(PilotReportGateway pilot, ReportJobRunner runner)
    {
        _pilot = pilot;
        _runner = runner;
    }

    [BindProperty(SupportsGet = true)] public string Name { get; set; } = "";
    [BindProperty] public string? Description { get; set; }
    [BindProperty] public string? Task { get; set; }

    public List<string> Errors { get; } = new();
    public AutomationRepo? Repo { get; private set; }
    public EnvironmentSettings? Dev { get; private set; }
    public EnvironmentSettings? Main { get; private set; }
    public string? MainPath { get; private set; }
    public ReportDrift Drift { get; private set; }
    public bool BranchesEqual { get; private set; }
    public PilotReport? TestReport { get; private set; }
    public bool? TestUpToDate { get; private set; }
    public string? OpenMergeRequestUrl { get; private set; }

    /// <summary>Коммиты main, которых нет в dev (новые первыми) - что именно правили на проде.</summary>
    public List<GitLabCommit> HotfixCommits { get; } = new();
    public DiffResult? ScriptDiff { get; private set; }
    public DiffResult? MarkupDiff { get; private set; }

    public bool CanBackport => Errors.Count == 0 && Repo is not null && MainPath is not null && Dev is not null
                               && !(BranchesEqual && TestUpToDate == true);

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        if (!GitLabConnected)
            return RedirectToGitLab();
        if (await DenyReportsChangeAsync(ct) is { } denied)
            return denied;
        ViewData["Section"] = "reports";
        await LoadAsync(ct);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        if (!GitLabConnected)
            return RedirectToGitLab();
        if (await DenyReportsChangeAsync(ct) is { } denied)
            return denied;
        ViewData["Section"] = "reports";
        await LoadAsync(ct);
        if (!CanBackport)
        {
            if (Errors.Count == 0)
                Errors.Add("Переносить с прода нечего.");
            return Page();
        }

        var message = AutomationChecks.BuildCommitMessage(Dev!, Repo!.Name, $"хотфикс с прода: {Description?.Trim()}", Task, out var error);
        if (string.IsNullOrWhiteSpace(Description))
            error = "Опишите хотфикс - что исправили на проде.";
        if (error is not null)
        {
            Errors.Add(error);
            return Page();
        }

        try
        {
            var job = _runner.StartBackport(
                new ReportBackportRequest { Name = Repo.Name, CommitMessage = message },
                new ReportBackportContext
                {
                    TestLogin = ReportLogin(Dev!.Key),
                    GitLabToken = CurrentSession.GitLabToken!,
                    Settings = AppSettings,
                    ProjectPath = Repo.ProjectPath,
                    RepoName = Repo.Name,
                    CiConfigPath = Repo.CiConfigPath,
                },
                CurrentSession.PilotLogin, CurrentSession.GitLabUser?.Username);
            return RedirectToPage("/Jobs/Details", new { id = job.Id });
        }
        catch (InvalidOperationException ex)
        {
            Errors.Add(ex.Message);
            return Page();
        }
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        Dev = ReportCatalog.DevEnv(AppSettings);
        Main = ReportCatalog.MainEnv(AppSettings);
        if (ReportsGroupPath is null)
        {
            Errors.Add("В настройках не указана группа GitLab с отчётами.");
            return;
        }
        if (Dev is null)
        {
            Errors.Add("В настройках нет контура с сервером Pilot и веткой dev.");
            return;
        }

        var git = CreateGitLabClient();
        var key = ProjectNames.Key(Name);
        byte[]? mainBytes;
        try
        {
            Repo = await AutomationRepositories.FindAsync(git, ReportsGroupPath, Name, ct);
            if (Repo is null)
                Repo = (await AutomationRepositories.ListAsync(git, ReportsGroupPath, ct)).FirstOrDefault(r => ProjectNames.Key(r.Name) == key);
            if (Repo is null)
            {
                Errors.Add($"Репозиторий отчёта «{Name}» не найден в группе {ReportsGroupPath}.");
                return;
            }

            var devTree = await git.ListTreeAsync(Repo.ProjectPath, AutomationRepositories.DevBranch, ct);
            var mainTree = await git.ListTreeAsync(Repo.ProjectPath, AutomationRepositories.MainBranch, ct);
            if (devTree is null) Errors.Add("В репозитории нет ветки dev.");
            if (mainTree is null) Errors.Add("В репозитории нет ветки main.");
            MainPath = ReportFiles.FindPath(mainTree, Repo.Name);
            if (mainTree is not null && MainPath is null) Errors.Add("В ветке main нет файла .repx отчёта.");
            if (Errors.Count > 0)
                return;

            var devPath = ReportFiles.FindPath(devTree, Repo.Name);
            mainBytes = await git.GetRawFileAsync(Repo.ProjectPath, MainPath!, AutomationRepositories.MainBranch, ct);
            var devBytes = devPath is null ? null : await git.GetRawFileAsync(Repo.ProjectPath, devPath, AutomationRepositories.DevBranch, ct);
            BranchesEqual = ReportFiles.SameContent(devBytes, mainBytes);
            Drift = BranchesEqual ? ReportDrift.None : await ReportCatalog.DriftAsync(git, Repo.ProjectPath, devPath, MainPath, ct);

            HotfixCommits.AddRange((await git.CompareCommitsAsync(Repo.ProjectPath, AutomationRepositories.DevBranch, AutomationRepositories.MainBranch, ct))
                .Where(c => c.ParentIds is not { Count: > 1 })
                .Reverse());
            if (Description is null && !BranchesEqual)
                Description = string.Join("\n", HotfixCommits.Select(c => ChangelogSource.StripName(c.Title ?? "", Repo.Name)).Where(t => t.Length > 0).Distinct());

            OpenMergeRequestUrl = (await git.ListOpenMergeRequestsAsync(Repo.ProjectPath, AutomationRepositories.MainBranch, AutomationRepositories.DevBranch, ct))
                .FirstOrDefault()?.WebUrl;
        }
        catch (GitLabApiException ex)
        {
            Errors.Add($"GitLab: {ex.Message}");
            return;
        }

        try
        {
            var list = await _pilot.ListAsync(Dev.PilotUrl, ReportLogin(Dev.Key), AppSettings.ReportsLicenseType, withContent: false, ct);
            TestReport = list.Reports.FirstOrDefault(r => ProjectNames.Key(r.Name) == key);
            var testBytes = TestReport is null ? null
                : await _pilot.DownloadAsync(Dev.PilotUrl, ReportLogin(Dev.Key), AppSettings.ReportsLicenseType, TestReport.Id, ct);
            TestUpToDate = TestReport is null ? null : ReportFiles.SameContent(testBytes, mainBytes);

            var test = ReportText.Parse(testBytes);
            var prod = ReportText.Parse(mainBytes);
            ScriptDiff = LineDiff.Compute(test.Script, prod.Script);
            MarkupDiff = LineDiff.Compute(test.Markup, prod.Markup);
        }
        catch (PilotGatewayException ex)
        {
            Errors.Add($"{Dev.Title}: {ex.Message}" + (ex.Kind == PilotErrorKind.Authentication ? " Введите учётку на вкладке «Отчёты»." : ""));
        }
    }
}
