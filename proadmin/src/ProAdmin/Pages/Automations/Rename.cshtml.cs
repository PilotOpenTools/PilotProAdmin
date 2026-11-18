using ProAdmin.Automations;
using ProAdmin.GitLab;
using ProAdmin.Pilot;
using Microsoft.AspNetCore.Mvc;

namespace ProAdmin.Pages.Automations;

/// <summary>Переименование автоматизации: репозиторий, файлы в ветках и все серверы Pilot.</summary>
public class RenameModel : AppPageModel
{
    private readonly AutomationOperations _operations;

    public RenameModel(AutomationOperations operations) => _operations = operations;

    [BindProperty(SupportsGet = true)] public string Name { get; set; } = "";
    [BindProperty] public string? NewName { get; set; }

    public AutomationPresence? Presence { get; private set; }
    public string? NameProblem => AutomationFiles.NameError(Name);
    public List<string> Errors { get; } = new();

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        if (!GitLabConnected)
            return RedirectToGitLab();
        if (await DenyAutomationsChangeAsync(ct) is { } denied)
            return denied;
        ViewData["Section"] = "automations";
        await LoadAsync(ct);
        NewName ??= NameProblem is null ? Name : AutomationFiles.SuggestName(Name);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        if (!GitLabConnected)
            return RedirectToGitLab();
        if (await DenyAutomationsChangeAsync(ct) is { } denied)
            return denied;
        ViewData["Section"] = "automations";
        await LoadAsync(ct);
        if (Presence is null)
            return Page();

        var newName = NewName?.Trim() ?? "";
        if (AutomationFiles.NameError(newName) is { } error)
            Errors.Add(error);
        else if (newName == Name)
            Errors.Add("Новое имя совпадает с текущим.");
        if (Presence.Servers.Any(s => s.Error is not null))
            Errors.Add("Не все серверы Pilot доступны - переименование везде сразу невозможно.");
        if (Errors.Count > 0)
            return Page();

        var git = CreateGitLabClient();
        try
        {
            if (await AutomationRepositories.FindAsync(git, GroupPath!, newName, ct) is { } taken)
            {
                Errors.Add($"Репозиторий «{newName}» уже есть: {taken.ProjectPath}.");
                return Page();
            }
            var clash = await _operations.FindAsync(AppSettings, Login, git, newName, ct);
            if (clash.ServersWithAutomation.Any())
            {
                Errors.Add($"Автоматизация «{newName}» уже есть на серверах: {string.Join(", ", clash.ServersWithAutomation.Select(e => e.Title))}.");
                return Page();
            }
        }
        catch (GitLabApiException ex)
        {
            Errors.Add($"GitLab: {ex.Message}");
            return Page();
        }

        var job = _operations.StartLog("переименование", Name, CurrentSession.PilotLogin, CurrentSession.GitLabUser?.Username,
            $"Переименование «{Name}» / «{newName}».");
        Exception? failure = null;
        try
        {
            await _operations.RenameAsync(Presence, newName, Login, git, job, ct);
        }
        catch (Exception ex) when (ex is GitLabApiException or PilotGatewayException)
        {
            failure = ex;
        }
        _operations.Finish(job, failure);
        return RedirectToPage("/Jobs/Details", new { id = job.Id });
    }

    private PilotLogin Login(string env) => Vault.GetPilotLogin(CurrentSession, env);

    private async Task LoadAsync(CancellationToken ct)
    {
        try
        {
            Presence = await _operations.FindAsync(AppSettings, Login, CreateGitLabClient(), Name, ct);
            if (Presence.Repo is null && !Presence.ServersWithAutomation.Any())
                Errors.Add($"Автоматизации «{Name}» нет ни на серверах, ни в GitLab.");
        }
        catch (GitLabApiException ex)
        {
            Errors.Add($"GitLab: {ex.Message}");
        }
    }
}
