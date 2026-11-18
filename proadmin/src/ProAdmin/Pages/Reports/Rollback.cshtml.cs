using ProAdmin.Automations;
using ProAdmin.Compare;
using ProAdmin.GitLab;
using ProAdmin.Pilot;
using ProAdmin.Reports;
using ProAdmin.Settings;
using Microsoft.AspNetCore.Mvc;

namespace ProAdmin.Pages.Reports;

/// <summary>
/// Откат отчёта на прод: содержимое выбранного релиза (тега v2026.09.20) уходит в main и на продовый сервер
/// новой версией с сегодняшним номером - обычным путём MR / pipeline / сервер / merge / релиз.
/// </summary>
public class RollbackModel : AppPageModel
{
    private readonly PilotReportGateway _pilot;
    private readonly ReportJobRunner _runner;

    public RollbackModel(PilotReportGateway pilot, ReportJobRunner runner)
    {
        _pilot = pilot;
        _runner = runner;
    }

    [BindProperty(SupportsGet = true)] public string Name { get; set; } = "";
    [BindProperty(SupportsGet = true)] public string? Tag { get; set; }
    [BindProperty] public string? Description { get; set; }
    [BindProperty] public string? Task { get; set; }

    public List<string> Errors { get; } = new();
    public AutomationRepo? Repo { get; private set; }
    public EnvironmentSettings? Main { get; private set; }
    public List<(GitLabTag Tag, string Version)> Releases { get; private set; } = new();

    /// <summary>Релиз, которым помечен текущий коммит main (что сейчас на проде по данным GitLab).</summary>
    public string? CurrentTag { get; private set; }
    public string? NewVersion { get; private set; }
    public PilotReport? ProdReport { get; private set; }

    /// <summary>Сервер уже содержит выбранный релиз.</summary>
    public bool ProdEqualsTag { get; private set; }
    public DiffResult? ScriptDiff { get; private set; }
    public DiffResult? MarkupDiff { get; private set; }

    public bool CanRollback => Errors.Count == 0 && Tag is not null && NewVersion is not null && ProdReport is not null
                               && !(ProdEqualsTag && Tag == CurrentTag);

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
        if (!CanRollback)
        {
            if (Errors.Count == 0)
                Errors.Add("Откат сейчас невозможен.");
            return Page();
        }
        if (string.IsNullOrWhiteSpace(Description))
        {
            Errors.Add("Укажите причину отката - она попадёт в CHANGELOG.md, Release и историю версий отчёта в Pilot.");
            return Page();
        }

        var message = AutomationChecks.BuildCommitMessage(Main!, Repo!.Name, $"откат на {Tag}: {Description.Trim()}", Task, out var error);
        if (error is not null)
        {
            Errors.Add(error);
            return Page();
        }

        try
        {
            var job = _runner.StartRollback(
                new ReportRollbackRequest
                {
                    Name = Repo.Name,
                    Tag = Tag!,
                    CommitMessage = message,
                    Reasons = Changelog.SplitNotes(Description),
                    TaskReference = Task,
                },
                new ReportPromoteContext
                {
                    ProdLogin = ReportLogin(Main!.Key),
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
        byte[]? tagBytes = null, prodBytes = null;
        try
        {
            Repo = await AutomationRepositories.FindAsync(git, ReportsGroupPath, Name, ct);
            if (Repo is null)
            {
                var key = ProjectNames.Key(Name);
                Repo = (await AutomationRepositories.ListAsync(git, ReportsGroupPath, ct)).FirstOrDefault(r => ProjectNames.Key(r.Name) == key);
            }
            if (Repo is null)
            {
                Errors.Add($"Репозиторий отчёта «{Name}» не найден в группе {ReportsGroupPath}.");
                return;
            }

            Releases = await ReportVersion.ListReleasesAsync(git, Repo.ProjectPath, ct);
            if (Releases.Count == 0)
            {
                Errors.Add("У отчёта ещё нет релизов (тегов v2026.09.27) - откатываться не к чему. Релиз создаётся при каждом переносе на прод.");
                return;
            }

            var head = (await git.FindBranchAsync(Repo.ProjectPath, AutomationRepositories.MainBranch, ct))?.Commit?.Id;
            CurrentTag = Releases.FirstOrDefault(r => r.Tag.Commit?.Id == head).Tag?.Name;
            NewVersion = ReportVersion.Next(DateTimeOffset.Now, Releases.Select(r => r.Tag.Name));

            // По умолчанию - предыдущий релиз перед текущим.
            Tag ??= Releases.Select(r => r.Tag.Name).SkipWhile(t => CurrentTag is not null && t != CurrentTag).Skip(CurrentTag is null ? 0 : 1).FirstOrDefault()
                    ?? Releases[0].Tag.Name;
            if (Releases.All(r => r.Tag.Name != Tag))
            {
                Errors.Add($"Релиз {Tag} не найден.");
                return;
            }

            var tree = await git.ListTreeAsync(Repo.ProjectPath, Tag, ct);
            var path = ReportFiles.FindPath(tree, Repo.Name);
            tagBytes = path is null ? null : await git.GetRawFileAsync(Repo.ProjectPath, path, Tag, ct);
            if (tagBytes is null)
            {
                Errors.Add($"В релизе {Tag} нет файла .repx отчёта.");
                return;
            }
        }
        catch (GitLabApiException ex)
        {
            Errors.Add($"GitLab: {ex.Message}");
            return;
        }

        try
        {
            var list = await _pilot.ListAsync(Main.PilotUrl, ReportLogin(Main.Key), AppSettings.ReportsLicenseType, withContent: false, ct);
            var key = ProjectNames.Key(Repo.Name);
            ProdReport = list.Reports.FirstOrDefault(r => ProjectNames.Key(r.Name) == key);
            if (ProdReport is null)
            {
                Errors.Add($"Отчёта нет на сервере {Main.Title} - откатывать нечего.");
                return;
            }
            prodBytes = await _pilot.DownloadAsync(Main.PilotUrl, ReportLogin(Main.Key), AppSettings.ReportsLicenseType, ProdReport.Id, ct);
        }
        catch (PilotGatewayException ex)
        {
            Errors.Add($"{Main.Title}: {ex.Message}" + (ex.Kind == PilotErrorKind.Authentication ? " Введите учётку на вкладке «Отчёты»." : ""));
            return;
        }

        ProdEqualsTag = ReportFiles.SameContent(prodBytes, tagBytes);
        var prod = ReportText.Parse(prodBytes);
        var target = ReportText.Parse(tagBytes);
        ScriptDiff = LineDiff.Compute(prod.Script, target.Script);
        MarkupDiff = LineDiff.Compute(prod.Markup, target.Markup);
    }
}
