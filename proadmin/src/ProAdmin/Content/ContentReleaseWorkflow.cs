using Ascon.Pilot.DataClasses;
using ProAdmin.Automations;
using ProAdmin.GitLab;
using ProAdmin.Jobs;
using ProAdmin.Pilot;
using ProAdmin.Settings;

namespace ProAdmin.Content;

/// <summary>Релиз автоматизации на стейдж (у отчётов стейджа нет - два контура).</summary>
public sealed class ContentReleaseRequest
{
    public required string Name { get; init; }
    public required string ProjectPath { get; init; }
}

public sealed class ContentReleaseContext
{
    public required PilotLogin StageLogin { get; init; }

    /// <summary>Репозиторий типов: связанные с автоматизацией изменения уходят на стейдж перед ней; null - без типов.</summary>
    public Types.TypesFollowDeps? Types { get; init; }
    public required GitLabToken GitLabToken { get; init; }
    public required AdminSettings Settings { get; init; }
}

/// <summary>
/// «Релиз / стейдж»: ветка <c>release/&lt;версия&gt;</c> от dev (активный релиз переиспользуется) и запись её содержимого
/// на сервер стейджа. Версия автоматизации - X.Y.Z из dev (должна быть выше main), отчёта - дата.
/// В main и на прод релиз переносит «На прод» (источник - эта ветка).
/// </summary>
internal sealed class ContentReleaseWorkflow
{
    private readonly PilotGateway _automations;
    private readonly GitLabClient _git;
    private readonly PublishJob _job;
    private readonly ContentReleaseRequest _req;
    private readonly ContentReleaseContext _ctx;
    private readonly EnvironmentSettings _stage;

    public ContentReleaseWorkflow(PilotGateway automations, GitLabClient git, PublishJob job,
        ContentReleaseRequest request, ContentReleaseContext context)
    {
        _automations = automations;
        _git = git;
        _job = job;
        _req = request;
        _ctx = context;
        _stage = ContentReleases.StageEnv(context.Settings) ?? throw new WorkflowException("В настройках не задан сервер стейджа.");
    }

    private string Project => _req.ProjectPath;
    private const string Dev = AutomationRepositories.DevBranch;
    private const string Main = AutomationRepositories.MainBranch;

    public Task RunAsync(CancellationToken ct) => AutomationAsync(ct);

    private async Task AutomationAsync(CancellationToken ct)
    {
        var repo = new AutomationRepo { Name = _req.Name, ProjectPath = Project, Folder = "", WebUrl = "" };
        var branches = await _git.ListBranchesAsync(Project, null, ct) ?? new();
        var main = await AutomationOperations.ReadBranchAsync(_git, repo, Main, ct);
        var mainVersion = AutomationVersion.Find(main?.Triggers);

        string branch;
        if (ContentReleases.ActiveAutomationRelease(branches, mainVersion) is { } active)
        {
            branch = active.Name;
            Info($"Активный релиз {branch} уже есть - выкладываю его на стейдж заново.");
        }
        else
        {
            var dev = await AutomationOperations.ReadBranchAsync(_git, repo, Dev, ct)
                      ?? throw new WorkflowException($"В ветке {Dev} нет автоматизации «{_req.Name}».");
            var version = AutomationVersion.Find(dev.Triggers)
                          ?? throw new WorkflowException($"В {Dev} у автоматизации нет версии (Version: X.Y.Z) - сохраните её в редакторе.");
            if (mainVersion is { } mv && version <= mv)
                throw new WorkflowException($"Версия в {Dev} ({version}) не выше, чем в {Main} ({mv}): на проде уже она. Поднимите версию, сохранив изменения в редакторе на тесте.");
            branch = ContentReleases.Branch(version.ToString());
            await _git.CreateBranchAsync(Project, branch, Dev, ct);
            Ok($"Создана ветка {branch} от {Dev}.");
        }
        _job.Update(j => j.Branch = branch);

        var content = await AutomationOperations.ReadBranchAsync(_git, repo, branch, ct)
                      ?? throw new WorkflowException($"В ветке {branch} нет автоматизации «{_req.Name}».");
        if (_ctx.Types is { } types)
            await types.For(_git, _job).StageAsync(Types.TypesTargetKind.Automation, Project, branch, "Автоматизация НЕ записана на стейдж.", ct);
        Info($"Запись на сервер {_stage.Title}…");
        var metadata = await _automations.UpdateAutomationsAsync(_stage.PilotUrl, _ctx.StageLogin, list =>
        {
            var index = list.FindIndex(a => a.Name == _req.Name);
            var script = index >= 0 ? list[index] : new AutomationScript { Name = _req.Name };
            if (index < 0) list.Add(script);
            script.Triggers = content.Triggers;
            if (content.CSharpCode.Length > 0 || !string.IsNullOrWhiteSpace(script.CSharpCode))
                script.CSharpCode = content.CSharpCode;
        }, ct);
        Ok($"Автоматизация из {branch} сохранена на сервере {_stage.Title} (версия метаданных {metadata}).");
    }

    private void Info(string text, string? url = null) => _job.Add("info", text, url);
    private void Ok(string text, string? url = null) => _job.Add("ok", text, url);
}

/// <summary>Запускает «Релиз / стейдж» автоматизаций в фоне; журнал - общий (вкладка «Операции»).</summary>
public sealed class ContentReleaseRunner
{
    public const string Operation = "релиз / стейдж";

    private readonly JobStore _store;
    private readonly PilotGateway _automations;
    private readonly GitLabClientFactory _gitLab;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<ContentReleaseRunner> _logger;
    private readonly object _startLock = new();

    public ContentReleaseRunner(JobStore store, PilotGateway automations, GitLabClientFactory gitLab,
        IHostApplicationLifetime lifetime, ILogger<ContentReleaseRunner> logger)
    {
        _store = store;
        _automations = automations;
        _gitLab = gitLab;
        _lifetime = lifetime;
        _logger = logger;
    }

    public PublishJob Start(ContentReleaseRequest request, ContentReleaseContext context, string pilotUser, string? gitLabUser)
    {
        var stage = ContentReleases.StageEnv(context.Settings) ?? throw new InvalidOperationException("В настройках не задан сервер стейджа.");
        var jobKey = stage.Key;
        PublishJob job;
        lock (_startLock)
        {
            if (_store.FindActive(jobKey, request.Name) is { } active)
                throw new InvalidOperationException($"По «{request.Name}» на стейдже уже идёт операция (запустил {active.PilotUser}).");
            job = new PublishJob
            {
                Operation = Operation,
                Env = jobKey,
                EnvTitle = stage.Title,
                Branch = "dev / release",
                AutomationName = request.Name,
                CommitMessage = $"{request.Name}: релиз на стейдж",
                PilotUser = pilotUser,
                GitLabUser = gitLabUser,
            };
            job.Add("info", $"Релиз «{request.Name}»: ветка release от dev / сервер {stage.Title}.");
            _store.Add(job);
        }

        var ct = _lifetime.ApplicationStopping;
        _ = Task.Run(async () =>
        {
            try
            {
                await new ContentReleaseWorkflow(_automations, _gitLab.Create(context.GitLabToken), job, request, context).RunAsync(ct);
                job.Update(j => j.Status = JobStatus.Succeeded);
                job.Add("ok", "Готово: релиз на стейдже. Проверьте его и выпустите в прод («На прод»).");
            }
            catch (Exception ex) when (ex is WorkflowException or GitLabApiException or PilotGatewayException or OperationCanceledException)
            {
                job.Update(j => { j.Status = JobStatus.Failed; j.Error = ex.Message; });
                job.Add("error", ex is OperationCanceledException ? "Операция прервана остановкой сервиса." : ex.Message, details: (ex as WorkflowException)?.Details);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Сбой релиза на стейдж {JobId}", job.Id);
                job.Update(j => { j.Status = JobStatus.Failed; j.Error = ex.Message; });
                job.Add("error", $"Непредвиденная ошибка: {ex.Message}", details: ex.ToString());
            }
            finally
            {
                job.Update(j => j.FinishedAt = DateTimeOffset.UtcNow);
                _store.Persist();
            }
        });
        return job;
    }
}
