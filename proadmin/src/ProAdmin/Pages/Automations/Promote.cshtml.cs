using ProAdmin.Automations;
using ProAdmin.Automations.Validation;
using ProAdmin.GitLab;
using ProAdmin.Jobs;
using ProAdmin.Pilot;
using ProAdmin.Settings;
using Microsoft.AspNetCore.Mvc;

namespace ProAdmin.Pages.Automations;

/// <summary>
/// Перенос на прод: версия из ветки контура-источника (dev, проверена на тесте) отправляется в ветку прода (main)
/// обычным путём - MR, pipeline, запись на продовый сервер, merge.
/// </summary>
public class PromoteModel : AppPageModel
{
    private readonly PilotGateway _pilot;
    private readonly PublishJobRunner _runner;
    private readonly Pilot.ServerModules.PilotServerModuleGateway _modules;

    public PromoteModel(PilotGateway pilot, PublishJobRunner runner, Pilot.ServerModules.PilotServerModuleGateway modules)
    {
        _pilot = pilot;
        _runner = runner;
        _modules = modules;
    }

    [BindProperty(SupportsGet = true)] public string Name { get; set; } = "";

    /// <summary>hotfix/… - выпустить hotfix вместо релиза/dev (после успеха ветка удаляется).</summary>
    [BindProperty(SupportsGet = true)] public string? From { get; set; }

    public bool IsHotfix => From is { } f && ProAdmin.Modules.ModuleBranches.IsHotfix(f);
    [BindProperty] public string? Description { get; set; }
    [BindProperty] public string? TaskReference { get; set; }
    [BindProperty] public string? BasePilotHash { get; set; }
    [BindProperty] public string? BaseMainJsonBlob { get; set; }
    [BindProperty] public string? BaseMainCsBlob { get; set; }

    public EnvironmentSettings? Source { get; private set; }

    /// <summary>Откуда переносится: активный релиз со стейджа (release/X.Y.Z) или dev, если релиза нет.</summary>
    public string SourceBranch { get; private set; } = AutomationRepositories.DevBranch;
    public EnvironmentSettings? Target { get; private set; }
    public AutomationRepo? Repo { get; private set; }
    public AutomationSnapshot? SourceContent { get; private set; }
    public GitLabFile? TargetJson { get; private set; }
    public GitLabFile? TargetCs { get; private set; }
    public AutomationSnapshot? TargetServer { get; private set; }
    public AutomationSnapshot? SourceServer { get; private set; }
    public List<LintIssue> Lint { get; private set; } = new();
    public string? LintNote { get; private set; }
    public List<string> Errors { get; } = new();

    /// <summary>Откуда подставлено «что изменилось» (null - ничего не найдено).</summary>
    public string? NotesSource { get; private set; }

    public bool BranchesEqual => SourceContent is not null && TargetJson is not null &&
                                 AutomationFiles.SameContent(SourceContent.Triggers, TargetJson.Text) &&
                                 AutomationFiles.SameContent(SourceContent.CSharpCode, TargetCs?.Text);

    public bool ServerUpToDate => SourceContent is not null && TargetServer is not null &&
                                  AutomationFiles.SameContent(SourceContent.Triggers, TargetServer.Triggers) &&
                                  AutomationFiles.SameContent(SourceContent.CSharpCode, TargetServer.CSharpCode);

    public bool TestedOnSourceServer => SourceContent is not null && SourceServer is not null &&
                                        AutomationFiles.SameContent(SourceContent.Triggers, SourceServer.Triggers) &&
                                        AutomationFiles.SameContent(SourceContent.CSharpCode, SourceServer.CSharpCode);

    public bool CanPromote => Errors.Count == 0 && SourceContent is not null && Target is not null && !(BranchesEqual && ServerUpToDate);

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        if (!GitLabConnected)
            return RedirectToGitLab();
        if (await DenyAutomationsChangeAsync(ct) is { } denied)
            return denied;
        ViewData["Section"] = "automations";

        await LoadAsync(ct);
        BasePilotHash = TargetServer?.Hash ?? EditModel.Absent;
        BaseMainJsonBlob = TargetJson?.BlobId ?? EditModel.Absent;
        BaseMainCsBlob = TargetCs?.BlobId ?? EditModel.Absent;
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
        if (!CanPromote)
        {
            if (Errors.Count == 0)
                Errors.Add("Переносить нечего: ветка и сервер прода уже содержат эту версию.");
            return Page();
        }

        var message = AutomationChecks.BuildCommitMessage(Target!, Name, Description, TaskReference, out var messageError);
        if (messageError is not null)
        {
            Errors.Add(messageError);
            return Page();
        }

        var request = new PublishRequest
        {
            Env = Target!.Key,
            Name = Name,
            Triggers = SourceContent!.Triggers,
            CSharpCode = SourceContent.CSharpCode,
            // Пауза на проде не меняется переносом; новая автоматизация создаётся включённой.
            IsPaused = TargetServer?.IsPaused ?? false,
            CommitMessage = message,
            ChangeNotes = Changelog.SplitNotes(Description),
            TaskReference = TaskReference,
            BasePilotHash = FromBase(BasePilotHash),
            BaseGitJsonBlob = FromBase(BaseMainJsonBlob),
            BaseGitCsBlob = FromBase(BaseMainCsBlob),
            SourceBranch = IsHotfix ? SourceBranch : null,
            ContentRef = SourceBranch,
        };
        var context = new PublishContext
        {
            Pilot = Vault.GetPilotLogin(CurrentSession, Target.Key),
            ModulesLogin = ModuleLogin(Target.Key),
            GitLabToken = CurrentSession.GitLabToken!,
            Settings = AppSettings,
            ProjectPath = Repo!.ProjectPath,
            CiConfigPath = Repo.CiConfigPath,
            Types = ProAdmin.Pages.Types.TypesContexts.Follow(this),
        };

        try
        {
            var job = _runner.Start(request, context, CurrentSession.PilotLogin, CurrentSession.GitLabUser?.Username,
                $"перенос {SourceBranch} / {Target.Branch}");
            job.Add("info", $"Источник: ветка {SourceBranch} (версия {SourceContent.Version?.ToString() ?? "без версии"}).");
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
        Source = AppSettings.Environments.FirstOrDefault(e => e.Branch == AutomationRepositories.DevBranch);
        Target = AppSettings.Environments.FirstOrDefault(e => e.Branch == AutomationRepositories.MainBranch);
        if (Source is null || Target is null)
        {
            Errors.Add("В настройках должны быть контуры с ветками dev и main.");
            return;
        }
        if (!Target.HasServer)
        {
            Errors.Add($"Для контура {Target.Title} не задан сервер Pilot.");
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
            var mainForRelease = await git.FindFileAsync(Repo.ProjectPath, AutomationFiles.JsonPath(Name), Target.Branch, ct);
            var branches = await git.ListBranchesAsync(Repo.ProjectPath, null, ct) ?? new();
            if (IsHotfix)
            {
                if (branches.All(b => b.Name != From))
                {
                    Errors.Add($"Ветки {From} нет.");
                    return;
                }
                SourceBranch = From!;
            }
            else if ((string.IsNullOrWhiteSpace(AppSettings.StagePilotUrl) || !AppSettings.AutomationsAutoStage) &&
                     ProAdmin.Content.ContentReleases.ActiveAutomationRelease(branches, AutomationVersion.Find(mainForRelease?.Text)) is { } release)
                SourceBranch = release.Name;
            SourceContent = await AutomationOperations.ReadBranchAsync(git, Repo, SourceBranch, ct);
            if (SourceContent is null)
            {
                Errors.Add($"В ветке {SourceBranch} нет {AutomationFiles.JsonPath(Name)} - переносить нечего.");
                return;
            }
            TargetJson = await git.FindFileAsync(Repo.ProjectPath, AutomationFiles.JsonPath(Name), Target.Branch, ct);
            TargetCs = await git.FindFileAsync(Repo.ProjectPath, AutomationFiles.CsPath(Name), Target.Branch, ct);

            // dev должен быть не ниже main: иначе на проде есть изменения, которые не проходили через тест.
            var devVersion = SourceContent.Version;
            var mainVersion = AutomationVersion.Find(TargetJson?.Text);
            if (devVersion is null)
                Errors.Add($"В ветке {SourceBranch} не указана версия («Version: X.Y.Z» в _comment_) - повысьте её на тесте.");
            else if (mainVersion is { } mv && mv > devVersion.Value)
                Errors.Add($"В {Target.Branch} версия {mv} выше, чем в {SourceBranch} ({devVersion}): прод меняли мимо теста. " +
                           $"Сначала перенесите эти изменения в {SourceBranch} (в редакторе теста - «Из ветки {Target.Branch}»), повысьте версию и проверьте на тесте.");
            else if (!BranchesEqual && mainVersion is { } same && same == devVersion.Value)
                Errors.Add($"Содержимое {SourceBranch} отличается от {Target.Branch}, но версия та же ({same}). Повысьте версию на тесте.");

            // Первое открытие формы: «что изменилось» - из журнала dev (или из коммитов, если журнала нет).
            if (Description is null)
            {
                var (notes, source) = await ChangelogSource.PendingAsync(git, Repo.ProjectPath, Name, mainVersion, ct);
                Description = string.Join("\n", notes);
                NotesSource = source;
            }
        }
        catch (GitLabApiException ex)
        {
            Errors.Add($"GitLab: {ex.Message}");
            return;
        }

        var targetLogin = Vault.GetPilotLogin(CurrentSession, Target.Key);
        try
        {
            TargetServer = await _pilot.GetAutomationAsync(Target.PilotUrl, targetLogin, Name, ct);
        }
        catch (PilotGatewayException ex)
        {
            Errors.Add($"Сервер {Target.Title}: {ex.Message}");
            return;
        }

        if (Source.HasServer)
        {
            try { SourceServer = await _pilot.GetAutomationAsync(Source.PilotUrl, Vault.GetPilotLogin(CurrentSession, Source.Key), Name, ct); }
            catch (PilotGatewayException) { /* не критично: только для подсказки «проверено на тесте» */ }
        }

        // Сценарий проверяется по базе прода: там могут не быть атрибуты/состояния, созданные на тесте.
        MetadataIndex? metadata = null;
        try
        {
            metadata = await _pilot.GetMetadataIndexAsync(Target.PilotUrl, targetLogin, ct);
            LintNote = $"Сверено со справкой Pilot и базой сервера {Target.Title}.";
        }
        catch (PilotGatewayException ex)
        {
            LintNote = $"База сервера {Target.Title} недоступна ({ex.Message}) - сверено только со справкой.";
        }
        var hosted = await _modules.GetHostedActivitiesAsync(Target.PilotUrl, ModuleLogin(Target.Key), AppSettings.ReportsLicenseType, ct);
        if (hosted.Note.Length > 0)
            LintNote += " " + hosted.Note;
        Lint = AutomationLinter.Lint(SourceContent.Triggers, SourceContent.CSharpCode, Name, metadata, hosted);
    }
}
