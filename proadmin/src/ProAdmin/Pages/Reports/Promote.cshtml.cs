using ProAdmin.Automations;
using ProAdmin.GitLab;
using ProAdmin.Pilot;
using ProAdmin.Reports;
using ProAdmin.Settings;
using Microsoft.AspNetCore.Mvc;

namespace ProAdmin.Pages.Reports;

/// <summary>Перенос отчёта на прод: dev / main (Merge Request, pipeline) / продовый сервер.</summary>
public class PromoteModel : AppPageModel
{
    private readonly PilotReportGateway _pilot;
    private readonly ReportJobRunner _runner;

    public PromoteModel(PilotReportGateway pilot, ReportJobRunner runner)
    {
        _pilot = pilot;
        _runner = runner;
    }

    [BindProperty(SupportsGet = true)] public string Name { get; set; } = "";

    /// <summary>hotfix/… - выпустить hotfix вместо релиза/dev (ветка удаляется при влитии).</summary>
    [BindProperty(SupportsGet = true)] public string? From { get; set; }

    public bool IsHotfix => From is { } f && ProAdmin.Modules.ModuleBranches.IsHotfix(f);
    [BindProperty] public string? Description { get; set; }
    [BindProperty] public string? Task { get; set; }
    [BindProperty] public Guid? FolderId { get; set; }

    public List<string> Errors { get; } = new();
    public AutomationRepo? Repo { get; private set; }
    public EnvironmentSettings? Dev { get; private set; }
    public EnvironmentSettings? Main { get; private set; }
    public string? DevPath { get; private set; }
    public bool HasMain { get; private set; }
    public bool BranchesEqual { get; private set; }
    public PilotReport? TestReport { get; private set; }
    public PilotReport? ProdReport { get; private set; }
    public bool? TestedOnTest { get; private set; }
    public bool? ProdUpToDate { get; private set; }
    public List<PilotReportFolder> ProdFolders { get; } = new();
    public string? OpenMergeRequestUrl { get; private set; }

    /// <summary>Откуда переносится: активный релиз со стейджа (release/&lt;дата&gt;) или dev, если релиза нет.</summary>
    public string Source { get; private set; } = AutomationRepositories.DevBranch;

    /// <summary>Номер, который получит версия при переносе (дата), и последний релиз.</summary>
    public string? NextVersion { get; private set; }
    public string? LastRelease { get; private set; }

    /// <summary>Откуда подставлено «что изменилось» (null - ничего не найдено).</summary>
    public string? NotesSource { get; private set; }

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
        if (Errors.Count > 0 || Repo is null || Main is null)
            return Page();

        var message = AutomationChecks.BuildCommitMessage(Main, Repo.Name, Description, Task, out var error);
        if (error is not null)
        {
            Errors.Add(error);
            return Page();
        }
        if (ProdReport is null && FolderId is null)
        {
            Errors.Add($"Отчёта нет на сервере {Main.Title} - выберите папку, в которой его создать.");
            return Page();
        }

        try
        {
            var job = _runner.StartPromote(
                new ReportPromoteRequest
                {
                    Name = Repo.Name,
                    CommitMessage = message,
                    ChangeNotes = Changelog.SplitNotes(Description),
                    TaskReference = Task,
                    TargetFolderId = ProdReport is null ? FolderId : null,
                    Source = Source,
                },
                new ReportPromoteContext
                {
                    ProdLogin = ReportLogin(Main.Key),
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
        if (Main is null)
        {
            Errors.Add("В настройках нет контура с сервером Pilot и веткой main.");
            return;
        }

        var git = CreateGitLabClient();
        var key = ProjectNames.Key(Name);
        try
        {
            Repo = await AutomationRepositories.FindAsync(git, ReportsGroupPath, Name, ct);
            if (Repo is null)
            {
                var all = await AutomationRepositories.ListAsync(git, ReportsGroupPath, ct);
                Repo = all.FirstOrDefault(r => ProjectNames.Key(r.Name) == key);
            }
            if (Repo is null)
            {
                Errors.Add($"Репозиторий отчёта «{Name}» не найден в группе {ReportsGroupPath}.");
                return;
            }

            var releases = await ReportVersion.ListReleasesAsync(git, Repo.ProjectPath, ct);
            var branches = await git.ListBranchesAsync(Repo.ProjectPath, null, ct) ?? new();
            if (IsHotfix)
            {
                if (branches.All(b => b.Name != From))
                {
                    Errors.Add($"Ветки {From} нет.");
                    return;
                }
                Source = From!;
            }


            var devTree = await git.ListTreeAsync(Repo.ProjectPath, Source, ct);
            var mainTree = await git.ListTreeAsync(Repo.ProjectPath, AutomationRepositories.MainBranch, ct);
            DevPath = ReportFiles.FindPath(devTree, Repo.Name);
            HasMain = mainTree is not null;
            if (devTree is null) Errors.Add($"В репозитории нет ветки {Source}.");
            else if (DevPath is null) Errors.Add($"В ветке {Source} нет файла .repx отчёта - сначала отправьте отчёт в dev из Pilot.");
            if (mainTree is null) Errors.Add("В репозитории нет ветки main.");

            byte[]? devBytes = DevPath is null ? null : await git.GetRawFileAsync(Repo.ProjectPath, DevPath, Source, ct);
            var mainPath = ReportFiles.FindPath(mainTree, Repo.Name);
            byte[]? mainBytes = mainPath is null ? null : await git.GetRawFileAsync(Repo.ProjectPath, mainPath, AutomationRepositories.MainBranch, ct);
            BranchesEqual = ReportFiles.SameContent(devBytes, mainBytes);

            // Первое открытие формы: «что изменилось» - из журнала dev (или из коммитов dev после последнего изменения main).
            if (Description is null && !BranchesEqual)
            {
                var (notes, source) = await ChangelogSource.PendingAsync(git, Repo.ProjectPath, Repo.Name, null, ct);
                Description = string.Join("\n", notes);
                NotesSource = source;
            }

            NextVersion = ProAdmin.Content.ContentReleases.Version(Source) ?? ReportVersion.Next(DateTimeOffset.Now, releases.Select(r => r.Tag.Name));
            LastRelease = releases.FirstOrDefault().Tag?.Name;

            OpenMergeRequestUrl = (await git.ListOpenMergeRequestsAsync(Repo.ProjectPath, Source, AutomationRepositories.MainBranch, ct))
                .FirstOrDefault()?.WebUrl;

            (TestReport, TestedOnTest) = await ServerStateAsync(Dev, key, devBytes, null, ct);
            (ProdReport, ProdUpToDate) = await ServerStateAsync(Main, key, devBytes, ProdFolders, ct);
            if (ProdReport is null && ProdFolders.Count > 0 && FolderId is null)
            {
                var testFolder = TestReport?.Folder;
                FolderId = ProdFolders.FirstOrDefault(f => f.Path == testFolder)?.Id ?? ProdFolders[0].Id;
            }
        }
        catch (GitLabApiException ex)
        {
            Errors.Add($"GitLab: {ex.Message}");
        }
    }

    /// <summary>Отчёт на сервере контура и совпадает ли он с версией из dev.</summary>
    private async Task<(PilotReport?, bool?)> ServerStateAsync(EnvironmentSettings? env, string key, byte[]? devBytes,
        List<PilotReportFolder>? folders, CancellationToken ct)
    {
        if (env is null)
            return (null, null);
        try
        {
            var list = await _pilot.ListAsync(env.PilotUrl, ReportLogin(env.Key), AppSettings.ReportsLicenseType, withContent: false, ct);
            folders?.AddRange(list.Folders);
            var report = list.Reports.FirstOrDefault(r => ProjectNames.Key(r.Name) == key);
            if (report is null || devBytes is null)
                return (report, null);
            var bytes = await _pilot.DownloadAsync(env.PilotUrl, ReportLogin(env.Key), AppSettings.ReportsLicenseType, report.Id, ct);
            return (report, ReportFiles.SameContent(bytes, devBytes));
        }
        catch (PilotGatewayException ex)
        {
            Errors.Add($"{env.Title}: {ex.Message}" + (ex.Kind == PilotErrorKind.Authentication ? " Введите учётку на вкладке «Отчёты»." : ""));
            return (null, null);
        }
    }
}
