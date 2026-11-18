using ProAdmin.Automations;
using ProAdmin.GitLab;
using ProAdmin.Pilot;
using Microsoft.AspNetCore.Mvc;

namespace ProAdmin.Pages.Automations;

/// <summary>Удаление автоматизации с выбранных серверов Pilot и архивирование/удаление репозитория.</summary>
public class DeleteModel : AppPageModel
{
    private readonly AutomationOperations _operations;

    public DeleteModel(AutomationOperations operations) => _operations = operations;

    [BindProperty(SupportsGet = true)] public string Name { get; set; } = "";
    [BindProperty] public List<string> Servers { get; set; } = new();
    [BindProperty] public AutomationOperations.RepoAction RepoAction { get; set; } = AutomationOperations.RepoAction.Archive;
    [BindProperty] public string? Confirm { get; set; }

    public AutomationPresence? Presence { get; private set; }
    public List<string> Errors { get; } = new();

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        if (!GitLabConnected)
            return RedirectToGitLab();
        if (await DenyAutomationsChangeAsync(ct) is { } denied)
            return denied;
        ViewData["Section"] = "automations";
        await LoadAsync(ct);
        Servers = Presence?.ServersWithAutomation.Select(e => e.Key).ToList() ?? new();
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

        if (Confirm?.Trim() != Name)
            Errors.Add("Для подтверждения введите имя автоматизации точно.");
        var repoAction = Presence.Repo is null ? AutomationOperations.RepoAction.Keep : RepoAction;
        if (Servers.Count == 0 && repoAction == AutomationOperations.RepoAction.Keep)
            Errors.Add("Ничего не выбрано для удаления.");
        if (Errors.Count > 0)
            return Page();

        var what = new List<string>();
        if (Servers.Count > 0)
            what.Add("серверы: " + string.Join(", ", Presence.ServersWithAutomation.Where(e => Servers.Contains(e.Key)).Select(e => e.Title)));
        if (repoAction != AutomationOperations.RepoAction.Keep)
            what.Add(repoAction == AutomationOperations.RepoAction.Archive ? "репозиторий - в архив" : "репозиторий - удалить");

        var job = _operations.StartLog("удаление", Name, CurrentSession.PilotLogin, CurrentSession.GitLabUser?.Username,
            $"Удаление «{Name}» ({string.Join("; ", what)}).");
        Exception? failure = null;
        try
        {
            await _operations.DeleteAsync(Presence, Servers, repoAction, env => Vault.GetPilotLogin(CurrentSession, env), CreateGitLabClient(), job, ct);
        }
        catch (Exception ex) when (ex is GitLabApiException or PilotGatewayException)
        {
            failure = ex;
        }
        _operations.Finish(job, failure);
        return RedirectToPage("/Jobs/Details", new { id = job.Id });
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        try
        {
            Presence = await _operations.FindAsync(AppSettings, env => Vault.GetPilotLogin(CurrentSession, env), CreateGitLabClient(), Name, ct);
        }
        catch (GitLabApiException ex)
        {
            Errors.Add($"GitLab: {ex.Message}");
        }
    }
}
