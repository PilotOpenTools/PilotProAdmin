using ProAdmin.Automations;
using ProAdmin.GitLab;
using ProAdmin.Modules;
using ProAdmin.Types;
using Microsoft.AspNetCore.Mvc;

namespace ProAdmin.Pages.Types;

/// <summary>Перенос выбранных коммитов: dev / release (стейдж) или release / main (прод, с версией и CHANGELOG).</summary>
public class PromoteModel : AppPageModel
{
    private readonly TypesJobRunner _runner;

    public PromoteModel(TypesJobRunner runner) => _runner = runner;

    [BindProperty(SupportsGet = true)] public string Target { get; set; } = TypesBranches.Release;
    /// <summary>Выбранные коммиты; в ссылке со вкладки «Типы» - коммит dev, тогда в main отмечается его перенос в release.</summary>
    [BindProperty(SupportsGet = true)] public List<string> Selected { get; set; } = new();
    [BindProperty] public string? Version { get; set; }
    [BindProperty] public string? TaskId { get; set; }
    [BindProperty] public string? Notes { get; set; }

    public string Source => TypesBranches.SourceOf(Target) ?? TypesBranches.Dev;
    public bool IsMain => Target == TypesBranches.Main;
    public TypesEnv? Env => TypesEnv.ForBranch(AppSettings, Target);
    public List<PendingCommit> Pending { get; private set; } = new();
    public string? MainVersion { get; private set; }
    public ModuleVersion SuggestedVersion { get; private set; } = new(1, 1, 0);
    public List<string> Errors { get; } = new();
    public GitLabGroupAccess? Access { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        ViewData["Section"] = "types";
        if (!GitLabConnected)
            return RedirectToGitLab();
        await LoadAsync(ct);
        Notes = string.Join("\n", Pending.Select(p => p.Commit.Title));
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        ViewData["Section"] = "types";
        if (!GitLabConnected)
            return RedirectToGitLab();
        await LoadAsync(ct);
        if (GitLabGroupAccess.CannotChange(Access))
        {
            Errors.Add(ChangeDeniedText("типы", Access, Access?.GroupPath));
            return Page();
        }
        if (Env is not { HasServer: true })
            Errors.Add($"В настройках не задан сервер для ветки {Target}.");
        if (Selected.Count == 0)
            Errors.Add("Выберите хотя бы один коммит.");

        ModuleVersion? version = null;
        var task = string.IsNullOrWhiteSpace(TaskId) ? null : TaskId.Trim();
        var notes = Changelog.SplitNotes(Notes);
        if (IsMain)
        {
            if (!ModuleVersion.TryParse(Version, out version))
                Errors.Add("Версия - в формате X.Y.Z.");
            else if (ModuleVersion.TryParse(MainVersion, out var current) && version! <= current!)
                Errors.Add($"Версия {version} должна быть выше текущей ({current}).");
            if (task is null && AppSettings.Find(TypesEnv.Prod)?.RequireTaskReference == true)
                Errors.Add("Для выпуска в прод укажите задачу (так настроен контур «Прод»).");
        }
        if (Errors.Count > 0)
            return Page();

        try
        {
            var job = _runner.StartPromote(TypesContexts.Create(this), Target, Selected, version, notes, task,
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
        Target = Target == TypesBranches.Main ? TypesBranches.Main : TypesBranches.Release;
        var project = AppSettings.TypesProjectPath;
        if (project is null)
        {
            Errors.Add("В настройках не указан репозиторий типов.");
            return;
        }
        Access = await GetGroupAccessAsync(TypesRepository.GroupOf(project), fresh: false, ct);
        var git = CreateGitLabClient();
        try
        {
            Pending = await TypesRepository.PendingAsync(git, project, Source, Target, ct);
            // Ссылка «На прод» с коммита dev: отметить его перенос в release.
            if (Selected.Count > 0 && IsMain)
                Selected = Pending.Where(p => Selected.Contains(p.Commit.Id) ||
                                             TypesRepository.PickedFrom(p.Commit.Message).Any(x => Selected.Any(s => s.StartsWith(x, StringComparison.Ordinal))))
                    .Select(p => p.Commit.Id).ToList();
            await Task.WhenAll(Pending.Take(60).Select(async p => p.Items.AddRange(await TypesRepository.ItemsAsync(git, project, p.Commit.Id, ct))));

            if (!IsMain)
                return;
            if (await git.FindFileAsync(project, TypesSnapshot.ConfigFile, TypesBranches.Main, ct) is { } config)
                MainVersion = TypesJson.Read<TypesConfigInfo>(config.Text, TypesSnapshot.ConfigFile).Version;
            SuggestedVersion = ModuleVersion.TryParse(MainVersion, out var v) ? v!.NextMinor() : new ModuleVersion(1, 1, 0);
            Version ??= SuggestedVersion.ToString();
        }
        catch (Exception ex) when (ex is GitLabApiException or TypesFormatException)
        {
            Errors.Add(ex.Message);
        }
    }

    public string? TaskUrl(string? text) =>
        ModuleBranches.TaskUrl(ModuleBranches.Task(text, AppSettings.TaskPattern), AppSettings.TaskUrlTemplate);
}
