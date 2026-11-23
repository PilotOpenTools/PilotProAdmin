using ProAdmin.Automations;
using ProAdmin.Content;
using ProAdmin.GitLab;
using ProAdmin.Jobs;
using ProAdmin.Modules;
using ProAdmin.Pilot;
using ProAdmin.Settings;
using Microsoft.AspNetCore.Mvc;

namespace ProAdmin.Pages.Automations;

/// <summary>
/// Страница автоматизации: где она сейчас (правка / dev/тест / release/стейдж / main/прод) и что сделать дальше,
/// серверы против своих веток, активные ветки репозитория и ветки, которые можно удалить.
/// </summary>
public class ItemModel : AppPageModel
{
    private readonly AutomationOperations _operations;
    private readonly PilotGateway _pilot;
    private readonly ContentReleaseRunner _releases;
    private readonly PublishJobRunner _publish;

    public ItemModel(AutomationOperations operations, PilotGateway pilot, ContentReleaseRunner releases, PublishJobRunner publish)
    {
        _operations = operations;
        _pilot = pilot;
        _releases = releases;
        _publish = publish;
    }

    [BindProperty] public string? Branch { get; set; }

    /// <summary>Ветки задач (в dev) и hotfix (в main) с числом коммитов базы, которых в них нет.</summary>
    public sealed record WorkBranch(ActiveBranch Branch, int Behind);
    public List<WorkBranch> Work { get; } = new();

    [BindProperty(SupportsGet = true)] public string Name { get; set; } = "";

    /// <summary>Релиз, на который откатили прод (голова main - откат); null - не откат.</summary>
    public string? RollbackTo { get; private set; }
    public string? UndoRollbackTag { get; private set; }

    public List<string> Errors { get; } = new();
    public AutomationRepo? Repo { get; private set; }
    public EnvironmentSettings? Test { get; private set; }
    public EnvironmentSettings? Prod { get; private set; }
    public EnvironmentSettings? Stage { get; private set; }

    /// <summary>Режим «тест и стейдж вместе»: стейдж = dev, релизов нет, на прод переносится dev.</summary>
    public bool AutoStage => Stage is not null && AppSettings.AutomationsAutoStage;

    /// <summary>release/X.Y.Z с версией выше main - то, что на стейдже и ждёт выпуска.</summary>
    public GitLabBranch? ActiveRelease { get; private set; }

    /// <summary>Коммиты dev и активного релиза, которых нет в main, - линии задач на карточке «где сейчас».</summary>
    public List<GitLabCommit> DevCommits { get; private set; } = new();
    public List<GitLabCommit> ReleaseCommits { get; private set; } = new();
    public AutomationSnapshot? ReleaseContent { get; private set; }
    public List<GitLabBranch> ReleasedReleases { get; private set; } = new();

    /// <summary>Почему нельзя собрать релиз (версия dev не выше main); null - можно.</summary>
    public string? ReleaseBlocked { get; private set; }
    public AutomationSnapshot? DevContent { get; private set; }
    public AutomationSnapshot? MainContent { get; private set; }
    public bool HasDev { get; private set; }
    public bool HasMain { get; private set; }
    public AutomationVersion? DevVersion => AutomationVersion.Find(DevContent?.Triggers);
    public AutomationVersion? MainVersion => AutomationVersion.Find(MainContent?.Triggers);
    public string? LastRelease { get; private set; }

    /// <summary>Сервер контура против ветки контура.</summary>
    public sealed record EnvState(EnvironmentSettings Env, AutomationSnapshot? Pilot, SyncState State, string? Error);
    public List<EnvState> Envs { get; } = new();

    public BranchActivityResult Branches { get; private set; } = new();
    public ModuleNextStep? NextStep { get; private set; }

    public string Q => Uri.EscapeDataString(Name);

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        if (!GitLabConnected)
            return RedirectToGitLab();
        ViewData["Section"] = "automations";
        await LoadAutomationsAccessAsync(ct);
        await LoadAsync(ct);
        return Page();
    }

    /// <summary>
    /// «Влить в тест»: содержимое ветки задачи уходит обычной отправкой в dev (MR, pipeline, запись на тестовый сервер, merge),
    /// после успеха ветка удаляется. Если dev ушёл вперёд, сначала «Обновить из dev», иначе его изменения затёрлись бы.
    /// </summary>
    public async Task<IActionResult> OnPostMergeTaskAsync(CancellationToken ct)
    {
        if (!GitLabConnected)
            return RedirectToGitLab();
        if (await DenyAutomationsChangeAsync(ct) is { } denied)
            return denied;
        ViewData["Section"] = "automations";
        await LoadAsync(ct);
        if (Repo is null || Test is null)
            return Page();
        var work = Work.FirstOrDefault(w => w.Branch.Branch.Name == Branch && w.Branch.Kind == "задача");
        if (work is null)
        {
            Errors.Add($"Ветки задачи «{Branch}» нет.");
            return Page();
        }
        if (work.Behind > 0)
        {
            Errors.Add($"В dev есть {ModuleNextSteps.Plural(work.Behind, "коммит", "коммита", "коммитов")}, которых нет в {Branch}: нажмите «Обновить из dev», затем влейте.");
            return Page();
        }

        var git = CreateGitLabClient();
        try
        {
            var content = await AutomationOperations.ReadBranchAsync(git, Repo, work.Branch.Branch.Name, ct);
            if (content is null)
            {
                Errors.Add($"В ветке {Branch} нет {AutomationFiles.JsonPath(Repo.Name)}.");
                return Page();
            }
            var commits = (await git.CompareCommitsAsync(Repo.ProjectPath, AutomationRepositories.DevBranch, work.Branch.Branch.Name, ct))
                .Where(c => c.ParentIds is not { Count: > 1 }).Select(c => ChangelogSource.StripName(c.Title ?? "", Repo.Name)).Where(t => t.Length > 0).Distinct().ToList();
            var task = ModuleBranches.Task(work.Branch.Branch.Name, AppSettings.TaskPattern);
            var notes = commits.Count > 0 ? string.Join("\n", commits) : work.Branch.Branch.Name;
            var message = AutomationChecks.BuildCommitMessage(Test, Repo.Name, notes, task, out var error);
            if (error is not null)
            {
                Errors.Add(error);
                return Page();
            }
            var job = _publish.Start(
                new PublishRequest
                {
                    Env = Test.Key,
                    Name = Repo.Name,
                    Triggers = content.Triggers,
                    CSharpCode = content.CSharpCode,
                    IsPaused = Envs.FirstOrDefault(e => e.Env == Test)?.Pilot?.IsPaused ?? false,
                    CommitMessage = message,
                    ChangeNotes = Changelog.SplitNotes(notes),
                    TaskReference = task,
                    SourceBranch = work.Branch.Branch.Name,
                },
                new PublishContext
                {
                    Pilot = Vault.GetPilotLogin(CurrentSession, Test.Key),
                    StagePilot = AutoStage && Stage is not null ? Vault.GetPilotLogin(CurrentSession, Stage.Key) : null,
                    Types = AutoStage ? ProAdmin.Pages.Types.TypesContexts.Follow(this) : null,
                    ModulesLogin = ModuleLogin(Test.Key),
                    GitLabToken = CurrentSession.GitLabToken!,
                    Settings = AppSettings,
                    ProjectPath = Repo.ProjectPath,
                    CiConfigPath = Repo.CiConfigPath,
                },
                CurrentSession.PilotLogin, CurrentSession.GitLabUser?.Username, $"влить {work.Branch.Branch.Name} / dev");
            return RedirectToPage("/Jobs/Details", new { id = job.Id });
        }
        catch (Exception ex) when (ex is GitLabApiException or InvalidOperationException)
        {
            Errors.Add(ex.Message);
            return Page();
        }
    }

    /// <summary>«Обновить из dev»: MR dev / ветка задачи и его влитие (при конфликте - MR остаётся, разрешить в GitLab).</summary>
    public async Task<IActionResult> OnPostUpdateFromDevAsync(CancellationToken ct)
    {
        if (!GitLabConnected)
            return RedirectToGitLab();
        if (await DenyAutomationsChangeAsync(ct) is { } denied)
            return denied;
        ViewData["Section"] = "automations";
        await LoadAsync(ct);
        if (Repo is null || Work.FirstOrDefault(w => w.Branch.Branch.Name == Branch && w.Branch.Kind == "задача") is not { } work)
        {
            Errors.Add($"Ветки задачи «{Branch}» нет.");
            return Page();
        }
        var git = CreateGitLabClient();
        try
        {
            var mr = (await git.ListOpenMergeRequestsAsync(Repo.ProjectPath, AutomationRepositories.DevBranch, work.Branch.Branch.Name, ct)).FirstOrDefault()
                     ?? await git.CreateMergeRequestAsync(Repo.ProjectPath, AutomationRepositories.DevBranch, work.Branch.Branch.Name,
                         $"Обновить {work.Branch.Branch.Name} из dev", "Создано PilotProAdmin: изменения dev в ветку задачи перед вливанием.", ct, removeSourceBranch: false);
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    var current = await git.GetMergeRequestAsync(Repo.ProjectPath, mr.Iid, ct);
                    await git.MergeAsync(Repo.ProjectPath, mr.Iid, current.Sha ?? "", ct, removeSourceBranch: false);
                    TempData["Flash"] = $"Ветка {work.Branch.Branch.Name} обновлена из dev (MR !{mr.Iid}). Теперь её можно влить.";
                    break;
                }
                catch (GitLabApiException) when (attempt < 10)
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), ct);
                }
                catch (GitLabApiException ex)
                {
                    TempData["FlashError"] = $"MR !{mr.Iid} dev / {work.Branch.Branch.Name} не влился автоматически ({ex.Message}). Разрешите конфликт в GitLab: {mr.WebUrl}";
                    break;
                }
            }
        }
        catch (GitLabApiException ex)
        {
            Errors.Add($"GitLab: {ex.Message}");
            return Page();
        }
        return RedirectToPage(new { name = Name });
    }

    /// <summary>«Релиз / стейдж»: release/X.Y.Z от dev (или активный релиз заново) на сервер стейджа.</summary>
    public async Task<IActionResult> OnPostReleaseAsync(CancellationToken ct)
    {
        if (!GitLabConnected)
            return RedirectToGitLab();
        if (await DenyAutomationsChangeAsync(ct) is { } denied)
            return denied;
        ViewData["Section"] = "automations";
        await LoadAsync(ct);
        if (Repo is null || Stage is null)
            return Page();
        if (ActiveRelease is null && ReleaseBlocked is not null)
        {
            Errors.Add(ReleaseBlocked);
            return Page();
        }
        try
        {
            var job = _releases.Start(new ContentReleaseRequest { Name = Repo.Name, ProjectPath = Repo.ProjectPath },
                new ContentReleaseContext
                {
                    StageLogin = Vault.GetPilotLogin(CurrentSession, Stage.Key), GitLabToken = CurrentSession.GitLabToken!, Settings = AppSettings,
                    Types = ProAdmin.Pages.Types.TypesContexts.Follow(this),
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

    /// <summary>Удалить ветки без своих коммитов, из которых уже влит MR (временные ветки правок и т.п.), и выпущенные релизы.</summary>
    public async Task<IActionResult> OnPostCleanupBranchesAsync(CancellationToken ct)
    {
        if (!GitLabConnected)
            return RedirectToGitLab();
        if (await DenyAutomationsChangeAsync(ct) is { } denied)
            return denied;
        ViewData["Section"] = "automations";
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
        Test = AppSettings.Environments.FirstOrDefault(e => e.HasServer && e.Branch == AutomationRepositories.DevBranch);
        Prod = AppSettings.Environments.FirstOrDefault(e => e.HasServer && e.Branch == AutomationRepositories.MainBranch);
        Stage = ContentReleases.StageEnv(AppSettings);
        if (GroupPath is null)
        {
            Errors.Add("В настройках не указана группа GitLab с автоматизациями.");
            return;
        }

        var git = CreateGitLabClient();
        AutomationPresence presence;
        try
        {
            presence = await _operations.FindAsync(AppSettings, env => Vault.GetPilotLogin(CurrentSession, env), git, Name, ct);
            Repo = presence.Repo;
            if (Repo is not null)
            {
                var branches = await git.ListBranchesAsync(Repo.ProjectPath, null, ct) ?? new();
                if (branches.Any(b => b.Name == AutomationRepositories.MainBranch))
                    {
                    var rollback = await ContentNextSteps.RollbackAtMainAsync(git, Repo.ProjectPath, ct);
                    RollbackTo = rollback?.Target;
                    UndoRollbackTag = rollback?.UndoTag;
                }
                HasDev = branches.Any(b => b.Name == AutomationRepositories.DevBranch);
                HasMain = branches.Any(b => b.Name == AutomationRepositories.MainBranch);
                if (HasDev) DevContent = await AutomationOperations.ReadBranchAsync(git, Repo, AutomationRepositories.DevBranch, ct);
                if (HasMain) MainContent = await AutomationOperations.ReadBranchAsync(git, Repo, AutomationRepositories.MainBranch, ct);

                var tags = await git.ListVersionTagsAsync(Repo.ProjectPath, ct) ?? new();
                LastRelease = tags.Select(t => AutomationVersion.TryParseTag(t.Name, out var v) ? (AutomationVersion?)v : null)
                    .Where(v => v is not null).Max() is { } last ? "v" + last : null;

                ActiveRelease = AutoStage ? null : ContentReleases.ActiveAutomationRelease(branches, MainVersion);
                ReleasedReleases = ContentReleases.ReleasedAutomationReleases(branches, MainVersion);
                if (ActiveRelease is not null)
                    ReleaseContent = await AutomationOperations.ReadBranchAsync(git, Repo, ActiveRelease.Name, ct);
                ReleaseBlocked = DevVersion is not { } devV ? "У автоматизации в dev нет версии (Version: X.Y.Z) - сохраните её в редакторе на тесте."
                    : MainVersion is { } mainV && devV <= mainV ? $"Версия в dev ({devV}) не выше, чем на проде ({mainV}): поднимите её, сохранив изменения в редакторе на тесте."
                    : null;

                if (HasDev && HasMain && !SameContent(DevContent, MainContent))
                    DevCommits = ContentReleases.NotOnProd((await git.CompareCommitsAsync(Repo.ProjectPath, AutomationRepositories.MainBranch, AutomationRepositories.DevBranch, ct))
                        .Where(c => c.ParentIds is not { Count: > 1 }).Reverse(), branches);
                if (ActiveRelease is not null)
                    ReleaseCommits = ContentReleases.NotOnProd((await git.CompareCommitsAsync(Repo.ProjectPath, AutomationRepositories.MainBranch, ActiveRelease.Name, ct))
                        .Where(c => c.ParentIds is not { Count: > 1 }), branches);

                var openMrs = await git.ListOpenMergeRequestsToAsync(Repo.ProjectPath, null, ct) ?? new();
                Branches = await BranchActivity.LoadAsync(git, Repo.ProjectPath, branches, openMrs, b => Classify(b.Name), ct);
                foreach (var a in Branches.Active.Where(a => a.Kind is "задача" or "hotfix"))
                    Work.Add(new WorkBranch(a, (await git.CompareCommitsAsync(Repo.ProjectPath, a.Branch.Name, a.Base, ct)).Count));
                if (ActiveRelease is not null)
                {
                    var ahead = (await git.CompareCommitsAsync(Repo.ProjectPath, AutomationRepositories.MainBranch, ActiveRelease.Name, ct))
                        .Count(c => c.ParentIds is not { Count: > 1 });
                    Branches.Active.Add(new ActiveBranch(ActiveRelease, "стейдж", AutomationRepositories.MainBranch, ahead,
                        openMrs.FirstOrDefault(m => m.SourceBranch == ActiveRelease.Name)));
                }
                // dev впереди main - изменения на тесте, ещё не перенесённые на прод.
                if (HasDev && HasMain && !SameContent(DevContent, MainContent) && branches.First(b => b.Name == AutomationRepositories.DevBranch) is var dev)
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

        foreach (var (env, pilot, error) in presence.Servers)
        {
            var branch = env.Branch == AutomationRepositories.DevBranch ? DevContent : env.Branch == AutomationRepositories.MainBranch ? MainContent : null;
            var state = error is not null || Repo is null ? SyncState.Unknown
                : branch is null ? (pilot is null ? SyncState.Unknown : SyncState.OnlyPilot)
                : pilot is null ? SyncState.OnlyGit
                : SameContent(pilot, branch) ? SyncState.Synced : SyncState.Differs;
            Envs.Add(new EnvState(env, pilot, state, error));
            if (error is not null)
                Errors.Add($"{env.Title}: {error}");
        }
        if (Stage is not null)
        {
            // Стейдж - против активного релиза; без релиза - против main (там последний выпущенный); в режиме «вместе» - против dev.
            var expected = AutoStage ? DevContent : ActiveRelease is not null ? ReleaseContent : MainContent;
            try
            {
                var pilot = await _pilot.GetAutomationAsync(Stage.PilotUrl, Vault.GetPilotLogin(CurrentSession, Stage.Key), Name, ct);
                var state = Repo is null || expected is null ? (pilot is null ? SyncState.Unknown : SyncState.OnlyPilot)
                    : pilot is null ? SyncState.OnlyGit
                    : SameContent(pilot, expected) ? SyncState.Synced : SyncState.Differs;
                Envs.Insert(Math.Min(1, Envs.Count), new EnvState(Stage, pilot, state, null));
            }
            catch (PilotGatewayException ex)
            {
                Envs.Insert(Math.Min(1, Envs.Count), new EnvState(Stage, null, SyncState.Unknown, ex.Message));
                Errors.Add($"{Stage.Title}: {ex.Message}");
            }
        }

        var devAhead = HasDev && HasMain && DevContent is not null && !SameContent(DevContent, MainContent);
        var mainAhead = devAhead && MainVersion is { } mv && (DevVersion is not { } dv || mv > dv);
        NextStep = ContentNextSteps.For(new ContentFlowInput
        {
            Noun = "автоматизация",
            HasRepo = Repo is not null,
            HasDev = HasDev,
            HasMain = HasMain,
            DevAhead = devAhead && !mainAhead,
            MainAhead = mainAhead,
            RollbackTo = RollbackTo,
            UndoRollbackUrl = RollbackTo is not null && UndoRollbackTag is not null ? $"/Automations/Rollback?name={Q}&tag={Uri.EscapeDataString(UndoRollbackTag)}" : null,
            Test = Envs.FirstOrDefault(e => e.Env == Test)?.State,
            Prod = Envs.FirstOrDefault(e => e.Env == Prod)?.State,
            Stage = Envs.FirstOrDefault(e => e.Env == Stage)?.State,
            HasStage = Stage is not null && !AutoStage,
            Release = ActiveRelease?.Name,
            ReleaseBlocked = ReleaseBlocked,
            Hotfix = Work.FirstOrDefault(w => w.Branch.Kind == "hotfix" && w.Branch.Ahead > 0)?.Branch.Branch.Name,
            HotfixUrl = Work.FirstOrDefault(w => w.Branch.Kind == "hotfix" && w.Branch.Ahead > 0) is { } h
                ? $"/Automations/Promote?name={Q}&from={Uri.EscapeDataString(h.Branch.Branch.Name)}" : null,
            ReadyTask = Work.FirstOrDefault(w => w.Branch.Kind == "задача" && w.Branch.Ahead > 0)?.Branch.Branch.Name,
            TestDiffersHint = "На тестовом сервере не то, что в dev: правили напрямую в Pilot или запись не прошла. Сохраните автоматизацию в редакторе - она пройдёт через dev.",
            LastRelease = LastRelease,
            CreateUrl = $"/Automations/Create?source={Q}",
            PromoteUrl = $"/Automations/Promote?name={Q}",
            BackportUrl = Test is null ? null : $"/Automations/{Test.Key}/Edit?name={Q}",
            CompareUrl = $"/Compare/Automation?name={Q}",
        });
    }

    /// <summary>Вид ветки и её база: временные ветки правок webadmin/&lt;контур&gt;/… - в ветку контура, прочие - в dev.</summary>
    private (string Kind, string Base)? Classify(string branch)
    {
        if (branch is AutomationRepositories.DevBranch or AutomationRepositories.MainBranch || ContentReleases.Version(branch) is not null)
            return null;
        if (branch.StartsWith("webadmin/", StringComparison.Ordinal))
        {
            var env = branch.Split('/')[1];
            var target = AppSettings.Find(env)?.Branch;
            return ("правка", string.IsNullOrEmpty(target) ? AutomationRepositories.DevBranch : target);
        }
        if (ModuleBranches.IsHotfix(branch))
            return ("hotfix", AutomationRepositories.MainBranch);
        return (ModuleBranches.IsTaskBranch(branch) ? "задача" : "вне схемы", AutomationRepositories.DevBranch);
    }

    private static bool SameContent(AutomationSnapshot? a, AutomationSnapshot? b) =>
        a is not null && b is not null &&
        AutomationFiles.SameContent(a.Triggers, b.Triggers) && AutomationFiles.SameContent(a.CSharpCode, b.CSharpCode);
}
