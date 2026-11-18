using ProAdmin.Automations;
using ProAdmin.Automations.Validation;
using ProAdmin.GitLab;
using ProAdmin.Jobs;
using ProAdmin.Pilot;
using ProAdmin.Settings;
using Microsoft.AspNetCore.Mvc;

namespace ProAdmin.Pages.Automations;

/// <summary>
/// Откат прода: содержимое выбранного релиза (тега vX.Y.Z) отправляется в main новым коммитом
/// с новой версией (max + patch) - обычным путём MR / pipeline / прод-сервер / merge / тег.
/// Версии только растут, история в GitLab остаётся честной: «v1.0.3 = откат на v1.0.1».
/// </summary>
public class RollbackModel : AppPageModel
{
    private readonly PilotGateway _pilot;
    private readonly PublishJobRunner _runner;
    private readonly Pilot.ServerModules.PilotServerModuleGateway _modules;

    public RollbackModel(PilotGateway pilot, PublishJobRunner runner, Pilot.ServerModules.PilotServerModuleGateway modules)
    {
        _pilot = pilot;
        _runner = runner;
        _modules = modules;
    }

    [BindProperty(SupportsGet = true)] public string Name { get; set; } = "";
    [BindProperty(SupportsGet = true)] public string? Tag { get; set; }
    [BindProperty] public string? Description { get; set; }
    [BindProperty] public string? TaskReference { get; set; }
    [BindProperty] public string? BasePilotHash { get; set; }
    [BindProperty] public string? BaseMainJsonBlob { get; set; }
    [BindProperty] public string? BaseMainCsBlob { get; set; }

    public EnvironmentSettings? Target { get; private set; }
    public AutomationRepo? Repo { get; private set; }
    public GitLabFile? MainJson { get; private set; }
    public GitLabFile? MainCs { get; private set; }
    public AutomationVersion? MainVersion { get; private set; }
    public AutomationVersion? DevVersion { get; private set; }
    public AutomationSnapshot? ProdServer { get; private set; }
    public List<(GitLabTag Tag, AutomationVersion Version)> Releases { get; } = new();
    public AutomationSnapshot? TagContent { get; private set; }
    public AutomationVersion? NewVersion { get; private set; }
    public string? RollbackTriggers { get; private set; }
    public List<LintIssue> Lint { get; private set; } = new();
    public List<string> Errors { get; } = new();

    public bool CanRollback => Errors.Count == 0 && TagContent is not null && NewVersion is not null && Lint.All(i => !i.IsError);

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        if (!GitLabConnected)
            return RedirectToGitLab();
        if (await DenyAutomationsChangeAsync(ct) is { } denied)
            return denied;
        ViewData["Section"] = "automations";

        await LoadAsync(ct);
        BasePilotHash = ProdServer?.Hash ?? EditModel.Absent;
        BaseMainJsonBlob = MainJson?.BlobId ?? EditModel.Absent;
        BaseMainCsBlob = MainCs?.BlobId ?? EditModel.Absent;
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
        if (!CanRollback)
        {
            if (Errors.Count == 0)
                Errors.Add("Откат невозможен: исправьте ошибки сценария выбранной версии.");
            return Page();
        }

        var message = AutomationChecks.BuildCommitMessage(Target!, Name, $"откат на {Tag}: {Description?.Trim()}", TaskReference, out var messageError);
        if (string.IsNullOrWhiteSpace(Description))
            messageError = "Укажите причину отката - она попадёт в коммит и Release.";
        if (messageError is not null)
        {
            Errors.Add(messageError);
            return Page();
        }

        var request = new PublishRequest
        {
            Env = Target!.Key,
            Name = Name,
            Triggers = RollbackTriggers!,
            CSharpCode = TagContent!.CSharpCode,
            IsPaused = ProdServer?.IsPaused ?? false,
            CommitMessage = message,
            ChangeNotes = new[] { $"Откат на {Tag}: {string.Join("; ", Changelog.SplitNotes(Description))}" },
            TaskReference = TaskReference,
            BasePilotHash = FromBase(BasePilotHash),
            BaseGitJsonBlob = FromBase(BaseMainJsonBlob),
            BaseGitCsBlob = FromBase(BaseMainCsBlob),
        };
        var context = new PublishContext
        {
            Pilot = Vault.GetPilotLogin(CurrentSession, Target.Key),
            ModulesLogin = ModuleLogin(Target.Key),
            GitLabToken = CurrentSession.GitLabToken!,
            Settings = AppSettings,
            ProjectPath = Repo!.ProjectPath,
            CiConfigPath = Repo.CiConfigPath,
        };

        try
        {
            var job = _runner.Start(request, context, CurrentSession.PilotLogin, CurrentSession.GitLabUser?.Username, $"откат на {Tag}");
            job.Add("info", $"Откат: содержимое {Tag}, новая версия {NewVersion} (была {MainVersion?.ToString() ?? "-"}).");
            return RedirectToPage("/Jobs/Details", new { id = job.Id });
        }
        catch (InvalidOperationException ex)
        {
            Errors.Add(ex.Message);
            return Page();
        }
    }

    private static string? FromBase(string? value) => value == EditModel.Absent ? "" : value;

    private async Task LoadAsync(CancellationToken ct)
    {
        Name = Name.Trim();
        Target = AppSettings.Environments.FirstOrDefault(e => e.Branch == AutomationRepositories.MainBranch && e.HasServer);
        if (Target is null)
        {
            Errors.Add("В настройках нет контура с веткой main и сервером Pilot.");
            return;
        }

        var git = CreateGitLabClient();
        try
        {
            Repo = await AutomationRepositories.FindAsync(git, GroupPath!, Name, ct);
            if (Repo is null)
            {
                Errors.Add($"Репозитория «{Name}» нет в GitLab.");
                return;
            }

            MainJson = await git.FindFileAsync(Repo.ProjectPath, AutomationFiles.JsonPath(Name), Target.Branch, ct);
            MainCs = await git.FindFileAsync(Repo.ProjectPath, AutomationFiles.CsPath(Name), Target.Branch, ct);
            MainVersion = AutomationVersion.Find(MainJson?.Text);
            var devJson = await git.FindFileAsync(Repo.ProjectPath, AutomationFiles.JsonPath(Name), AutomationRepositories.DevBranch, ct);
            DevVersion = AutomationVersion.Find(devJson?.Text);

            foreach (var tag in await git.ListVersionTagsAsync(Repo.ProjectPath, ct) ?? new())
                if (AutomationVersion.TryParseTag(tag.Name, out var v))
                    Releases.Add((tag, v));
            Releases.Sort((a, b) => b.Version.CompareTo(a.Version));

            if (Releases.Count == 0)
            {
                Errors.Add("В репозитории ещё нет релизов (тегов vX.Y.Z) - откатываться не к чему. Теги появляются после переносов на прод.");
                return;
            }

            // По умолчанию - ближайший релиз ниже текущей версии main.
            Tag ??= Releases.FirstOrDefault(r => MainVersion is null || r.Version < MainVersion.Value).Tag?.Name;
            var selected = Releases.FirstOrDefault(r => r.Tag.Name == Tag);
            if (selected.Tag is null)
            {
                Errors.Add(Tag is null ? "Нет релизов старше текущей версии." : $"Релиз {Tag} не найден.");
                return;
            }
            if (MainVersion is { } current && selected.Version == current)
            {
                Errors.Add($"{Tag} - это текущая версия в main. Выберите более ранний релиз.");
                return;
            }

            TagContent = await AutomationOperations.ReadBranchAsync(git, Repo, selected.Tag.Name, ct);
            if (TagContent is null)
            {
                Errors.Add($"В релизе {Tag} нет {AutomationFiles.JsonPath(Name)}.");
                return;
            }
        }
        catch (GitLabApiException ex)
        {
            Errors.Add($"GitLab: {ex.Message}");
            return;
        }

        // Новая версия - выше всех существующих, чтобы не конфликтовать ни с тегами, ни с проверкой версий.
        var highest = Releases.Select(r => r.Version).Append(MainVersion ?? default).Max();
        NewVersion = highest.NextPatch();
        RollbackTriggers = AutomationVersion.WithVersion(TagContent.Triggers, NewVersion.Value);

        var login = Vault.GetPilotLogin(CurrentSession, Target.Key);
        try
        {
            ProdServer = await _pilot.GetAutomationAsync(Target.PilotUrl, login, Name, ct);
            var metadata = await _pilot.GetMetadataIndexAsync(Target.PilotUrl, login, ct);
            var hosted = await _modules.GetHostedActivitiesAsync(Target.PilotUrl, ModuleLogin(Target.Key), AppSettings.ReportsLicenseType, ct);
            Lint = AutomationLinter.Lint(RollbackTriggers, TagContent.CSharpCode, Name, metadata, hosted);
        }
        catch (PilotGatewayException ex)
        {
            Errors.Add($"Сервер {Target.Title}: {ex.Message}");
        }
    }
}
