using ProAdmin.Automations;
using ProAdmin.GitLab;
using ProAdmin.Jobs;
using ProAdmin.Modules;
using Microsoft.AspNetCore.Mvc;

namespace ProAdmin.Pages.Modules;

/// <summary>
/// Новый модуль: репозиторий в группе модулей GitLab с готовым каркасом (проект, точка входа с примером, CHANGELOG, README),
/// ветки main и dev с шаблоном CI, защита веток. ?kind=server - серверный модуль для ServerHost.
/// </summary>
public class CreateModel : AppPageModel
{
    private readonly ModuleCatalog _catalog;
    private readonly JobStore _jobs;

    public CreateModel(ModuleCatalog catalog, JobStore jobs)
    {
        _catalog = catalog;
        _jobs = jobs;
    }

    [BindProperty(SupportsGet = true)] public string? Kind { get; set; }
    public ModuleKind ModuleKind => ModuleKinds.Parse(Kind);
    public bool IsServer => ModuleKind == ModuleKind.Server;
    public override string? ModulesGroupPath => ModuleKind.GroupPath(AppSettings);
    protected override object? ModulesListRoute => new { kind = ModuleKind.Query() };

    [BindProperty] public string Code { get; set; } = "";
    [BindProperty] public string Title { get; set; } = "";
    [BindProperty] public string? Description { get; set; }
    [BindProperty] public string? Number { get; set; }
    [BindProperty] public string? Section { get; set; }
    [BindProperty] public bool UseClientSdk { get; set; } = true;
    [BindProperty] public string DebugClient { get; set; } = ModuleScaffold.PilotBim;
    [BindProperty] public string? Folder { get; set; }
    [BindProperty] public bool DeployToTest { get; set; } = true;

    public List<(string Folder, long GroupId)> Folders { get; private set; } = new();
    public ModuleCreationHints? Hints { get; private set; }
    public List<string> Errors { get; } = new();
    public List<string> Warnings { get; } = new();

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        if (Prepare() is { } redirect)
            return redirect;
        if (await DenyModulesChangeAsync(ct) is { } deny)
            return deny;
        await LoadAsync(ct);
        Number ??= Hints?.SuggestedNumber;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        if (Prepare() is { } redirect)
            return redirect;
        if (await DenyModulesChangeAsync(ct) is { } deny)
            return deny;
        await LoadAsync(ct);
        if (Errors.Count > 0)
            return Page();

        var request = BuildRequest();
        Errors.AddRange(ModuleScaffold.Validate(request));
        var target = Folders.FirstOrDefault(f => f.Folder == (Folder ?? ""));
        if (target == default)
            Errors.Add("Выберите папку из списка.");
        if (!IsServer && Hints is not null && Hints.UsedNumbers.Contains(Number ?? ""))
            Errors.Add($"Номер {Number} уже занят другим модулем стенда - возьмите свободный (например, {Hints.SuggestedNumber}).");
        if (Errors.Count > 0)
            return Page();

        var job = new PublishJob
        {
            Operation = IsServer ? "создание серверного модуля" : "создание клиентского модуля",
            Env = IsServer ? "servermodules" : "modules",
            EnvTitle = IsServer ? "серверные модули" : "модули",
            Branch = "main, dev",
            AutomationName = request.Code,
            CommitMessage = $"{ModuleScaffold.AssemblyName(request)} · «{ModuleScaffold.Product(request)}»",
            PilotUser = CurrentSession.PilotLogin,
            GitLabUser = CurrentSession.GitLabUser?.Username,
        };
        job.Add("info", $"Репозиторий {request.Code} в {ModulesGroupPath}{(string.IsNullOrEmpty(Folder) ? "" : "/" + Folder)}");
        _jobs.Add(job);
        try
        {
            var project = await ModuleCreation.CreateAsync(CreateGitLabClient(), AppSettings, request, target.GroupId, DeployToTest,
                (level, text, url) => job.Add(level, text, url), ct);
            Finish(job, null);
            TempData["Flash"] = $"Создан модуль {request.Code}: репозиторий {project.PathWithNamespace}, ветки main и dev." +
                                (DeployToTest ? " Pipeline dev выложит его на тестовый сервер." : "") +
                                (IsServer ? "" : " Новый модуль на сервере пользователям не назначен - назначьте его в Pilot-myAdmin.");
            return RedirectToPage("/Modules/Module", new { name = project.Name, kind = ModuleKind.Query() });
        }
        catch (Exception ex) when (ex is GitLabApiException or InvalidOperationException)
        {
            Errors.Add(ex.Message);
            Finish(job, ex.Message);
            return Page();
        }
    }

    public ModuleScaffoldRequest BuildRequest() => new()
    {
        Kind = ModuleKind,
        Code = Code.Trim(),
        Title = Title.Trim(),
        Description = Description?.Trim() ?? "",
        Number = Number?.Trim() ?? "",
        Section = Section?.Trim() ?? "",
        UseClientSdk = !IsServer && UseClientSdk,
        DebugClient = DebugClient == ModuleScaffold.PilotIce ? ModuleScaffold.PilotIce : ModuleScaffold.PilotBim,
        Author = CurrentSession.GitLabUser?.Username ?? CurrentSession.PilotLogin,
    };

    private void Finish(PublishJob job, string? error)
    {
        job.Update(j =>
        {
            j.Status = error is null ? JobStatus.Succeeded : JobStatus.Failed;
            j.Error = error;
            j.FinishedAt = DateTimeOffset.UtcNow;
        });
        job.Add(error is null ? "ok" : "error", error ?? "Готово.");
        _jobs.Persist();
    }

    private IActionResult? Prepare()
    {
        ViewData["Section"] = IsServer ? "servermodules" : "modules";
        if (!GitLabConnected)
            return RedirectToGitLab();
        if (ModulesGroupPath is null)
            Errors.Add(IsServer ? "В настройках не указана группа GitLab с серверными модулями." : "В настройках не указана группа GitLab с клиентскими модулями.");
        return null;
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        if (Errors.Count > 0)
            return;
        var git = CreateGitLabClient();
        try
        {
            Folders = await AutomationRepositories.ListFoldersAsync(git, ModulesGroupPath!, ct);
        }
        catch (GitLabApiException ex)
        {
            Errors.Add($"GitLab: {ex.Message}");
            return;
        }

        // Занятые номера: сборки репозиториев группы и модули на серверах (в том числе без репозитория).
        var assemblies = new List<string>();
        try
        {
            var catalog = await _catalog.LoadAsync(AppSettings, ModuleLogin, git, ct, ModuleKind);
            assemblies.AddRange(catalog.Rows.Select(r => r.Git?.AssemblyName).OfType<string>());
            assemblies.AddRange(catalog.Pilot.Values.SelectMany(l => l.Modules).Select(m => m.FileName ?? "").Where(f => f.Length > 0));
            foreach (var (env, error) in catalog.PilotErrors)
                Warnings.Add($"Сервер {env}: {error.Message} - номера модулей на нём не учтены.");
        }
        catch (GitLabApiException ex)
        {
            Warnings.Add($"Список модулей не прочитан ({ex.Message}) - проверьте, что номер свободен.");
        }
        Hints = await ModuleCreation.HintsAsync(ModuleKind, assemblies);
    }
}
