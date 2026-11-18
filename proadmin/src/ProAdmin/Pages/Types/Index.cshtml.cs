using ProAdmin.GitLab;
using ProAdmin.Jobs;
using ProAdmin.Modules;
using ProAdmin.Pilot;
using ProAdmin.Types;
using Microsoft.AspNetCore.Mvc;

namespace ProAdmin.Pages.Types;

/// <summary>Состояние контура: ветка, что на сервере, совпадает ли.</summary>
public sealed class TypesEnvCard
{
    public required TypesEnv Env { get; init; }
    public GitLabBranch? Branch { get; set; }
    public TypesDeployment? Deployment { get; set; }
    public TypesPlan? Plan { get; set; }
    public string? Error { get; set; }

    /// <summary>Сервер совпадает с веткой (или сравнить не удалось / сервера нет - тогда не шумим).</summary>
    public bool Ok => Plan is null || Plan is { CanApply: true, HasChanges: false };

    public string Caption => !Env.HasServer ? "сервер не задан"
        : Error is not null ? "не удалось сравнить"
        : Plan is null ? ""
        : !Plan.CanApply ? $"ветку нельзя записать: {Plan.Errors.Count}"
        : Plan.HasChanges ? $"сервер отличается: {Plan.Changes.Count}"
        : "совпадает";
}

/// <summary>
/// Типы как код, тот же путь, что у клиентских модулей: ветка изменения types/… / dev (тест) / release (стейдж) / main (прод).
/// Изменения в dev связываются с коммитами модулей и автоматизаций и уходят на стейдж и прод вместе с ними.
/// </summary>
public class IndexModel : AppPageModel
{
    private readonly PilotGateway _pilot;
    private readonly TypesStore _store;
    private readonly TypesJobRunner _runner;

    public IndexModel(PilotGateway pilot, TypesStore store, TypesJobRunner runner)
    {
        _pilot = pilot;
        _store = store;
        _runner = runner;
    }

    /// <summary>overview - изменения и серверы; types / states / machines - элементы (из dev или ветки изменения).</summary>
    [BindProperty(SupportsGet = true)] public string View { get; set; } = "overview";

    /// <summary>Ветка изменения, содержимое которой показывается и куда сохраняет редактор.</summary>
    [BindProperty(SupportsGet = true)] public string? Branch { get; set; }

    [BindProperty] public string? Message { get; set; }
    [BindProperty] public string? NewChange { get; set; }
    [BindProperty] public string? SourceEnv { get; set; }
    [BindProperty] public string? Confirm { get; set; }
    [BindProperty] public Guid? LinkId { get; set; }

    public string? ProjectPath => AppSettings.TypesProjectPath;
    public GitLabProject? Project { get; private set; }
    public bool Initialized { get; private set; }
    public string? MainVersion { get; private set; }
    public List<TypesEnvCard> Cards { get; } = new();
    public List<TypesChangeEntry> Changes { get; private set; } = new();
    public List<GitLabBranch> TaskBranches { get; private set; } = new();
    public TypesSnapshot? Snapshot { get; private set; }
    public string? SnapshotRef { get; private set; }
    public FlowCard? Flow { get; private set; }
    public List<string> Errors { get; } = new();
    public GitLabGroupAccess? Access { get; private set; }
    public bool ReadOnly => GitLabGroupAccess.CannotChange(Access);

    public IEnumerable<TypesChangeEntry> Active => Changes.Where(c => c.Stage != TypesChangeStage.Prod);
    public IEnumerable<TypesChangeEntry> Released => Changes.Where(c => c.Stage == TypesChangeStage.Prod).Take(15);

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        if (GitHubTypesMode)
            return RedirectToPage("/Types/GitHub");
        ViewData["Section"] = "types";
        if (!GitLabConnected)
            return RedirectToGitLab();
        View = View is "types" or "states" or "machines" ? View : "overview";
        await LoadAsync(ct, full: true);
        return Page();
    }

    // ------------------------------------------------------------------ операции

    public Task<IActionResult> OnPostInitAsync(CancellationToken ct) => StartAsync(ct, ctx =>
    {
        var env = TypesEnv.Find(AppSettings, SourceEnv) is { HasServer: true } e ? e.Key : throw new InvalidOperationException("Выберите сервер-источник.");
        return _runner.StartInit(ctx, env, CurrentSession.PilotLogin, CurrentSession.GitLabUser?.Username);
    });

    /// <summary>Ветка изменения / squash в dev / тест.</summary>
    public Task<IActionResult> OnPostMergeBranchAsync(CancellationToken ct) => StartAsync(ct, ctx =>
    {
        var change = Changes.FirstOrDefault(c => c.Branch?.Name == Branch) ?? throw new InvalidOperationException($"Ветка изменения {Branch} не найдена.");
        if (change.Ahead == 0)
            throw new InvalidOperationException($"В ветке {Branch} нет правок относительно dev - вливать нечего.");
        var text = (Message ?? "").Trim();
        if (text.Length == 0)
            throw new InvalidOperationException("Опишите изменение - это сообщение коммита в dev и пункт будущего CHANGELOG.");
        var task = ModuleBranches.Task(Branch, AppSettings.TaskPattern) ?? ModuleBranches.Task(text, AppSettings.TaskPattern);
        if (task is not null && !text.Contains(task, StringComparison.OrdinalIgnoreCase))
            text = $"{task}: {text}";
        return _runner.StartMergeBranch(ctx, Branch!, text, CurrentSession.PilotLogin, CurrentSession.GitLabUser?.Username);
    });

    public Task<IActionResult> OnPostSyncAsync(CancellationToken ct) => StartAsync(ct, ctx =>
    {
        var branch = Branch is TypesBranches.Dev or TypesBranches.Release or TypesBranches.Main ? Branch : throw new InvalidOperationException("Неизвестная ветка.");
        if (branch == TypesBranches.Main && Confirm != "прод")
            throw new InvalidOperationException("Запись main на прод: введите «прод» для подтверждения.");
        return _runner.StartSync(ctx, branch, CurrentSession.PilotLogin, CurrentSession.GitLabUser?.Username);
    });

    /// <summary>Новое изменение: ветка types/… от dev, дальше правки в редакторе.</summary>
    public Task<IActionResult> OnPostCreateBranchAsync(CancellationToken ct) => ChangeAsync(ct, async git =>
    {
        var title = (NewChange ?? "").Trim();
        if (title.Length == 0)
            return Fail("Назовите изменение: номер задачи и/или короткое описание.");
        var task = ModuleBranches.ParseTask(title, AppSettings.TaskPattern);
        var branch = TypesBranches.Task(task, task is null ? title : title.Replace(task, "", StringComparison.OrdinalIgnoreCase));
        if (branch == TypesBranches.TaskPrefix || TaskBranches.Any(b => b.Name == branch))
            branch += (branch.EndsWith('/') ? "" : "-") + DateTime.Now.ToString("MMdd-HHmm");
        await git.CreateBranchAsync(ProjectPath!, branch, TypesBranches.Dev, ct);
        TempData["Flash"] = $"Создана ветка {branch} от dev. Откройте тип, состояние или группу состояний и сохраните правку - она попадёт в эту ветку.";
        return RedirectToPage(new { view = "types", branch });
    });

    public Task<IActionResult> OnPostDeleteBranchAsync(CancellationToken ct) => ChangeAsync(ct, async git =>
    {
        if (!TypesBranches.IsTask(Branch))
            return Fail("Удалить можно только ветку изменения types/….");
        foreach (var mr in await git.ListOpenMergeRequestsAsync(ProjectPath!, Branch!, TypesBranches.Dev, ct))
            await git.CloseMergeRequestAsync(ProjectPath!, mr.Iid, ct);
        await git.DeleteBranchAsync(ProjectPath!, Branch!, ct);
        foreach (var l in _store.ChangeLinks().Where(l => l.IsChange(null, Branch)))
            _store.RemoveLink(l.Id);
        TempData["Flash"] = $"Ветка {Branch} удалена вместе с её связями.";
        return RedirectToPage(null, null, "changes");
    });

    public Task<IActionResult> OnPostUnlinkAsync(CancellationToken ct) => ChangeAsync(ct, _ =>
    {
        TempData["Flash"] = LinkId is { } id && _store.RemoveLink(id) ? "Связь удалена." : "Связь не найдена.";
        return Task.FromResult<IActionResult>(RedirectToPage(null, null, "changes"));
    });

    /// <summary>Отличия тестового сервера от dev / новая ветка изменения (если типы правили на сервере напрямую).</summary>
    public Task<IActionResult> OnPostCaptureAsync(CancellationToken ct) => ChangeAsync(ct, async git =>
    {
        var env = TypesEnv.Find(AppSettings, TypesEnv.Test);
        if (env is not { HasServer: true })
            return Fail("Тестовый сервер не задан.");
        var dev = await TypesRepository.LoadAsync(git, ProjectPath!, TypesBranches.Dev, ct);
        var metadata = await _pilot.GetMetadataAsync(env.PilotUrl, Vault.GetPilotLogin(CurrentSession, env.Key), ct);
        var actions = TypesRepository.DiffActions(dev, TypesExport.FromMetadata(metadata), includeDeletes: false);
        if (actions.Count == 0)
        {
            TempData["Flash"] = $"Сервер {env.Title} совпадает с dev - записывать нечего.";
            return RedirectToPage();
        }
        var branch = TypesBranches.Task(null, $"test-server-{DateTime.Now:MMdd-HHmm}");
        var commit = await TypesRepository.CommitToDraftAsync(git, ProjectPath!, branch, $"Снимок сервера {env.Title}: {actions.Count} файл(ов)", actions, ct);
        TempData["Flash"] = $"Отличия сервера {env.Title} записаны в ветку {branch} (коммит {commit.ShortId}, файлов: {actions.Count}). Проверьте и влейте в dev.";
        return RedirectToPage(null, null, "changes");
    });

    // ------------------------------------------------------------------

    private async Task<IActionResult> StartAsync(CancellationToken ct, Func<TypesContext, PublishJob> start)
    {
        ViewData["Section"] = "types";
        if (!GitLabConnected)
            return RedirectToGitLab();
        await LoadAsync(ct, full: false);
        if (Denied() is { } denied)
            return denied;
        if (ProjectPath is null)
            return Fail("В настройках не указан репозиторий типов.");
        try
        {
            var job = start(TypesContexts.Create(this));
            return RedirectToPage("/Jobs/Details", new { id = job.Id });
        }
        catch (InvalidOperationException ex)
        {
            return Fail(ex.Message);
        }
    }

    private async Task<IActionResult> ChangeAsync(CancellationToken ct, Func<GitLabClient, Task<IActionResult>> change)
    {
        ViewData["Section"] = "types";
        if (!GitLabConnected)
            return RedirectToGitLab();
        await LoadAsync(ct, full: false);
        if (Denied() is { } denied)
            return denied;
        if (ProjectPath is null || !Initialized)
            return Fail("Репозиторий типов не настроен или не инициализирован.");
        try
        {
            return await change(CreateGitLabClient());
        }
        catch (Exception ex) when (ex is GitLabApiException or PilotGatewayException or TypesFormatException)
        {
            return Fail(ex.Message);
        }
    }

    private IActionResult? Denied()
    {
        if (!GitLabGroupAccess.CannotChange(Access))
            return null;
        TempData["FlashError"] = ChangeDeniedText("типы", Access, Access?.GroupPath);
        return RedirectToPage();
    }

    private IActionResult Fail(string message)
    {
        TempData["FlashError"] = message;
        return RedirectToPage();
    }

    private async Task LoadAsync(CancellationToken ct, bool full)
    {
        if (ProjectPath is null)
            return;
        Access = await GetGroupAccessAsync(TypesRepository.GroupOf(ProjectPath), fresh: false, ct);
        var git = CreateGitLabClient();
        try
        {
            Project = await git.FindProjectAsync(ProjectPath, ct);
            if (Project is null)
            {
                Errors.Add($"Репозиторий {ProjectPath} не найден в GitLab (или нет доступа). Создайте пустой проект.");
                return;
            }

            var branches = new Dictionary<string, GitLabBranch?>();
            foreach (var b in new[] { TypesBranches.Dev, TypesBranches.Release, TypesBranches.Main })
                branches[b] = await git.FindBranchAsync(ProjectPath, b, ct);
            Initialized = branches.Values.All(b => b is not null) &&
                          await git.FindFileAsync(ProjectPath, TypesSnapshot.ConfigFile, TypesBranches.Main, ct) is { } config &&
                          (MainVersion = TypesJson.Read<TypesConfigInfo>(config.Text, TypesSnapshot.ConfigFile).Version) is not null;
            if (!Initialized)
                return;

            Changes = await TypesChanges.LoadAsync(git, ProjectPath, _store, withItems: full && View == "overview", ct);
            TaskBranches = Changes.Where(c => c.Branch is not null).Select(c => c.Branch!).ToList();
            if (Branch is not null && TaskBranches.All(b => b.Name != Branch) && HttpMethods.IsGet(Request.Method))
                Branch = null;
            if (!full)
                return;

            if (View != "overview")
            {
                SnapshotRef = Branch ?? TypesBranches.Dev;
                Snapshot = await TypesRepository.LoadAsync(git, ProjectPath, SnapshotRef, ct);
                return;
            }
            foreach (var env in TypesEnv.All(AppSettings))
                Cards.Add(new TypesEnvCard { Env = env, Branch = branches[env.Branch], Deployment = _store.Deployment(env.Key) });
            await Task.WhenAll(Cards.Select(card => PlanAsync(git, card, ct)));
            Flow = BuildFlow();
        }
        catch (Exception ex) when (ex is GitLabApiException or TypesFormatException)
        {
            Errors.Add(ex.Message);
        }
    }

    private async Task PlanAsync(GitLabClient git, TypesEnvCard card, CancellationToken ct)
    {
        if (!card.Env.HasServer || card.Branch?.Commit is null)
            return;
        try
        {
            var snapshot = await TypesRepository.LoadAsync(git, ProjectPath!, card.Branch.Commit.Id, ct);
            card.Plan = await _pilot.PlanTypesAsync(card.Env.PilotUrl, Vault.GetPilotLogin(CurrentSession, card.Env.Key), snapshot, ct);
        }
        catch (Exception ex) when (ex is GitLabApiException or PilotGatewayException or TypesFormatException)
        {
            card.Error = ex.Message;
        }
    }

    /// <summary>Этапы и следующий шаг: правка / тест / стейдж / прод.</summary>
    private FlowCard BuildFlow()
    {
        var card = Cards.ToDictionary(c => c.Env.Key);
        var test = card[TypesEnv.Test];
        var stage = card[TypesEnv.Stage];
        var prod = card[TypesEnv.Prod];
        var working = Changes.Where(c => c.Stage == TypesChangeStage.Branch).ToList();
        var onTest = Changes.Where(c => c.Stage == TypesChangeStage.Test).ToList();
        var onStage = Changes.Where(c => c.Stage == TypesChangeStage.Stage).ToList();
        static bool AllLinked(List<TypesChangeEntry> list) => list.All(c => c.Links.Any(l => !l.Done));

        // Линия на каждое изменение: где оно сейчас и с каким модулем едет.
        var lanes = Active.Select(c => new FlowLane(c.Title, c.Branch?.Name ?? c.Commit!.ShortId,
            c.Branch is { } b ? $"{Project?.WebUrl}/-/commits/{Uri.EscapeDataString(b.Name)}" : c.Commit!.WebUrl, (int)c.Stage,
            c.Links.FirstOrDefault(x => !x.Done) is { } link ? $"едет с {link.TargetName} {link.TargetRef}" : c.Mr is { } m ? $"MR !{m.Iid}" : null)).ToList();
        var steps = new List<FlowStep>
        {
            new("Правка", "types/… / dev", working.Count == 0 ? "веток изменений нет" : $"в работе: {working.Count}"),
            new("Тест", TypesBranches.Dev, onTest.Count > 0 ? $"ждут стейджа: {onTest.Count} · {test.Caption}" : test.Caption),
            new("Стейдж", TypesBranches.Release, onTest.Count + onStage.Count > 0
                ? $"{FlowLanes.Reached(lanes, 2, 1, "изменения", "изменений", "изменений")} · {stage.Caption}" : stage.Caption),
            new("Прод", TypesBranches.Main, $"v{MainVersion} · {prod.Caption}"),
        };

        ModuleNextStep next;
        if (!test.Ok)
            next = new(FlowStage.Test, NextStepKind.Warn, "Тестовый сервер расходится с dev",
                "Запишите dev на сервер или, если типы правили прямо на тесте, заберите отличия в ветку изменения.", "К серверам ", "#servers");
        else if (working.FirstOrDefault(c => c.Ahead > 0) is { } ready)
            next = new(FlowStage.Task, NextStepKind.Action, $"Ветка {ready.Branch!.Name}: влейте в dev",
                "«Влить / тест» создаст MR со squash в dev, дождётся pipeline и запишет dev на тестовый сервер. После этого изменение можно связать с модулем.",
                "К изменениям ", "#changes");
        else if (onTest.Count > 0 && !AllLinked(onTest))
            next = new(FlowStage.Test, NextStepKind.Action, $"В dev {ModuleNextSteps.Plural(onTest.Count, "изменение ждёт", "изменения ждут", "изменений ждут")} стейджа",
                "Свяжите изменение с коммитом модуля или автоматизации - оно уйдёт на стейдж и прод вместе с ним. Или перенесите вручную.",
                "На стейдж /", "/Types/Promote?target=release");
        else if (!stage.Ok)
            next = new(FlowStage.Stage, NextStepKind.Warn, "Сервер стейджа расходится с release", "Запишите release на сервер стейджа.", "К серверам ", "#servers");
        else if (onStage.Count > 0 && !AllLinked(onStage))
            next = new(FlowStage.Stage, NextStepKind.Action, $"На стейдже {ModuleNextSteps.Plural(onStage.Count, "изменение ждёт", "изменения ждут", "изменений ждут")} прода",
                "Проверьте на стейдже и выпустите: перенос release / main с версией, CHANGELOG и тегом.", "На прод /", "/Types/Promote?target=main");
        else if (onTest.Count + onStage.Count > 0)
            next = new(onStage.Count > 0 ? FlowStage.Stage : FlowStage.Test, NextStepKind.Wait,
                $"Изменения едут вместе с модулями: {onTest.Count + onStage.Count}",
                "Они перейдут на стейдж и прод, когда туда выйдут связанные коммиты модулей и автоматизаций.", "К изменениям ", "#changes");
        else if (!prod.Ok)
            next = new(FlowStage.Prod, NextStepKind.Warn, "Продовый сервер расходится с main", "Запишите main на прод или выясните, кто менял типы напрямую.", "К серверам ", "#servers");
        else if (working.Count > 0)
            next = new(FlowStage.Task, NextStepKind.Action, "Ветки изменений без правок",
                "Откройте тип, состояние или группу состояний и сохраните правку в ветку - или удалите ветку.", "К изменениям ", "#changes");
        else
            next = new(FlowStage.Done, NextStepKind.Done, $"Всё выпущено: v{MainVersion} на проде",
                "Изменений вне main нет, серверы совпадают с ветками. Новое изменение начинается с ветки.", "+ Новое изменение", "#new-change");

        var current = next.Stage switch
        {
            FlowStage.Task => 0,
            FlowStage.Test => 1,
            FlowStage.Stage => 2,
            FlowStage.Prod => 3,
            _ => 4,
        };
        return new FlowCard(steps, current, next, Array.Empty<ActiveBranch>(), Project?.WebUrl ?? "", lanes);
    }

    public string? TaskUrl(string? text) =>
        ModuleBranches.TaskUrl(ModuleBranches.Task(text, AppSettings.TaskPattern), AppSettings.TaskUrlTemplate);
}

/// <summary>Контекст операций над типами из сессии страницы.</summary>
internal static class TypesContexts
{
    public static TypesContext Create(AppPageModel page)
    {
        var vault = page.HttpContext.RequestServices.GetRequiredService<Sessions.SessionVault>();
        var settings = page.AppSettings;
        return new TypesContext
        {
            Settings = settings,
            GitLabToken = page.CurrentSession.GitLabToken!,
            ProjectPath = settings.TypesProjectPath!,
            ProjectWebUrl = settings.TypesGitLabProjectUrl,
            Logins = TypesEnv.All(settings).Where(e => e.HasServer).ToDictionary(e => e.Key, e => vault.GetPilotLogin(page.CurrentSession, e.Key)),
        };
    }

    /// <summary>Для операций модулей и автоматизаций: выпуск связанных изменений типов; null - репозиторий типов не настроен.</summary>
    public static TypesFollowDeps? Follow(AppPageModel page) =>
        page.AppSettings.TypesProjectPath is null
            ? null
            : new TypesFollowDeps
            {
                Ctx = Create(page),
                Store = page.HttpContext.RequestServices.GetRequiredService<TypesStore>(),
                Pilot = page.HttpContext.RequestServices.GetRequiredService<PilotGateway>(),
            };
}
