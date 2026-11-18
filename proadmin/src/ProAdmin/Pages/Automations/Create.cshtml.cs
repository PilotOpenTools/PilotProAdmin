using ProAdmin.Automations;
using ProAdmin.GitLab;
using ProAdmin.Pilot;
using ProAdmin.Settings;
using Microsoft.AspNetCore.Mvc;

namespace ProAdmin.Pages.Automations;

/// <summary>
/// Создание автоматизации: отдельный репозиторий в выбранной папке (подгруппе) с ветками main и dev.
/// Режимы: новая; «source» - репозиторий для автоматизации, которая уже есть на серверах (с переименованием,
/// если её имя не годится для GitLab); «copyFrom» - копия существующей автоматизации под новым именем.
/// </summary>
public class CreateModel : AppPageModel
{
    private readonly PilotGateway _pilot;
    private readonly AutomationOperations _operations;

    public CreateModel(PilotGateway pilot, AutomationOperations operations)
    {
        _pilot = pilot;
        _operations = operations;
    }

    [BindProperty(SupportsGet = true)] public string? Name { get; set; }
    [BindProperty(SupportsGet = true)] public string? Folder { get; set; }
    [BindProperty(SupportsGet = true)] public string? Source { get; set; }
    [BindProperty(SupportsGet = true)] public string? CopyFrom { get; set; }
    [BindProperty] public string? Description { get; set; }
    [BindProperty] public bool ImportFromServers { get; set; } = true;

    public List<(string Folder, long GroupId)> Folders { get; private set; } = new();
    public List<string> Errors { get; } = new();

    public bool IsImport => !string.IsNullOrEmpty(Source);
    public bool IsCopy => !string.IsNullOrEmpty(CopyFrom);
    public string? SourceNameProblem => IsImport ? AutomationFiles.NameError(Source) : null;

    public EnvironmentSettings? DevEnv => AppSettings.Environments.FirstOrDefault(e => e.Branch == AutomationRepositories.DevBranch && e.HasServer);
    public EnvironmentSettings? MainEnv => AppSettings.Environments.FirstOrDefault(e => e.Branch == AutomationRepositories.MainBranch && e.HasServer);

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        if (Prepare() is { } redirect)
            return redirect;
        if (await DenyAutomationsChangeAsync(ct) is { } denied)
            return denied;

        await LoadFoldersAsync(ct);

        if (IsImport)
        {
            Name ??= SourceNameProblem is null ? Source : AutomationFiles.SuggestName(Source!);
        }
        else if (IsCopy)
        {
            var baseName = AutomationFiles.NameError(CopyFrom) is null ? CopyFrom! : AutomationFiles.SuggestName(CopyFrom!);
            Name ??= baseName + "Copy";
            if (GroupPath is not null)
            {
                try
                {
                    if (await AutomationRepositories.FindAsync(CreateGitLabClient(), GroupPath, CopyFrom!, ct) is { } repo)
                    {
                        Folder ??= repo.Folder;
                        Description ??= repo.Description;
                    }
                }
                catch (GitLabApiException ex)
                {
                    Errors.Add($"GitLab: {ex.Message}");
                }
            }
        }
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        if (Prepare() is { } redirect)
            return redirect;
        if (await DenyAutomationsChangeAsync(ct) is { } denied)
            return denied;

        await LoadFoldersAsync(ct);
        if (Errors.Count > 0)
            return Page();

        var name = Name?.Trim() ?? "";
        var description = Description?.Trim() ?? "";
        var folder = Folder ?? "";

        if (AutomationFiles.NameError(name) is { } nameError)
            Errors.Add(nameError);
        if (description.Length == 0)
            Errors.Add("Опишите, что делает автоматизация - описание попадёт в README и в описание репозитория.");
        var target = Folders.FirstOrDefault(f => f.Folder == folder);
        if (target == default)
            Errors.Add("Выберите папку из списка.");
        if (Errors.Count > 0)
            return Page();

        var git = CreateGitLabClient();
        Pilot.PilotLogin Login(string env) => Vault.GetPilotLogin(CurrentSession, env);
        try
        {
            if (await AutomationRepositories.FindAsync(git, GroupPath!, name, ct) is { } existing)
            {
                Errors.Add($"Репозиторий «{name}» уже есть: {existing.ProjectPath}.");
                return Page();
            }

            // Новое имя не должно занимать чужую автоматизацию на серверах.
            if (name != Source)
            {
                var clash = await _operations.FindAsync(AppSettings, Login, git, name, ct);
                if (clash.ServersWithAutomation.Any() && (IsImport || IsCopy))
                {
                    Errors.Add($"Автоматизация «{name}» уже есть на серверах: {string.Join(", ", clash.ServersWithAutomation.Select(e => e.Title))}. Выберите другое имя.");
                    return Page();
                }
            }

            // Содержимое веток.
            AutomationSnapshot? mainContent = null, devContent = null;
            AutomationPresence? sourcePresence = null;
            if (IsCopy)
            {
                var sourceRepo = await AutomationRepositories.FindAsync(git, GroupPath!, CopyFrom!, ct);
                if (sourceRepo is not null)
                {
                    mainContent = await AutomationOperations.ReadBranchAsync(git, sourceRepo, AutomationRepositories.MainBranch, ct);
                    devContent = await AutomationOperations.ReadBranchAsync(git, sourceRepo, AutomationRepositories.DevBranch, ct);
                }
                else
                {
                    mainContent = await FromServerAsync(MainEnv, CopyFrom!, ct);
                    devContent = await FromServerAsync(DevEnv, CopyFrom!, ct);
                }
            }
            else if (IsImport)
            {
                sourcePresence = await _operations.FindAsync(AppSettings, Login, git, Source!, ct);
                if (sourcePresence.Servers.FirstOrDefault(s => s.Error is not null) is { Error: not null } broken)
                {
                    Errors.Add($"Сервер {broken.Env.Title} недоступен: {broken.Error}");
                    return Page();
                }
                mainContent = sourcePresence.Servers.FirstOrDefault(s => s.Env == MainEnv).Automation;
                devContent = sourcePresence.Servers.FirstOrDefault(s => s.Env == DevEnv).Automation;
            }
            else if (ImportFromServers)
            {
                mainContent = await FromServerAsync(MainEnv, name, ct);
                devContent = await FromServerAsync(DevEnv, name, ct);
            }

            var operation = IsCopy ? "копирование" : IsImport ? "создание репозитория" : "создание";
            var job = _operations.StartLog(operation, name, CurrentSession.PilotLogin, CurrentSession.GitLabUser?.Username,
                IsCopy ? $"Копия «{CopyFrom}» / «{name}» в {GroupPath}/{folder}".TrimEnd('/')
                    : $"Репозиторий «{name}» в {GroupPath}/{folder}".TrimEnd('/') + (IsImport && name != Source ? $", переименование «{Source}» / «{name}» на серверах" : ""));
            Exception? failure = null;
            try
            {
                var project = await AutomationRepositories.CreateAsync(git, name, target.GroupId, description, mainContent, devContent, AppSettings.AutomationsCiConfigPath, ct, (level, text) => job.Add(level, text));
                job.Add("ok", $"Создан репозиторий {project.PathWithNamespace}: main / {Origin(mainContent, MainEnv)}, dev / {Origin(devContent, DevEnv)}.", project.WebUrl);

                if (IsImport && name != Source && sourcePresence is not null)
                    await _operations.RenameOnServersAsync(sourcePresence, Source!, name, Login, job, ct);
            }
            catch (Exception ex) when (ex is GitLabApiException or PilotGatewayException)
            {
                failure = ex;
            }
            _operations.Finish(job, failure);

            if (failure is not null)
                return RedirectToPage("/Jobs/Details", new { id = job.Id });

            TempData["Flash"] = $"Создан репозиторий «{name}» с ветками main и dev." + (IsCopy ? " Копия ещё не на серверах - отправьте её из редактора." : "");
            var editEnv = DevEnv ?? AppSettings.Environments.FirstOrDefault(e => e.IsEditable);
            return editEnv is null
                ? RedirectToPage("/Automations/Index")
                : Redirect($"/Automations/{editEnv.Key}/Edit?name={Uri.EscapeDataString(name)}");
        }
        catch (PilotGatewayException ex)
        {
            Errors.Add($"Не удалось прочитать автоматизацию с сервера Pilot: {ex.Message}");
        }
        catch (GitLabApiException ex)
        {
            Errors.Add($"GitLab: {ex.Message}");
        }
        return Page();
    }

    private string Origin(AutomationSnapshot? content, EnvironmentSettings? env) =>
        content is null ? "пустая автоматизация"
        : IsCopy ? $"копия «{CopyFrom}»"
        : $"сервер {env?.Title}";

    private async Task<AutomationSnapshot?> FromServerAsync(EnvironmentSettings? env, string name, CancellationToken ct) =>
        env is null ? null : await _pilot.GetAutomationAsync(env.PilotUrl, Vault.GetPilotLogin(CurrentSession, env.Key), name, ct);

    private IActionResult? Prepare()
    {
        ViewData["Section"] = "automations";
        if (!GitLabConnected)
            return RedirectToGitLab();
        return null;
    }

    private async Task LoadFoldersAsync(CancellationToken ct)
    {
        if (GroupPath is null)
        {
            Errors.Add("В настройках не указана группа GitLab.");
            return;
        }
        try
        {
            Folders = await AutomationRepositories.ListFoldersAsync(CreateGitLabClient(), GroupPath, ct);
        }
        catch (GitLabApiException ex)
        {
            Errors.Add($"GitLab: {ex.Message}");
        }
    }
}
