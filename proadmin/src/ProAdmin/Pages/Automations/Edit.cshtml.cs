using ProAdmin.Automations;
using ProAdmin.Automations.Validation;
using ProAdmin.GitLab;
using ProAdmin.Modules;
using ProAdmin.Jobs;
using ProAdmin.Pilot;
using ProAdmin.Settings;
using Microsoft.AspNetCore.Mvc;

namespace ProAdmin.Pages.Automations;

public class EditModel : AppPageModel
{
    /// <summary>Маркер «файла/автоматизации не было» (пустые строки при биндинге превращаются в null).</summary>
    public const string Absent = "absent";

    private readonly PilotGateway _pilot;
    private readonly PublishJobRunner _runner;
    private readonly Pilot.ServerModules.PilotServerModuleGateway _modules;

    public EditModel(PilotGateway pilot, PublishJobRunner runner, Pilot.ServerModules.PilotServerModuleGateway modules)
    {
        _pilot = pilot;
        _runner = runner;
        _modules = modules;
    }

    [BindProperty(SupportsGet = true)] public string Env { get; set; } = "";
    [BindProperty(SupportsGet = true)] public string Name { get; set; } = "";
    [BindProperty] public EditorForm Form { get; set; } = new();

    /// <summary>Ветка задачи (feature/…, bugfix/…) или hotfix/…, открытая в редакторе; null - ветка контура.</summary>
    [BindProperty(SupportsGet = true)] public string? Branch { get; set; }

    public EnvironmentSettings Current { get; private set; } = null!;
    public AutomationRepo? Repo { get; private set; }
    public AutomationSnapshot? PilotVersion { get; private set; }
    public GitLabFile? GitJson { get; private set; }
    public GitLabFile? GitCs { get; private set; }
    public string? PilotError { get; private set; }
    public string? GitError { get; private set; }
    public string InitialSource { get; private set; } = "pilot";
    public List<string> Errors { get; } = new();

    /// <summary>
    /// Правка через ветку: на тесте (dev) - ветка задачи, на проде (main) - hotfix. «Сохранить» коммитит в неё, сервер не меняется;
    /// на сервер изменения попадают со страницы автоматизации: «Влить в тест» / «Hotfix в прод».
    /// </summary>
    public bool WorkMode => Current.Branch is AutomationRepositories.DevBranch or AutomationRepositories.MainBranch;
    public bool IsHotfixMode => Current.Branch == AutomationRepositories.MainBranch;

    /// <summary>Существующие ветки задач (тест) или hotfix (прод) репозитория.</summary>
    public List<string> WorkBranches { get; } = new();

    /// <summary>Ветка, которую показывает редактор: открытая ветка задачи/hotfix или ветка контура.</summary>
    public string ShownBranch => Branch is { Length: > 0 } b && WorkBranches.Contains(b) ? b : Current.Branch;

    private bool IsWorkBranch(string? name) => name is not null &&
        (IsHotfixMode ? ModuleBranches.IsHotfix(name) : ModuleBranches.IsTaskBranch(name));

    /// <summary>Отправлять можно, только если есть репозиторий, контур привязан к ветке и в GitLab есть право на изменение.</summary>
    public bool CanPublish => Current.IsEditable && Repo is not null && GitError is null && !AutomationsReadOnly;

    public IEnumerable<EnvironmentSettings> OtherEnvironments =>
        AppSettings.Environments.Where(e => e.Key != Current.Key && (e.HasServer || e.Branch.Length > 0));

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        if (Prepare() is { } redirect)
            return redirect;

        await LoadAsync(ct);
        await LoadAutomationsAccessAsync(ct);
        if (PilotVersion is null && Repo is null && PilotError is null && GitError is null)
        {
            TempData["FlashError"] = $"Автоматизации «{Name}» нет ни на сервере {Current.Title}, ни в GitLab.";
            return RedirectToPage("/Automations/Index", new { env = Current.Key });
        }

        InitForm();
        return Page();
    }

    /// <summary>Содержимое автоматизации из другого источника: сервер или ветка любого контура.</summary>
    public async Task<IActionResult> OnGetSourceAsync(string from, string? sourceEnv, string? workBranch, CancellationToken ct)
    {
        if (Prepare() is not null)
            return new JsonResult(new { ok = false, message = "Сессия GitLab недоступна - обновите страницу." });

        var env = AppSettings.Find(sourceEnv) ?? Current;
        try
        {
            if (from == "pilot")
            {
                if (!env.HasServer)
                    return new JsonResult(new { ok = false, message = $"Для контура {env.Title} не задан сервер." });
                var a = await _pilot.GetAutomationAsync(env.PilotUrl, Vault.GetPilotLogin(CurrentSession, env.Key), Name, ct);
                return a is null
                    ? new JsonResult(new { ok = false, message = $"На сервере {env.Title} автоматизации «{Name}» нет." })
                    : new JsonResult(new { ok = true, message = $"Загружено с сервера {env.Title}.", triggers = a.Triggers, csharp = a.CSharpCode, paused = a.IsPaused });
            }

            if (env.Branch.Length == 0)
                return new JsonResult(new { ok = false, message = $"Контур {env.Title} не привязан к ветке." });

            var git = CreateGitLabClient();
            var repo = await AutomationRepositories.FindAsync(git, GroupPath!, Name, ct);
            if (repo is null)
                return new JsonResult(new { ok = false, message = $"Репозитория «{Name}» нет в GitLab." });

            if (workBranch is not null && (!WorkMode || workBranch != NewBranch && !IsWorkBranch(workBranch)))
                return new JsonResult(new { ok = false, message = "Недопустимая рабочая ветка." });

            var (source, _, reference) = workBranch is null
                ? (env.Branch, false, env.Branch)
                : await WorkSourceAsync(git, repo, workBranch == NewBranch ? null : workBranch, ct);
            var (json, cs) = await ReadGitAsync(git, repo, reference, ct);
            if (workBranch is not null)
                return new JsonResult(new
                {
                    ok = true, branch = source, triggers = json?.Text, csharp = cs?.Text,
                    version = json is null ? null : AutomationChecks.FirstTriggerVersion(json.Text)?.ToString(),
                });

            return json is null
                ? new JsonResult(new { ok = false, message = $"В ветке {env.Branch} нет {AutomationFiles.JsonPath(Name)}." })
                : new JsonResult(new { ok = true, message = $"Загружено из ветки {env.Branch}.", triggers = json.Text, csharp = cs?.Text ?? "" });
        }
        catch (Exception ex) when (ex is PilotGatewayException or GitLabApiException)
        {
            return new JsonResult(new { ok = false, message = ex.Message });
        }
    }

    /// <summary>Живая проверка сценария по справочнику Pilot и метаданным сервера контура (вызывается при наборе).</summary>
    public async Task<IActionResult> OnPostLintAsync(CancellationToken ct)
    {
        if (Prepare() is not null)
            return new JsonResult(new { issues = Array.Empty<object>() });

        var (issues, metadataNote) = await LintAsync(Form.Triggers ?? "", Form.CSharpCode ?? "", ct);
        return new JsonResult(new
        {
            issues = issues.Select(i => new { level = i.Level, line = i.Line, message = i.Message }),
            note = metadataNote,
        });
    }

    private async Task<(List<LintIssue> Issues, string Note)> LintAsync(string triggers, string csharp, CancellationToken ct)
    {
        MetadataIndex? metadata = null;
        string note;
        var hostedTask = _modules.GetHostedActivitiesAsync(Current.PilotUrl, ModuleLogin(Current.Key), AppSettings.ReportsLicenseType, ct);
        try
        {
            metadata = await _pilot.GetMetadataIndexAsync(Current.PilotUrl, Vault.GetPilotLogin(CurrentSession, Current.Key), ct);
            note = $"Сверено со справкой Pilot и базой сервера {Current.Title}: типов {metadata.Types.Count}, атрибутов {metadata.Attributes.Count}, состояний {metadata.States.Count}.";
        }
        catch (PilotGatewayException ex)
        {
            note = $"Сверено только со справкой Pilot - база сервера {Current.Title} недоступна: {ex.Message}";
        }
        var hosted = await hostedTask;
        if (hosted.Note.Length > 0)
            note += " " + hosted.Note;
        return (AutomationLinter.Lint(triggers, csharp, Name, metadata, hosted), note);
    }

    /// <summary>Кнопка «Проверить»: те же проверки, что выполнит отправка, без коммита.</summary>
    public async Task<IActionResult> OnPostValidateAsync(CancellationToken ct)
    {
        if (Prepare() is not null)
            return new JsonResult(new { items = new[] { CheckMessage.Error("Сессия GitLab недоступна - обновите страницу.") } });

        var items = new List<CheckMessage>();
        var triggers = AutomationFiles.PrepareForStorage(Form.Triggers);
        var cs = AutomationFiles.PrepareForStorage(Form.CSharpCode);

        var task = WorkMode ? ModuleBranches.ParseTask(Form.TaskReference, AppSettings.TaskPattern) : null;
        string? branch = null;
        if (WorkMode)
        {
            branch = WorkBranchName(task, out var branchError);
            if (branchError is not null)
                items.Add(CheckMessage.Error(branchError));
        }
        AutomationChecks.BuildCommitMessage(Current, Name, Form.Description, task ?? Form.TaskReference, out var messageError);
        items.Add(messageError is null ? CheckMessage.Ok("Сообщение коммита заполнено.") : CheckMessage.Error(messageError));

        var (lint, note) = await LintAsync(triggers, cs, ct);
        var lintErrors = lint.Count(i => i.IsError);
        var lintWarnings = lint.Count(i => i.Level == "warn");
        items.Add(lintErrors > 0
            ? CheckMessage.Error($"Сценарий: ошибок {lintErrors}, предупреждений {lintWarnings}.", note)
            : lintWarnings > 0
                ? CheckMessage.Warn($"Сценарий: ошибок нет, предупреждений {lintWarnings}.{(AppSettings.BlockPublishOnLintWarnings ? " Отправка при предупреждениях запрещена настройками." : "")}")
                : CheckMessage.Ok("Сценарий: ошибок и предупреждений нет. " + note));
        items.AddRange(lint.Select(i => new CheckMessage(i.Level, $"Строка {i.Line}: {i.Message}")));

        try
        {
            var git = CreateGitLabClient();
            var repo = await AutomationRepositories.FindAsync(git, GroupPath!, Name, ct);
            if (repo is null)
            {
                items.Add(CheckMessage.Error($"Репозитория «{Name}» нет в GitLab - сначала создайте его."));
            }
            else
            {
                var (source, _, reference) = await WorkSourceAsync(git, repo, branch, ct);
                var (gitJson, gitCs) = await ReadGitAsync(git, repo, reference, ct);
                var stored = AutomationFiles.PrepareForStorage(AutomationVersion.ToStorageLayout(triggers));
                var csChanged = WorkMode ? CSharpChanged(gitCs, cs) : !AutomationFiles.SameContent(gitCs?.Text, cs);
                var changed = (WorkMode && gitJson is null) || !AutomationFiles.SameContent(gitJson?.Text, stored) || csChanged;
                if (AppSettings.RequireVersion)
                    items.Add(AutomationChecks.CheckVersion(stored, gitJson?.Text, source, csChanged));

                items.Add(CheckMessage.Info(changed
                    ? $"Есть изменения относительно ветки {source} - будет {(WorkMode ? "коммит в рабочую ветку" : "коммит, MR и pipeline")}."
                    : $"Содержимое совпадает с веткой {source} - коммит не нужен{(WorkMode ? "." : ", запись сразу на сервер.")}"));
            }
        }
        catch (GitLabApiException ex)
        {
            items.Add(CheckMessage.Warn($"GitLab: {ex.Message}"));
        }

        if (cs.Length > 0)
        {
            try
            {
                var result = await _pilot.ValidateCSharpAsync(Current.PilotUrl, Vault.GetPilotLogin(CurrentSession, Current.Key), cs, ct);
                items.Add(result.IsSuccess
                    ? CheckMessage.Ok("C#-код компилируется на сервере Pilot.")
                    : CheckMessage.Error("C#-код не компилируется.",
                        string.Join("\n", (result.Diagnostics ?? new()).Select(d => $"[{d.Severity}] {d.Location}: {d.Message}"))));
            }
            catch (PilotGatewayException ex)
            {
                items.Add(CheckMessage.Warn($"Проверка C# недоступна: {ex.Message}"));
            }
        }

        return new JsonResult(new { items });
    }

    public Task<IActionResult> OnPostPublishAsync(CancellationToken ct) => StartAsync(delete: false, ct);

    /// <summary>
    /// «Сохранить в ветку»: коммит JSON и C# в ветку задачи (от dev) или hotfix (от main). Сервер Pilot не меняется.
    /// Новая ветка называется номером задачи и/или кратким описанием, как у модулей.
    /// </summary>
    public async Task<IActionResult> OnPostSaveBranchAsync(CancellationToken ct)
    {
        if (Prepare() is { } redirect)
            return redirect;
        await LoadAsync(ct);
        if (!WorkMode)
            Errors.Add($"Контур {Current.Title} не привязан к dev или main - сохранение в ветку недоступно.");
        if (GitLabGroupAccess.CannotChange(await LoadAutomationsAccessAsync(ct)))
            Errors.Add(ChangeDeniedText("автоматизации", AutomationsAccess, GroupPath));
        if (Repo is null)
            Errors.Add(GitError is null ? $"Репозитория «{Name}» нет в GitLab - сначала создайте его." : $"GitLab недоступен: {GitError}");

        var task = ModuleBranches.ParseTask(Form.TaskReference, AppSettings.TaskPattern);
        var branch = WorkBranchName(task, out var branchError);
        if (branchError is not null)
            Errors.Add(branchError);

        var message = AutomationChecks.BuildCommitMessage(Current, Name, Form.Description, task ?? Form.TaskReference, out var messageError);
        if (messageError is not null)
            Errors.Add(messageError);
        if (Errors.Count > 0 || branch is null)
            return Page();

        var triggers = AutomationFiles.PrepareForStorage(AutomationVersion.ToStorageLayout(Form.Triggers ?? ""));
        var cs = AutomationFiles.PrepareForStorage(Form.CSharpCode);
        var git = CreateGitLabClient();
        try
        {
            var repo = Repo!;
            var (source, exists, reference) = await WorkSourceAsync(git, repo, branch, ct);
            var (json, csFile) = await ReadGitAsync(git, repo, reference, ct);
            var csChanged = CSharpChanged(csFile, cs);
            if (AppSettings.RequireVersion &&
                AutomationChecks.CheckVersion(triggers, json?.Text, source, csChanged) is { Level: "error" } version)
            {
                Errors.Add(version.Text);
                return Page();
            }

            var jsonPath = AutomationFiles.JsonPath(Name);
            var csPath = AutomationFiles.CsPath(Name);
            var actions = new List<GitLabCommitAction>();
            if (json is null) actions.Add(GitLabCommitAction.Create(jsonPath, triggers));
            else if (!AutomationFiles.SameContent(json.Text, triggers))
                actions.Add(GitLabCommitAction.Update(jsonPath, triggers, exists ? json.LastCommitId : null));
            if (csChanged)
                actions.Add(csFile is null ? GitLabCommitAction.Create(csPath, cs)
                    : cs.Length == 0 ? GitLabCommitAction.Delete(csPath, exists ? csFile.LastCommitId : null)
                    : GitLabCommitAction.Update(csPath, cs, exists ? csFile.LastCommitId : null));

            if (actions.Count == 0)
            {
                if (!exists)
                    await git.CreateBranchAsync(repo.ProjectPath, branch, source, ct);
                TempData["Flash"] = $"В ветке {branch} уже это содержимое - коммит не нужен.";
            }
            else
            {
                var commit = await git.CreateCommitAsync(repo.ProjectPath, branch, exists ? null : source, message, actions, ct);
                TempData["Flash"] = $"Сохранено в ветку {branch}{(exists ? "" : $" (создана от {Current.Branch})")}: коммит {commit.ShortId}. " +
                                    (IsHotfixMode ? "На прод - «Hotfix в прод» на странице автоматизации." : "На тест - «Влить в тест» на странице автоматизации.");
            }
        }
        catch (GitLabApiException ex)
        {
            Errors.Add($"GitLab: {ex.Message}");
            return Page();
        }
        return RedirectToPage(new { env = Current.Key, name = Name, branch });
    }

    /// <summary>Значение «новая ветка» в выборе ветки.</summary>
    public const string NewBranch = "+new";

    private string? WorkBranchName(string? task, out string? error)
    {
        error = null;
        if (Form.WorkBranch is { Length: > 0 } chosen && chosen != NewBranch)
        {
            if (IsWorkBranch(chosen))
                return chosen;
            error = $"Ветка {chosen} - не {(IsHotfixMode ? "hotfix/…" : "ветка задачи feature/… или bugfix/…")}.";
        }
        else if (task is not null || !string.IsNullOrWhiteSpace(Form.BranchSlug))
            return ModuleBranches.TaskBranch(IsHotfixMode ? ModuleBranches.HotfixPrefix : ModuleBranches.FeaturePrefix, task, Form.BranchSlug);
        else
            error = "Укажите номер задачи или кратко, что делается, - из этого получится имя ветки.";
        return null;
    }

    private async Task<(string Branch, bool Exists, string Reference)> WorkSourceAsync(GitLabClient git, AutomationRepo repo, string? branch, CancellationToken ct) =>
        branch is not null && await git.FindBranchAsync(repo.ProjectPath, branch, ct) is { } head
            ? (branch, true, head.Commit?.Id ?? branch)
            : (Current.Branch, false, Current.Branch);

    private async Task<(GitLabFile? Json, GitLabFile? Cs)> ReadGitAsync(GitLabClient git, AutomationRepo repo, string branch, CancellationToken ct) =>
        (await git.FindFileAsync(repo.ProjectPath, AutomationFiles.JsonPath(Name), branch, ct),
         await git.FindFileAsync(repo.ProjectPath, AutomationFiles.CsPath(Name), branch, ct));

    private static bool CSharpChanged(GitLabFile? file, string cs) =>
        cs.Length == 0 ? file is not null : !AutomationFiles.SameContent(file?.Text, cs);

    public Task<IActionResult> OnPostDeleteAsync(CancellationToken ct) => StartAsync(delete: true, ct);

    private async Task<IActionResult> StartAsync(bool delete, CancellationToken ct)
    {
        if (Prepare() is { } redirect)
            return redirect;

        await LoadAsync(ct);
        if (!Current.IsEditable)
            Errors.Add($"Контур {Current.Title} не привязан к ветке - отправка недоступна.");
        if (GitLabGroupAccess.CannotChange(await LoadAutomationsAccessAsync(ct)))
            Errors.Add(ChangeDeniedText("автоматизации", AutomationsAccess, GroupPath));
        if (Repo is null && GitError is null)
            Errors.Add($"Репозитория «{Name}» нет в GitLab - сначала создайте его.");
        if (GitError is not null)
            Errors.Add($"GitLab недоступен: {GitError}");

        var message = AutomationChecks.BuildCommitMessage(Current, Name, Form.Description, Form.TaskReference, out var messageError);
        if (messageError is not null)
            Errors.Add(messageError);

        if (Errors.Count == 0)
        {
            var request = new PublishRequest
            {
                Env = Current.Key,
                Name = Name,
                Delete = delete,
                Triggers = Form.Triggers ?? "",
                CSharpCode = Form.CSharpCode ?? "",
                IsPaused = Form.IsPaused,
                CommitMessage = message,
                ChangeNotes = Changelog.SplitNotes(Form.Description),
                TaskReference = Form.TaskReference,
                BasePilotHash = FromBase(Form.BasePilotHash),
                BaseGitJsonBlob = FromBase(Form.BaseGitJsonBlob),
                BaseGitCsBlob = FromBase(Form.BaseGitCsBlob),
            };
            var context = new PublishContext
            {
                Pilot = Vault.GetPilotLogin(CurrentSession, Current.Key),
                ModulesLogin = ModuleLogin(Current.Key),
                GitLabToken = CurrentSession.GitLabToken!,
                Settings = AppSettings,
                ProjectPath = Repo!.ProjectPath,
                CiConfigPath = Repo.CiConfigPath,
            };

            try
            {
                var job = _runner.Start(request, context, CurrentSession.PilotLogin, CurrentSession.GitLabUser?.Username);
                return RedirectToPage("/Jobs/Details", new { id = job.Id });
            }
            catch (InvalidOperationException ex)
            {
                Errors.Add(ex.Message);
            }
        }

        // Ошибка - показываем редактор с введённым текстом (Form не перезаписываем).
        return Page();
    }

    private IActionResult? Prepare()
    {
        ViewData["Section"] = "automations";
        if (!GitLabConnected)
            return RedirectToGitLab();

        var env = AppSettings.Find(Env);
        if (env is null || !env.HasServer || string.IsNullOrWhiteSpace(Name))
            return RedirectToPage("/Automations/Index", new { env = Env });

        Current = env;
        Name = Name.Trim();
        return null;
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        var login = Vault.GetPilotLogin(CurrentSession, Current.Key);
        var pilotTask = Task.Run(async () =>
        {
            try
            {
                PilotVersion = await _pilot.GetAutomationAsync(Current.PilotUrl, login, Name, ct);
            }
            catch (PilotGatewayException ex)
            {
                PilotError = ex.Message;
            }
        }, ct);

        try
        {
            var git = CreateGitLabClient();
            Repo = await AutomationRepositories.FindAsync(git, GroupPath!, Name, ct);
            if (Repo is not null && WorkMode)
                WorkBranches.AddRange((await git.ListBranchesAsync(Repo.ProjectPath, null, ct) ?? new())
                    .Where(b => IsWorkBranch(b.Name))
                    .OrderByDescending(b => b.Commit?.CommittedDate)
                    .Select(b => b.Name));
            if (Repo is not null && Current.Branch.Length > 0)
            {
                GitJson = await git.FindFileAsync(Repo.ProjectPath, AutomationFiles.JsonPath(Name), ShownBranch, ct);
                GitCs = await git.FindFileAsync(Repo.ProjectPath, AutomationFiles.CsPath(Name), ShownBranch, ct);
            }
        }
        catch (GitLabApiException ex)
        {
            GitError = ex.Message;
        }

        await pilotTask;
    }

    private void InitForm()
    {
        Form.WorkBranch = ShownBranch != Current.Branch ? ShownBranch : NewBranch;
        if (ShownBranch != Current.Branch && GitJson is not null)
        {
            // Открыта ветка задачи/hotfix - в редакторе её содержимое.
            Form.Triggers = GitJson.Text;
            Form.CSharpCode = GitCs?.Text ?? "";
            Form.IsPaused = PilotVersion?.IsPaused ?? false;
            InitialSource = "git";
        }
        else if (PilotVersion is not null)
        {
            Form.Triggers = PilotVersion.Triggers;
            Form.CSharpCode = PilotVersion.CSharpCode;
            Form.IsPaused = PilotVersion.IsPaused;
            InitialSource = "pilot";
        }
        else if (GitJson is not null)
        {
            Form.Triggers = GitJson.Text;
            Form.CSharpCode = GitCs?.Text ?? "";
            InitialSource = "git";
        }

        Form.BasePilotHash = PilotError is null ? PilotVersion?.Hash ?? Absent : null;
        Form.BaseGitJsonBlob = GitError is null ? GitJson?.BlobId ?? Absent : null;
        Form.BaseGitCsBlob = GitError is null ? GitCs?.BlobId ?? Absent : null;
    }

    private static string? FromBase(string? value) => value == Absent ? "" : value;

    public sealed class EditorForm
    {
        public string Triggers { get; set; } = "";
        public string CSharpCode { get; set; } = "";
        public bool IsPaused { get; set; }
        public string? Description { get; set; }
        public string? TaskReference { get; set; }
        public string? BasePilotHash { get; set; }
        public string? BaseGitJsonBlob { get; set; }
        public string? BaseGitCsBlob { get; set; }

        /// <summary>Куда сохранить: существующая ветка или <see cref="NewBranch"/>.</summary>
        public string? WorkBranch { get; set; }

        /// <summary>Кратко для имени новой ветки (feature/KTN-1234-кратко).</summary>
        public string? BranchSlug { get; set; }
    }
}
