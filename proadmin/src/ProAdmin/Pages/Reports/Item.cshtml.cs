using ProAdmin.Automations;
using ProAdmin.Content;
using ProAdmin.GitLab;
using ProAdmin.Modules;
using ProAdmin.Pilot;
using ProAdmin.Reports;
using ProAdmin.Settings;
using Microsoft.AspNetCore.Mvc;

namespace ProAdmin.Pages.Reports;

/// <summary>
/// Страница отчёта: где он сейчас (правка в Pilot / dev/тест / release/стейдж / main/прод) и что сделать дальше,
/// серверы против своих веток, активные ветки репозитория и ветки, которые можно удалить.
/// </summary>
public class ItemModel : AppPageModel
{
    private readonly PilotReportGateway _pilot;

    public ItemModel(PilotReportGateway pilot) => _pilot = pilot;

    [BindProperty(SupportsGet = true)] public string Name { get; set; } = "";

    /// <summary>Релиз, на который откатили прод (голова main - откат); null - не откат.</summary>
    public string? RollbackTo { get; private set; }
    public string? UndoRollbackTag { get; private set; }

    public List<string> Errors { get; } = new();
    public AutomationRepo? Repo { get; private set; }
    public EnvironmentSettings? Test { get; private set; }
    public EnvironmentSettings? Prod { get; private set; }

    /// <summary>Ветки release/&lt;дата&gt; от прежней схемы со стейджем (с тегом) - можно удалить.</summary>
    public List<GitLabBranch> ReleasedReleases { get; private set; } = new();

    /// <summary>Коммиты dev, которых нет в main, - линии задач на карточке «где сейчас».</summary>
    public List<GitLabCommit> DevCommits { get; private set; } = new();
    public bool HasDev { get; private set; }
    public bool HasMain { get; private set; }
    public bool DevEqualsMain { get; private set; }
    public ReportDrift Drift { get; private set; }
    public string? LastRelease { get; private set; }
    public DateTimeOffset? LastReleaseDate { get; private set; }

    /// <summary>Сервер контура против ветки контура.</summary>
    public sealed record EnvState(EnvironmentSettings Env, PilotReport? Pilot, SyncState State);
    public List<EnvState> Envs { get; } = new();

    public BranchActivityResult Branches { get; private set; } = new();

    /// <summary>hotfix/… с коммитами сверх main (их отправляет Reports вместо прямой отправки в main).</summary>
    public ActiveBranch? Hotfix => Branches.Active.FirstOrDefault(a => a.Kind == "hotfix" && a.Ahead > 0);
    public ModuleNextStep? NextStep { get; private set; }

    public string Q => Uri.EscapeDataString(Name);

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        if (!GitLabConnected)
            return RedirectToGitLab();
        ViewData["Section"] = "reports";
        await LoadReportsAccessAsync(ct);
        await LoadAsync(ct);
        return Page();
    }

    /// <summary>Удалить ветки без своих коммитов, из которых уже влит MR, и выпущенные релизы.</summary>
    public async Task<IActionResult> OnPostCleanupBranchesAsync(CancellationToken ct)
    {
        if (!GitLabConnected)
            return RedirectToGitLab();
        if (await DenyReportsChangeAsync(ct) is { } denied)
            return denied;
        ViewData["Section"] = "reports";
        await LoadAsync(ct);
        if (Repo is null)
            return Page();
        var git = CreateGitLabClient();
        var deleted = new List<string>();
        try
        {
            foreach (var b in Branches.Removable.Concat(ReleasedReleases))
            {
                await git.DeleteBranchAsync(Repo.ProjectPath, b.Name, ct);
                deleted.Add(b.Name);
            }
        }
        catch (GitLabApiException ex)
        {
            Errors.Add($"GitLab: {ex.Message}");
            return Page();
        }
        TempData["Flash"] = deleted.Count == 0 ? "Удалять нечего." : $"Удалены ветки: {string.Join(", ", deleted)}.";
        return RedirectToPage(new { name = Name });
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        Test = ReportCatalog.DevEnv(AppSettings);
        Prod = ReportCatalog.MainEnv(AppSettings);
        if (ReportsGroupPath is null)
        {
            Errors.Add("В настройках не указана группа GitLab с отчётами.");
            return;
        }

        var git = CreateGitLabClient();
        var key = ProjectNames.Key(Name);
        var bytes = new Dictionary<string, byte[]?>(StringComparer.Ordinal);
        try
        {
            Repo = await AutomationRepositories.FindAsync(git, ReportsGroupPath, Name, ct)
                   ?? (await AutomationRepositories.ListAsync(git, ReportsGroupPath, ct)).FirstOrDefault(r => ProjectNames.Key(r.Name) == key);
            if (Repo is not null)
            {
                var paths = new Dictionary<string, string?>(StringComparer.Ordinal);
                foreach (var branch in new[] { AutomationRepositories.DevBranch, AutomationRepositories.MainBranch })
                {
                    var tree = await git.ListTreeAsync(Repo.ProjectPath, branch, ct);
                    if (tree is null)
                        continue;
                    if (branch == AutomationRepositories.DevBranch) HasDev = true; else HasMain = true;
                    paths[branch] = ReportFiles.FindPath(tree, Repo.Name);
                    bytes[branch] = paths[branch] is { } path ? await git.GetRawFileAsync(Repo.ProjectPath, path, branch, ct) : null;
                }
                var devBytes = bytes.GetValueOrDefault(AutomationRepositories.DevBranch);
                DevEqualsMain = devBytes is not null && ReportFiles.SameContent(devBytes, bytes.GetValueOrDefault(AutomationRepositories.MainBranch));
                if (devBytes is not null && !DevEqualsMain)
                {
                    try
                    {
                        Drift = await ReportCatalog.DriftAsync(git, Repo.ProjectPath, paths.GetValueOrDefault(AutomationRepositories.DevBranch),
                            paths.GetValueOrDefault(AutomationRepositories.MainBranch), ct);
                    }
                    catch (GitLabApiException)
                    {
                        Drift = ReportDrift.DevAhead;
                    }
                }

                var releases = await ReportVersion.ListReleasesAsync(git, Repo.ProjectPath, ct);
                var release = releases.FirstOrDefault().Tag;
                LastRelease = release?.Name;
                LastReleaseDate = release?.Commit?.CommittedDate;

                var branches = await git.ListBranchesAsync(Repo.ProjectPath, null, ct) ?? new();
                if (branches.Any(b => b.Name == AutomationRepositories.MainBranch))
                    {
                    var rollback = await ContentNextSteps.RollbackAtMainAsync(git, Repo.ProjectPath, ct);
                    RollbackTo = rollback?.Target;
                    UndoRollbackTag = rollback?.UndoTag;
                }
                ReleasedReleases = ContentReleases.ReleasedReportReleases(branches, releases.Select(r => r.Version));

                if (HasDev && HasMain && !DevEqualsMain)
                    DevCommits = ContentReleases.NotOnProd((await git.CompareCommitsAsync(Repo.ProjectPath, AutomationRepositories.MainBranch, AutomationRepositories.DevBranch, ct))
                        .Where(c => c.ParentIds is not { Count: > 1 }).Reverse(), branches);
                var openMrs = await git.ListOpenMergeRequestsToAsync(Repo.ProjectPath, null, ct) ?? new();
                Branches = await BranchActivity.LoadAsync(git, Repo.ProjectPath, branches, openMrs, b => Classify(b.Name), ct);
                if (HasDev && HasMain && !DevEqualsMain && branches.FirstOrDefault(b => b.Name == AutomationRepositories.DevBranch) is { } dev)
                {
                    var ahead = (await git.CompareCommitsAsync(Repo.ProjectPath, AutomationRepositories.MainBranch, dev.Name, ct))
                        .Count(c => c.ParentIds is not { Count: > 1 });
                    Branches.Active.Add(new ActiveBranch(dev, "тест", AutomationRepositories.MainBranch, ahead,
                        openMrs.FirstOrDefault(m => m.SourceBranch == dev.Name)));
                    BranchActivity.Sort(Branches.Active);
                }
            }
        }
        catch (GitLabApiException ex)
        {
            Errors.Add($"GitLab: {ex.Message}");
            return;
        }

        foreach (var env in ReportCatalog.ReportEnvs(AppSettings))
            Envs.Add(await EnvStateAsync(env, key, bytes, ct));

        NextStep = ContentNextSteps.For(new ContentFlowInput
        {
            Noun = "отчёт",
            HasRepo = Repo is not null,
            HasDev = HasDev,
            HasMain = HasMain,
            DevAhead = bytes.GetValueOrDefault(AutomationRepositories.DevBranch) is not null && !DevEqualsMain && Drift == ReportDrift.DevAhead,
            MainAhead = Drift is ReportDrift.MainAhead or ReportDrift.Diverged,
            RollbackTo = RollbackTo,
            UndoRollbackUrl = RollbackTo is not null && UndoRollbackTag is not null ? $"/Reports/Rollback?name={Q}&tag={Uri.EscapeDataString(UndoRollbackTag)}" : null,
            Test = Envs.FirstOrDefault(e => e.Env == Test)?.State,
            Prod = Envs.FirstOrDefault(e => e.Env == Prod)?.State,
            Hotfix = Hotfix?.Branch.Name,
            HotfixUrl = Hotfix is { } h ? $"/Reports/Promote?name={Q}&from={Uri.EscapeDataString(h.Branch.Name)}" : null,
            TestDiffersHint = $"На сервере {Test?.Title ?? "правки"} не то, что в dev: отчёт сохранили в Pilot, но не отправили в GitLab. Сохраните его в Pilot ещё раз и в окне отправки выберите dev.",
            LastRelease = LastRelease,
            CreateUrl = $"/Reports/Create?name={Q}",
            PromoteUrl = $"/Reports/Promote?name={Q}",
            BackportUrl = $"/Reports/Backport?name={Q}",
            CompareUrl = $"/Compare/Report?name={Q}",
        });
    }

    private async Task<EnvState> EnvStateAsync(EnvironmentSettings env, string key, Dictionary<string, byte[]?> branchBytes, CancellationToken ct)
    {
        try
        {
            var list = await _pilot.ListAsync(env.PilotUrl, ReportLogin(env.Key), AppSettings.ReportsLicenseType, withContent: false, ct);
            var report = list.Reports.FirstOrDefault(r => ProjectNames.Key(r.Name) == key);
            var branch = branchBytes.GetValueOrDefault(env.Branch);
            if (Repo is null)
                return new(env, report, report is null ? SyncState.Unknown : SyncState.OnlyPilot);
            if (branch is null)
                return new(env, report, report is null ? SyncState.Unknown : SyncState.OnlyPilot);
            if (report is null)
                return new(env, null, SyncState.OnlyGit);
            var server = await _pilot.DownloadAsync(env.PilotUrl, ReportLogin(env.Key), AppSettings.ReportsLicenseType, report.Id, ct);
            return new(env, report, ReportFiles.SameContent(server, branch) ? SyncState.Synced : SyncState.Differs);
        }
        catch (PilotGatewayException ex)
        {
            Errors.Add($"{env.Title}: {ex.Message}" + (ex.Kind == PilotErrorKind.Authentication ? " Введите учётку на вкладке «Отчёты»." : ""));
            return new(env, null, SyncState.Unknown);
        }
    }

    /// <summary>Вид ветки и её база: hotfix - в main, остальное - в dev.</summary>
    private static (string Kind, string Base)? Classify(string branch)
    {
        if (branch is AutomationRepositories.DevBranch or AutomationRepositories.MainBranch || ContentReleases.Version(branch) is not null)
            return null;
        if (ModuleBranches.IsHotfix(branch))
            return ("hotfix", AutomationRepositories.MainBranch);
        return (ModuleBranches.IsTaskBranch(branch) ? "задача" : "вне схемы", AutomationRepositories.DevBranch);
    }
}
