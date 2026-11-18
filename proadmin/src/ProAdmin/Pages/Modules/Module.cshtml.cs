using ProAdmin.Automations;
using ProAdmin.GitLab;
using ProAdmin.Modules;
using ProAdmin.Pilot;
using ProAdmin.Types;
using Microsoft.AspNetCore.Mvc;

namespace ProAdmin.Pages.Modules;

/// <summary>
/// Один модуль: серверы и ветки, задачи (feature / dev), релиз (dev / release / стейдж / main / прод),
/// hotfix (main / прод / dev/release) и откат прода. Операции идут в фоне, журнал - «Операции».
/// </summary>
public class ModuleModel : AppPageModel
{
    private readonly ModuleCatalog _catalog;
    private readonly ModuleTargets _targets;
    private readonly ModuleJobRunner _runner;
    private readonly ModuleAvailabilityService _availability;

    public ModuleModel(ModuleCatalog catalog, ModuleTargets targets, ModuleJobRunner runner, ModuleAvailabilityService availability)
    {
        _availability = availability;
        _catalog = catalog;
        _targets = targets;
        _runner = runner;
    }

    [BindProperty(SupportsGet = true)] public string Name { get; set; } = "";

    /// <summary>server - серверный модуль (группа серверных модулей, выкладка в ServerHost).</summary>
    [BindProperty(SupportsGet = true)] public string? Kind { get; set; }
    public ModuleKind ModuleKind => ModuleKinds.Parse(Kind);
    public bool IsServer => ModuleKind == ModuleKind.Server;
    public override string? ModulesGroupPath => ModuleKind.GroupPath(AppSettings);
    protected override object? ModulesListRoute => new { kind = ModuleKind.Query() };

    // Поля форм
    [BindProperty] public string? BranchKind { get; set; }
    [BindProperty] public string? TaskId { get; set; }
    [BindProperty] public string? Slug { get; set; }
    [BindProperty] public string? BaseBranch { get; set; }
    [BindProperty] public long? Iid { get; set; }
    [BindProperty] public string? Env { get; set; }
    [BindProperty] public string? Ref { get; set; }
    [BindProperty] public string? Version { get; set; }
    [BindProperty] public List<string> Selected { get; set; } = new();
    [BindProperty] public string? Notes { get; set; }
    [BindProperty] public bool AllowUntested { get; set; }
    [BindProperty] public string? Tag { get; set; }
    [BindProperty] public string? Confirm { get; set; }
    [BindProperty] public string? SkipReviewReason { get; set; }
    [BindProperty] public string? AvailabilityEnv { get; set; }
    [BindProperty] public string? AvailabilityReason { get; set; }
    [BindProperty] public string? AvailabilityConfirm { get; set; }

    public List<string> Errors { get; } = new();
    public ModuleRepoState? Git { get; private set; }
    public List<ModuleEnv> Envs { get; private set; } = new();
    public Dictionary<string, ModuleEnvState> EnvStates { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<GitLabMergeRequest> OpenMergeRequests { get; private set; } = new();
    public List<GitLabCommit> Unreleased { get; private set; } = new();

    /// <summary>Заголовки задач, которые уже в активном релизе (для выборочного релиза сверяются по заголовку).</summary>
    public HashSet<string> InRelease { get; } = new(StringComparer.Ordinal);
    public List<(ModuleVersion Version, GitLabTag Tag)> ProdReleases { get; private set; } = new();
    public ModuleVersion? LastProdVersion => ProdReleases.Select(r => r.Version).FirstOrDefault();
    public ModuleVersion SuggestedVersion { get; private set; } = new(1, 0, 0);

    /// <summary>Одобрившие MR в dev (не автор), по iid.</summary>
    public Dictionary<long, List<string>> DevReviewers { get; } = new();

    /// <summary>Контур / pipeline, который сейчас собирает/выкладывает голову его ветки.</summary>
    public Dictionary<string, GitLabPipeline> Deploying { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Ветка в работе (с последнего релиза): куда идёт, сколько коммитов впереди, её MR.</summary>
    public sealed record WorkBranch(GitLabBranch Branch, string Target, List<GitLabCommit> Commits, GitLabMergeRequest? Mr);

    /// <summary>Ветки задач и hotfix, у которых есть коммиты сверх своей базовой ветки.</summary>
    public List<WorkBranch> Work { get; } = new();

    /// <summary>Ветки задач/hotfix без новых коммитов (уже влиты) - можно удалить.</summary>
    public List<GitLabBranch> MergedBranches { get; } = new();

    /// <summary>Активные ветки, свежие сверху.</summary>
    public List<ActiveBranch> ActiveBranches { get; } = new();

    /// <summary>Ветки вне схемы без своих коммитов относительно dev.</summary>
    public List<GitLabBranch> IdleOtherBranches { get; } = new();

    /// <summary>Что удалит «Удалить выпущенные и влитые ветки»: выпущенные release/X.Y.Z (версия ≤ main) и влитые ветки задач/hotfix.</summary>
    public List<GitLabBranch> ReleasedBranches => Git is null ? new() : Git.ReleaseBranches
        .Where(b => Git.MainVersion is { } mv && ModuleBranches.ReleaseVersion(b.Name) is { } rv && rv <= mv).ToList();

    /// <summary>Задачи на стейдже (в активном релизе, нет в main).</summary>
    public List<GitLabCommit> ReleaseTasks { get; private set; } = new();

    /// <summary>.gitlab-ci.yml в dev отличается от шаблона админки.</summary>
    public bool CiOutdated { get; private set; }

    /// <summary>Версия в dev записана не только в &lt;Version&gt; csproj (FileVersion, AssemblyInfo).</summary>
    public bool VersionScattered { get; private set; }

    /// <summary>Администратор GitLab: может влить в dev без Code Review (с причиной).</summary>
    public bool CanSkipReview => CurrentSession.GitLabUser?.IsAdmin == true;

    public IEnumerable<GitLabMergeRequest> MergeRequestsTo(string? target) => OpenMergeRequests.Where(m => m.TargetBranch == target);

    /// <summary>Где сейчас модуль и что сделать дальше (блок вверху страницы).</summary>
    public ModuleNextStep? NextStep { get; private set; }

    private ModuleNextStep BuildNextStep()
    {
        var git = Git!;
        var noMr = Work.FirstOrDefault(w => w.Mr is null && w.Target != ModuleBranches.Main && w.Commits.Count > 0);
        return ModuleNextSteps.For(new NextStepInput
        {
            Ready = git.Dev is not null && git.HasCi,
            NeedReview = AppSettings.RequireDevReview,
            HasStage = Envs.Any(e => e.Key == ModuleEnv.Stage),
            Release = git.ActiveRelease?.Name,
            ReleaseMrs = git.ActiveRelease is { } r ? MergeRequestsTo(r.Name).Count() : 0,
            DevMrs = MergeRequestsTo(ModuleBranches.Dev)
                .Select(m => new NextStepMr(m.Iid, m.Title, m.Draft, DevReviewers.GetValueOrDefault(m.Iid) is { Count: > 0 }, m.WebUrl, m.HeadPipeline?.Status))
                .ToList(),
            Test = EnvStates.GetValueOrDefault(ModuleEnv.Test)?.State,
            Stage = EnvStates.GetValueOrDefault(ModuleEnv.Stage)?.State,
            Prod = EnvStates.GetValueOrDefault(ModuleEnv.Prod)?.State,
            DeployingTest = Deploying.GetValueOrDefault(ModuleEnv.Test)?.WebUrl,
            DeployingStage = Deploying.GetValueOrDefault(ModuleEnv.Stage)?.WebUrl,
            Unreleased = Unreleased.Count,
            LastProd = LastProdVersion,
            Hotfix = Work.FirstOrDefault(w => w.Target == ModuleBranches.Main && w.Commits.Count > 0)?.Branch.Name,
            BranchWithoutMr = noMr?.Branch.Name,
            EmptyBranch = Work.FirstOrDefault(w => w.Mr is null && w.Commits.Count == 0)?.Branch.Name,
            NewMrUrl = noMr is null ? null
                : $"{git.Repo.WebUrl}/-/merge_requests/new?merge_request%5Bsource_branch%5D={Uri.EscapeDataString(noMr.Branch.Name)}&merge_request%5Btarget_branch%5D=dev&merge_request%5Bsquash%5D=1",
        });
    }

    public string? TaskOf(string? text) => ModuleBranches.Task(text, AppSettings.TaskPattern);
    public string? TaskUrl(string? task) => ModuleBranches.TaskUrl(task, AppSettings.TaskUrlTemplate);

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        if (!GitLabConnected)
            return RedirectToGitLab();
        ViewData["Section"] = IsServer ? "servermodules" : "modules";
        await LoadModulesAccessAsync(ct);
        await LoadAsync(ct);
        return Page();
    }

    // ------------------------------------------------------------------ синхронные действия

    /// <summary>Ветка задачи: feature/bugfix от dev (bugfix - можно от релиза), hotfix от main.</summary>
    public Task<IActionResult> OnPostCreateBranchAsync(CancellationToken ct) => ChangeAsync(ct, async git =>
    {
        var task = ModuleBranches.ParseTask(TaskId, AppSettings.TaskPattern);
        if (task is null && !string.IsNullOrWhiteSpace(TaskId))
            return Fail($"«{TaskId.Trim()}» не похоже на номер задачи ({AppSettings.TaskPattern}), например KTN-1234. Оставьте поле пустым, чтобы создать ветку без задачи.");
        if (task is null && string.IsNullOrWhiteSpace(Slug))
            return Fail("Укажите номер задачи или кратко, что делается в ветке, - из этого получится её имя.");

        var (prefix, from) = BranchKind switch
        {
            "hotfix" => (ModuleBranches.HotfixPrefix, ModuleBranches.Main),
            "bugfix" => (ModuleBranches.BugfixPrefix, BaseBranch is { } b && ModuleBranches.IsRelease(b) ? b : ModuleBranches.Dev),
            _ => (ModuleBranches.FeaturePrefix, ModuleBranches.Dev),
        };
        var branch = ModuleBranches.TaskBranch(prefix, task, Slug);
        if (await git.FindBranchAsync(Git!.Repo.ProjectPath, branch, ct) is not null)
            return Fail($"Ветка {branch} уже есть.");
        await git.CreateBranchAsync(Git.Repo.ProjectPath, branch, from, ct);
        TempData["Flash"] = $"Создана ветка {branch} от {from}. Склонируйте репозиторий и переключитесь: git fetch && git switch {branch}";
        return null;
    });

    /// <summary>Отмена релиза (критичный баг на стейдже): MR в релиз закрываются, ветки release/X.Y.Z и release-prep/X.Y.Z удаляются.</summary>
    public Task<IActionResult> OnPostCancelReleaseAsync(CancellationToken ct) => ChangeAsync(ct, async git =>
    {
        var release = Git!.ActiveRelease?.Name;
        if (release is null)
            return Fail("Активного релиза нет.");
        if (Confirm != release)
            return Fail($"Для отмены введите имя ветки: {release}.");
        var project = Git.Repo.ProjectPath;
        foreach (var mr in MergeRequestsTo(release).ToList())
            await git.CloseMergeRequestAsync(project, mr.Iid, ct);
        var version = ModuleBranches.ReleaseVersion(release)!;
        await git.DeleteBranchAsync(project, ModuleBranches.ReleasePrep(version), ct);
        await git.DeleteBranchAsync(project, release, ct);
        TempData["Flash"] = $"Релиз {version} отменён: ветка {release} удалена. Исправьте баг в dev и подготовьте новый релиз. На стейдже остаётся последняя выложенная версия.";
        return null;
    });

    /// <summary>Защита dev: push - никто, merge - Developer+ (Code Review проверяет админка: «Влить / тест» только с апрувом).</summary>
    private const int DevPushLevel = 0, DevMergeLevel = 30;

    /// <summary>Защитить dev: изменения только через Merge Request.</summary>
    public Task<IActionResult> OnPostProtectDevAsync(CancellationToken ct) => ChangeAsync(ct, async git =>
    {
        if (Git!.Dev is null)
            return Fail("В репозитории нет ветки dev - сначала «Подключить».");
        var done = await git.ProtectBranchAsync(Git.Repo.ProjectPath, ModuleBranches.Dev, DevPushLevel, DevMergeLevel, ct);
        TempData["Flash"] = done ? "Ветка dev защищена: push запрещён, изменения - только через Merge Request." : "Ветка dev уже защищена.";
        return null;
    });

    /// <summary>
    /// Удалить выпущенные release/X.Y.Z (версия ≤ main, всё уже в main; история - в тегах) и влитые ветки задач/hotfix
    /// без своих коммитов. Ветки вне схемы не трогаются.
    /// </summary>
    public Task<IActionResult> OnPostCleanupBranchesAsync(CancellationToken ct) => ChangeAsync(ct, async git =>
    {
        var project = Git!.Repo.ProjectPath;
        var candidates = ReleasedBranches.Concat(MergedBranches).ToList();
        var deleted = new List<string>();
        var kept = new List<string>();
        foreach (var b in candidates)
        {
            // Удаляем, только если в ветке нет коммитов, которых нет в main (иначе это несохранённая работа).
            var ahead = await git.CompareCommitsAsync(project, ModuleBranches.Main, b.Name, ct);
            if (ahead.Count > 0 || OpenMergeRequests.Any(m => m.SourceBranch == b.Name))
            {
                kept.Add(b.Name);
                continue;
            }
            await git.DeleteBranchAsync(project, b.Name, ct);
            deleted.Add(b.Name);
        }
        TempData["Flash"] = (deleted.Count == 0 ? "Удалять нечего." : $"Удалены ветки: {string.Join(", ", deleted)}.") +
                            (kept.Count == 0 ? "" : $" Оставлены (есть коммиты не из main или открытый MR): {string.Join(", ", kept)}.");
        return null;
    });

    /// <summary>.gitlab-ci.yml в dev / шаблон админки: MR ci/template-… в dev, pipeline, merge.</summary>
    public Task<IActionResult> OnPostUpdateCiAsync(CancellationToken ct) => StartAsync(ct, ctx =>
    {
        if (Git!.Dev is null)
            throw new InvalidOperationException("В репозитории нет ветки dev - сначала «Подключить».");
        return _runner.StartCiUpdate(ctx, ModuleCiTemplate.Content(AppSettings, ModuleKind), CurrentSession.PilotLogin, CurrentSession.GitLabUser?.Username);
    });

    /// <summary>Подключить репозиторий: ветка dev от main, шаблон .gitlab-ci.yml в dev, если его нет, защита dev.</summary>
    public Task<IActionResult> OnPostSetupAsync(CancellationToken ct) => ChangeAsync(ct, async git =>
    {
        var project = Git!.Repo.ProjectPath;
        var done = new List<string>();
        if (Git.Main is null)
            return Fail("В репозитории нет ветки main - создайте её (ветка по умолчанию) и повторите.");
        if (Git.Dev is null)
        {
            await git.CreateBranchAsync(project, ModuleBranches.Dev, ModuleBranches.Main, ct);
            done.Add("создана ветка dev от main");
        }
        if (!Git.HasCi)
        {
            try
            {
                await git.CreateCommitAsync(project, ModuleBranches.Dev, null, "CI/CD модуля: .gitlab-ci.yml (сборка zip для Pilot)",
                    new[] { GitLabCommitAction.Create(".gitlab-ci.yml", ModuleCiTemplate.Content(AppSettings, ModuleKind)) }, ct);
            }
            catch (GitLabApiException ex) when (ex.StatusCode is 403 or 400)
            {
                // dev уже защищена (push: никто): прямой коммит запрещён, шаблон нужно вносить через Merge Request.
                return Fail($"Не удалось закоммитить .gitlab-ci.yml в dev: {ex.Message}. Если dev защищена от push, добавьте файл через Merge Request в dev.");
            }
            done.Add("в dev добавлен .gitlab-ci.yml (в main придёт с ближайшим релизом)");
        }
        // После коммита CI: защищённая dev принимает изменения только через Merge Request.
        if (await git.ProtectBranchAsync(project, ModuleBranches.Dev, DevPushLevel, DevMergeLevel, ct))
            done.Add("ветка dev защищена (только через Merge Request)");
        TempData["Flash"] = done.Count == 0 ? "Репозиторий уже подключён." : "Готово: " + string.Join("; ", done) + ".";
        return null;
    });

    // ------------------------------------------------------------------ фоновые операции

    /// <summary>Запустить заново упавшие pipeline Merge Request: при временном сбое код менять не нужно, новых pipeline без push нет.</summary>
    public Task<IActionResult> OnPostRetryPipelinesAsync(CancellationToken ct) => ChangeAsync(ct, async git =>
    {
        var mr = OpenMergeRequests.FirstOrDefault(m => m.Iid == Iid);
        if (mr is null)
            return Fail($"Открытый Merge Request !{Iid} не найден.");
        var project = Git!.Repo.ProjectPath;
        var failed = MergeRequestChecks.Latest(await git.ListMergeRequestChecksAsync(project, mr.Iid, mr.Sha ?? "", ct))
            .Where(p => p.CanRetry).ToList();
        if (failed.Count == 0)
            return Fail($"У !{mr.Iid} нет упавших pipeline.");
        foreach (var p in failed)
            await git.RetryPipelineAsync(project, p.Id, ct);
        TempData["Flash"] = $"!{mr.Iid}: pipeline {string.Join(", ", failed.Select(p => "#" + p.Id))} запущен заново. Когда он пройдёт, MR можно будет влить.";
        return null;
    });

    /// <summary>feature/bugfix / dev (squash) / тестовый сервер.</summary>
    public Task<IActionResult> OnPostMergeToDevAsync(CancellationToken ct) => StartAsync(ct, ctx =>
    {
        var mr = OpenMergeRequests.FirstOrDefault(m => m.Iid == Iid && m.TargetBranch == ModuleBranches.Dev)
                 ?? throw new InvalidOperationException($"Открытый Merge Request !{Iid} в dev не найден.");
        string? skip = null;
        if (AppSettings.RequireDevReview && DevReviewers.GetValueOrDefault(mr.Iid) is not { Count: > 0 })
        {
            if (!CanSkipReview)
                throw new InvalidOperationException($"Merge Request !{mr.Iid} не одобрен: нужен Approve в GitLab от ревьюера (не автора).");
            skip = string.IsNullOrWhiteSpace(SkipReviewReason)
                ? throw new InvalidOperationException("Чтобы влить без Code Review, укажите причину.")
                : SkipReviewReason.Trim();
        }
        return _runner.StartMergeToDev(ctx, mr.Iid, mr.SourceBranch ?? "", CurrentSession.PilotLogin, CurrentSession.GitLabUser?.Username, skip);
    });

    /// <summary>Выложить голову ветки на контур: dev / тест, release/hotfix / стейдж.</summary>
    public Task<IActionResult> OnPostDeployAsync(CancellationToken ct) => StartAsync(ct, ctx =>
    {
        var reference = Ref ?? "";
        var allowed = Env switch
        {
            ModuleEnv.Test => reference == ModuleBranches.Dev || ModuleBranches.IsTaskBranch(reference),
            ModuleEnv.Stage => ModuleBranches.IsRelease(reference) || ModuleBranches.IsHotfix(reference),
            _ => false,
        };
        if (!allowed)
            throw new InvalidOperationException($"Ветку {reference} нельзя выкладывать на контур {Env}: тест / dev, стейдж / release/* и hotfix/*. Прод - только через «Выпустить в прод».");
        return _runner.StartDeploy(ctx, Env!, reference, Changelog.SplitNotes(Notes), CurrentSession.PilotLogin, CurrentSession.GitLabUser?.Username);
    });

    public Task<IActionResult> OnPostPrepareReleaseAsync(CancellationToken ct) => StartAsync(ct, ctx =>
    {
        if (!ModuleVersion.TryParse(Version, out var version))
            throw new InvalidOperationException("Версия релиза - X.Y.Z, например 1.4.0.");
        if (Git!.ActiveRelease is not null)
            throw new InvalidOperationException($"Уже идёт релиз {Git.ActiveRelease.Name} - выпустите или отмените его.");
        if (LastProdVersion is { } last && version! <= last)
            throw new InvalidOperationException($"Версия {version} не больше последней на проде ({last}).");
        // Всегда явный список: задача, влитая в dev после открытия формы, без выбора в релиз не попадёт.
        var all = Unreleased.Select(c => c.Id).ToHashSet();
        IReadOnlyCollection<string> selected = Selected.Where(all.Contains).Distinct().ToList();
        if (selected.Count == 0)
            throw new InvalidOperationException("Не выбрано ни одной задачи.");
        return _runner.StartPrepareRelease(ctx, version!, selected, Changelog.SplitNotes(Notes), CurrentSession.PilotLogin, CurrentSession.GitLabUser?.Username);
    });

    /// <summary>Выбранные задачи dev, которых нет в активном релизе, - cherry-pick в релиз и на стейдж.</summary>
    public Task<IActionResult> OnPostAddToReleaseAsync(CancellationToken ct) => StartAsync(ct, ctx =>
    {
        var release = Git!.ActiveRelease?.Name ?? throw new InvalidOperationException("Активного релиза нет - сначала «Релиз / стейдж».");
        var all = Unreleased.Where(c => !InRelease.Contains(c.Title ?? "")).Select(c => c.Id).ToHashSet();
        IReadOnlyCollection<string> selected = Selected.Where(all.Contains).Distinct().ToList();
        if (selected.Count == 0)
            throw new InvalidOperationException("Не выбрано ни одной задачи.");
        return _runner.StartAddToRelease(ctx, release, selected, CurrentSession.PilotLogin, CurrentSession.GitLabUser?.Username);
    });

    public Task<IActionResult> OnPostStageAsync(CancellationToken ct) => StartAsync(ct, ctx =>
    {
        var release = Git!.ActiveRelease?.Name ?? throw new InvalidOperationException("Активного релиза нет - сначала «Релиз / стейдж».");
        if (Iid is { } iid && MergeRequestsTo(release).All(m => m.Iid != iid))
            throw new InvalidOperationException($"Открытый Merge Request !{iid} в {release} не найден.");
        return _runner.StartReleaseToStage(ctx, release, Iid, CurrentSession.PilotLogin, CurrentSession.GitLabUser?.Username);
    });

    public Task<IActionResult> OnPostProdAsync(CancellationToken ct) => StartAsync(ct, ctx =>
    {
        var release = Git!.ActiveRelease?.Name ?? throw new InvalidOperationException("Активного релиза нет.");
        if (MergeRequestsTo(release).Any())
            throw new InvalidOperationException($"В {release} есть невлитые Merge Request - влейте их («На стейдж») или закройте.");
        var task = string.IsNullOrWhiteSpace(TaskId) ? null : TaskId.Trim();
        if (task is null && AppSettings.Find(ModuleEnv.Prod)?.RequireTaskReference == true)
            throw new InvalidOperationException("Для выпуска в прод укажите задачу (так настроен контур «Прод»).");
        return _runner.StartReleaseToProd(ctx, release, task, AllowUntested, CurrentSession.PilotLogin, CurrentSession.GitLabUser?.Username);
    });

    public Task<IActionResult> OnPostHotfixAsync(CancellationToken ct) => StartAsync(ct, ctx =>
    {
        var branch = Git!.HotfixBranches.FirstOrDefault(b => b.Name == Ref)?.Name
                     ?? throw new InvalidOperationException($"Ветки hotfix {Ref} нет.");
        if (Git.Main?.Commit?.Id is { } mainHead && Git.HotfixBranches.First(b => b.Name == branch).Commit?.Id == mainHead)
            throw new InvalidOperationException($"В {branch} нет коммитов поверх main - нечего выпускать.");
        return _runner.StartHotfix(ctx, branch, Changelog.SplitNotes(Notes), Git.ActiveRelease?.Name, CurrentSession.PilotLogin, CurrentSession.GitLabUser?.Username);
    });

    /// <summary>Откат прода на прошлый релиз: артефакт сборки тега vX.Y.Z / прод. Ветки не меняются.</summary>
    public Task<IActionResult> OnPostRollbackAsync(CancellationToken ct) => StartAsync(ct, ctx =>
    {
        var tag = ProdReleases.FirstOrDefault(r => r.Tag.Name == Tag).Tag?.Name
                  ?? throw new InvalidOperationException($"Релиза {Tag} нет.");
        return _runner.StartDeploy(ctx, ModuleEnv.Prod, tag, Changelog.SplitNotes(Notes), CurrentSession.PilotLogin, CurrentSession.GitLabUser?.Username, isTag: true);
    });

    /// <summary>Причина, по которой модуль нельзя отключить (общий, лежит в Preload); null - можно.</summary>
    public string? ProtectedReason(PilotModule module) => _availability.ProtectedReason(module);

    /// <summary>Отключить модуль на контуре: объект в корзину Pilot, клиенты перестают его видеть.</summary>
    public Task<IActionResult> OnPostDisableModuleAsync(CancellationToken ct) => AvailabilityAsync(ct, async (st, login, actor) =>
        st.Pilot is not { } module ? "Модуля нет на этом сервере."
            : await _availability.DisableAsync(st.Env, login, AppSettings.ReportsLicenseType, Git!.Repo.Name, module, AvailabilityReason, actor, ct)
              ?? Done($"Модуль отключён на сервере {st.Env.Title}."));

    public Task<IActionResult> OnPostEnableModuleAsync(CancellationToken ct) => AvailabilityAsync(ct, async (st, login, actor) =>
        st.Disabled is not { } module ? "Модуль на этом сервере не отключён."
            : await _availability.EnableAsync(st.Env, login, AppSettings.ReportsLicenseType, Git!.Repo.Name, module, actor, ct)
              ?? Done($"Модуль включён на сервере {st.Env.Title}."));

    /// <summary>Удалить отключённый модуль с контура окончательно. Репозиторий в GitLab не затрагивается.</summary>
    public Task<IActionResult> OnPostDeleteModuleAsync(CancellationToken ct) => AvailabilityAsync(ct, async (st, login, actor) =>
        st.Disabled is not { } module ? "Удалить можно только отключённый модуль."
            : await _availability.DeleteAsync(st.Env, login, AppSettings.ReportsLicenseType, Git!.Repo.Name, module, AvailabilityConfirm, AvailabilityReason, actor, ct)
              ?? Done($"Модуль удалён с сервера {st.Env.Title}."));

    private string? Done(string message)
    {
        TempData["Flash"] = message;
        return null;
    }

    private Task<IActionResult> AvailabilityAsync(CancellationToken ct, Func<ModuleEnvState, PilotLogin, ModuleActor, Task<string?>> action) => ChangeAsync(ct, async _ =>
    {
        if (IsServer)
            return Fail("Серверные модули отключаются на странице «Серверные модули».");
        if (AvailabilityEnv is null || !EnvStates.TryGetValue(AvailabilityEnv, out var st) || st.State == ModuleSync.Unknown)
            return Fail("Сервер контура недоступен.");
        try
        {
            var error = await action(st, ModuleLogin(st.Env.Key), new ModuleActor(CurrentSession.PilotLogin, CurrentSession.GitLabUser?.Username));
            return error is null ? null : Fail(error);
        }
        catch (PilotGatewayException ex)
        {
            return Fail($"{st.Env.Title}: {ex.Message}");
        }
    });

    // ------------------------------------------------------------------

    private async Task<IActionResult> ChangeAsync(CancellationToken ct, Func<GitLabClient, Task<IActionResult?>> action)
    {
        if (!GitLabConnected)
            return RedirectToGitLab();
        if (await DenyModulesChangeAsync(ct) is { } denied)
            return denied;
        ViewData["Section"] = IsServer ? "servermodules" : "modules";
        await LoadAsync(ct);
        if (Git is null)
            return Page();
        try
        {
            var result = await action(CreateGitLabClient());
            return result ?? RedirectToPage(new { name = Name, kind = ModuleKind.Query() });
        }
        catch (GitLabApiException ex)
        {
            Errors.Add($"GitLab: {ex.Message}");
            return Page();
        }
    }

    private Task<IActionResult> StartAsync(CancellationToken ct, Func<ModuleContext, ProAdmin.Jobs.PublishJob> start) => ChangeAsync(ct, _ =>
    {
        try
        {
            var job = start(Context());
            return Task.FromResult<IActionResult?>(RedirectToPage("/Jobs/Details", new { id = job.Id }));
        }
        catch (InvalidOperationException ex)
        {
            Errors.Add(ex.Message);
            return Task.FromResult<IActionResult?>(Page());
        }
    });

    private IActionResult? Fail(string message)
    {
        Errors.Add(message);
        return Page();
    }

    /// <summary>Невыпущенные изменения типов, связанные с коммитами модуля: едут на стейдж и прод вместе с ним.</summary>
    public List<TypesChangeLink> LinkedTypes => Git is null
        ? new()
        : HttpContext.RequestServices.GetRequiredService<TypesStore>().OpenLinksFor(
            ModuleKind == ModuleKind.Server ? TypesTargetKind.Server : TypesTargetKind.Client, Git.Repo.ProjectPath);

    private ModuleContext Context() => new()
    {
        Settings = AppSettings,
        GitLabToken = CurrentSession.GitLabToken!,
        ProjectPath = Git!.Repo.ProjectPath,
        ProjectWebUrl = Git.Repo.WebUrl,
        RepoName = Git.Repo.Name,
        CiConfigPath = Git.Repo.CiConfigPath,
        ProjectFile = Git.ProjectFile,
        AssemblyInfoFile = Git.AssemblyInfoFile,
        Logins = Envs.ToDictionary(e => e.Key, e => ModuleLogin(e.Key)),
        Kind = ModuleKind,
        Assemblies = HttpContext.RequestServices.GetRequiredService<AssemblySpecStore>().For(ModuleKind),
        Disabled = IsServer ? null : HttpContext.RequestServices.GetRequiredService<DisabledModuleStore>(),
        Types = ProAdmin.Pages.Types.TypesContexts.Follow(this),
    };

    private async Task LoadAsync(CancellationToken ct)
    {
        Envs = ModuleEnv.All(AppSettings);
        if (ModulesGroupPath is null)
        {
            Errors.Add(IsServer ? "В настройках не указана группа GitLab с серверными модулями." : "В настройках не указана группа GitLab с модулями.");
            return;
        }

        var git = CreateGitLabClient();
        try
        {
            var repo = await AutomationRepositories.FindAsync(git, ModulesGroupPath, Name, ct)
                       ?? (await AutomationRepositories.ListAsync(git, ModulesGroupPath, ct)).FirstOrDefault(r => ProjectNames.Key(r.Name) == ProjectNames.Key(Name));
            if (repo is null)
            {
                Errors.Add($"Репозиторий модуля «{Name}» не найден в группе {ModulesGroupPath}.");
                return;
            }
            Git = await _catalog.LoadRepoAsync(git, repo, ct);
            OpenMergeRequests = await git.ListOpenMergeRequestsToAsync(repo.ProjectPath, null, ct) ?? new();
            // В списке MR GitLab не отдаёт head_pipeline. Статус считается так же, как при влитии (MergeRequestFlow).
            foreach (var mr in OpenMergeRequests)
                mr.HeadPipeline = MergeRequestChecks.Summary(MergeRequestChecks.Latest(
                    await git.ListMergeRequestChecksAsync(repo.ProjectPath, mr.Iid, mr.Sha ?? "", ct)));
            if (Git.Dev is not null && Git.HasCi)
            {
                var ci = await git.FindFileAsync(repo.ProjectPath, repo.CiConfigPath ?? ".gitlab-ci.yml", ModuleBranches.Dev, ct);
                CiOutdated = ci is not null && !ModuleCiTemplate.Same(ci.Text, ModuleCiTemplate.Content(AppSettings, ModuleKind));
                if (Git.ProjectFile is { } projPath && await git.FindFileAsync(repo.ProjectPath, projPath, ModuleBranches.Dev, ct) is { } proj)
                {
                    var info = Git.AssemblyInfoFile is { } infoPath ? await git.FindFileAsync(repo.ProjectPath, infoPath, ModuleBranches.Dev, ct) : null;
                    VersionScattered = ModuleSources.SingleVersionProject(proj.Text, info?.Text) != proj.Text ||
                                       info is not null && ModuleSources.SingleVersionAssemblyInfo(info.Text) != info.Text;
                }
            }
            if (AppSettings.RequireDevReview)
                foreach (var mr in MergeRequestsTo(ModuleBranches.Dev))
                    DevReviewers[mr.Iid] = (await git.GetApprovalsAsync(repo.ProjectPath, mr.Iid, ct)).Reviewers(mr);

            if (Git.Main is not null && Git.Dev is not null)
                Unreleased = (await git.CompareCommitsAsync(repo.ProjectPath, ModuleBranches.Main, ModuleBranches.Dev, ct))
                    .Where(c => c.ParentIds is not { Count: > 1 } && !IsService(c.Title))
                    .ToList();
            if (Git.ActiveRelease is not null)
            {
                var inRelease = await git.CompareCommitsAsync(repo.ProjectPath, ModuleBranches.Main, Git.ActiveRelease.Name, ct);
                foreach (var c in inRelease)
                    InRelease.Add(c.Title ?? "");
                ReleaseTasks = inRelease.Where(c => c.ParentIds is not { Count: > 1 } && !IsService(c.Title)).ToList();
            }

            // Схема работ: ветки задач (от dev или релиза) и hotfix (от main) с коммитами сверх базы.
            foreach (var b in Git.TaskBranches.Concat(Git.HotfixBranches))
            {
                var mr = OpenMergeRequests.FirstOrDefault(m => m.SourceBranch == b.Name);
                var target = mr?.TargetBranch ?? (ModuleBranches.IsHotfix(b.Name) ? ModuleBranches.Main : ModuleBranches.Dev);
                var ahead = (await git.CompareCommitsAsync(repo.ProjectPath, target, b.Name, ct)).Where(c => c.ParentIds is not { Count: > 1 }).ToList();
                // Без своих коммитов: влитая (был merged MR) - кандидат на удаление; иначе только что создана - в работе.
                if (ahead.Count > 0 || mr is not null || !await git.IsBranchMergedAsync(repo.ProjectPath, b, ct))
                    Work.Add(new WorkBranch(b, target, ahead, mr));
                else
                    MergedBranches.Add(b);
            }

            // Активные ветки: в каких есть коммиты, которых нет в базе.
            foreach (var w in Work)
                ActiveBranches.Add(new ActiveBranch(w.Branch, ModuleBranches.IsHotfix(w.Branch.Name) ? "hotfix" : "задача", w.Target, w.Commits.Count, w.Mr));
            if (Git.Dev is not null && Unreleased.Count > 0)
                ActiveBranches.Add(new ActiveBranch(Git.Dev, "тест", ModuleBranches.Main, Unreleased.Count, null));
            if (Git.ActiveRelease is not null)
                ActiveBranches.Add(new ActiveBranch(Git.ActiveRelease, "стейдж", ModuleBranches.Main, ReleaseTasks.Count,
                    OpenMergeRequests.FirstOrDefault(m => m.SourceBranch == Git.ActiveRelease.Name)));
            foreach (var b in Git.OtherBranches)
            {
                var ahead = Git.Dev is null ? 0 : (await git.CompareCommitsAsync(repo.ProjectPath, ModuleBranches.Dev, b.Name, ct)).Count(c => c.ParentIds is not { Count: > 1 });
                var mr = OpenMergeRequests.FirstOrDefault(m => m.SourceBranch == b.Name);
                if (ahead > 0 || mr is not null)
                    ActiveBranches.Add(new ActiveBranch(b, "вне схемы", mr?.TargetBranch ?? ModuleBranches.Dev, ahead, mr));
                else
                    IdleOtherBranches.Add(b);
            }
            ActiveBranches.Sort((a, b) => Nullable.Compare(b.Branch.Commit?.CommittedDate, a.Branch.Commit?.CommittedDate));

            // Контур, ветку которого сейчас собирает CI: карточка показывает «выкладывается», а не «ветка впереди».
            foreach (var env in ModuleEnv.All(AppSettings))
                if (Git.BranchFor(env.Key)?.Commit?.Id is { } head &&
                    (await git.ListPipelinesForShaAsync(repo.ProjectPath, head, ct)).Where(p => !p.IsFinished && p.Ref == Git.BranchFor(env.Key)!.Name).MaxBy(p => p.Id) is { } running)
                    Deploying[env.Key] = running;

            ProdReleases = ModuleBranches.ProdReleases(await git.ListVersionTagsAsync(repo.ProjectPath, ct) ?? new()).Take(20).ToList();
            var baseVersion = new[] { LastProdVersion, Git.MainVersion }.Where(v => v is not null).Max();
            SuggestedVersion = baseVersion?.NextMinor() ?? Git.DevVersion ?? new ModuleVersion(1, 0, 0);
            if (Version is null)
                Version = SuggestedVersion.ToString();
        }
        catch (GitLabApiException ex)
        {
            Errors.Add($"GitLab: {ex.Message}");
            return;
        }

        foreach (var env in Envs)
        {
            try
            {
                var list = await _targets.Get(ModuleKind).ListAsync(env.PilotUrl, ModuleLogin(env.Key), AppSettings.ReportsLicenseType, ct);
                var alive = ModuleCatalog.Match(Git, list.Modules);
                EnvStates[env.Key] = ModuleCatalog.State(env, Git, alive, serverKnown: true, _catalog.FindDisabled(ModuleKind, env.Key, Git, alive));
            }
            catch (PilotGatewayException ex)
            {
                Errors.Add($"{env.Title}: {ex.Message}" + (ex.Kind == PilotErrorKind.Authentication ? " Введите учётку на вкладке «Модули»." : ""));
                EnvStates[env.Key] = ModuleCatalog.State(env, Git, null, serverKnown: false);
            }
        }
        NextStep = BuildNextStep();
    }

    private static bool IsService(string? title) => ModuleWorkflowBase.IsServiceCommit(title);
}
