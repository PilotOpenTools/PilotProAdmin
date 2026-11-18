using ProAdmin.GitLab;
using ProAdmin.Jobs;
using ProAdmin.Pilot;
using ProAdmin.Types;

namespace ProAdmin.Modules;

/// <summary>Запуск операций над модулями в фоне; журнал - общий (вкладка «Операции»).</summary>
public sealed class ModuleJobRunner
{
    private readonly JobStore _store;
    private readonly ModuleTargets _targets;
    private readonly GitLabClientFactory _gitLab;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<ModuleJobRunner> _logger;
    private readonly object _startLock = new();

    public ModuleJobRunner(JobStore store, ModuleTargets targets, GitLabClientFactory gitLab,
        IHostApplicationLifetime lifetime, ILogger<ModuleJobRunner> logger)
    {
        _store = store;
        _targets = targets;
        _gitLab = gitLab;
        _lifetime = lifetime;
        _logger = logger;
    }

    /// <summary>Выложить ветку/тег на контур.</summary>
    public PublishJob StartDeploy(ModuleContext ctx, string env, string reference, IReadOnlyList<string> notes, string pilotUser, string? gitLabUser, bool isTag = false) =>
        Start(ctx, env, isTag ? "откат модуля" : "выкладка модуля", $"{reference} / {EnvTitle(ctx, env)}", notes, pilotUser, gitLabUser,
            (job, git) => new ModuleDeployWorkflow(_targets.Get(ctx.Kind), git, job, ctx, env, reference, notes, isTag),
            $"Готово: {reference} на сервере {EnvTitle(ctx, env)}.");

    public PublishJob StartCiUpdate(ModuleContext ctx, string content, string pilotUser, string? gitLabUser) =>
        Start(ctx, ModuleEnv.Test, "обновление шаблона CI", $"ci/template / {ModuleBranches.Dev}", Array.Empty<string>(), pilotUser, gitLabUser,
            (job, git) => new ModuleCiUpdateWorkflow(_targets.Get(ctx.Kind), git, job, ctx, content),
            "Готово: шаблон CI в dev обновлён.");

    public PublishJob StartMergeToDev(ModuleContext ctx, long iid, string source, string pilotUser, string? gitLabUser, string? skipReview = null) =>
        Start(ctx, ModuleEnv.Test, skipReview is null ? "задача / dev / тест" : "задача / dev / тест (без ревью)", $"{source} / {ModuleBranches.Dev}",
            skipReview is null ? Array.Empty<string>() : new[] { "без Code Review: " + skipReview }, pilotUser, gitLabUser,
            (job, git) => new ModuleMergeToDevWorkflow(_targets.Get(ctx.Kind), git, job, ctx, iid, skipReview),
            "Готово: задача в dev и на тестовом сервере.");

    public PublishJob StartPrepareRelease(ModuleContext ctx, ModuleVersion version, IReadOnlyCollection<string>? selected, IReadOnlyList<string> notes,
        string pilotUser, string? gitLabUser) =>
        Start(ctx, ModuleEnv.Stage, "релиз / стейдж", $"{ModuleBranches.Dev} / {ModuleBranches.Release(version)} / {EnvTitle(ctx, ModuleEnv.Stage)}", notes, pilotUser, gitLabUser,
            (job, git) => new ModulePrepareReleaseWorkflow(_targets.Get(ctx.Kind), git, job, ctx, version, selected, notes),
            $"Готово: релиз {version} на стейдже.");

    public PublishJob StartAddToRelease(ModuleContext ctx, string release, IReadOnlyCollection<string> selected, string pilotUser, string? gitLabUser) =>
        Start(ctx, ModuleEnv.Stage, "задачи / релиз / стейдж", $"{ModuleBranches.Dev} / {release} / {EnvTitle(ctx, ModuleEnv.Stage)}", Array.Empty<string>(), pilotUser, gitLabUser,
            (job, git) => new ModuleAddToReleaseWorkflow(_targets.Get(ctx.Kind), git, job, ctx, release, selected),
            $"Готово: задачи добавлены в {release}, релиз на стейдже.");

    public PublishJob StartReleaseToStage(ModuleContext ctx, string release, long? mrIid, string pilotUser, string? gitLabUser) =>
        Start(ctx, ModuleEnv.Stage, "релиз / стейдж", $"{release} / {EnvTitle(ctx, ModuleEnv.Stage)}", Array.Empty<string>(), pilotUser, gitLabUser,
            (job, git) => new ModuleReleaseToStageWorkflow(_targets.Get(ctx.Kind), git, job, ctx, release, mrIid),
            $"Готово: {release} на стейдже.");

    public PublishJob StartReleaseToProd(ModuleContext ctx, string release, string? task, bool allowUntested, string pilotUser, string? gitLabUser) =>
        Start(ctx, ModuleEnv.Prod, "релиз / прод", $"{release} / {ModuleBranches.Main}",
            task is null ? Array.Empty<string>() : new[] { task }, pilotUser, gitLabUser,
            (job, git) => new ModuleReleaseToProdWorkflow(_targets.Get(ctx.Kind), git, job, ctx, release, task, allowUntested),
            $"Готово: {release} выпущен в прод.");

    public PublishJob StartHotfix(ModuleContext ctx, string branch, IReadOnlyList<string> notes, string? activeRelease, string pilotUser, string? gitLabUser) =>
        Start(ctx, ModuleEnv.Prod, "hotfix / прод", $"{branch} / {ModuleBranches.Main}", notes, pilotUser, gitLabUser,
            (job, git) => new ModuleHotfixWorkflow(_targets.Get(ctx.Kind), git, job, ctx, branch, notes, activeRelease),
            $"Готово: {branch} на проде, ветки синхронизированы.");

    private static string EnvTitle(ModuleContext ctx, string env) => ModuleEnv.Find(ctx.Settings, env)?.Title ?? env;

    private PublishJob Start(ModuleContext ctx, string env, string operation, string branchText, IReadOnlyList<string> notes,
        string pilotUser, string? gitLabUser, Func<PublishJob, GitLabClient, ModuleWorkflowBase> create, string doneText)
    {
        var jobKey = ctx.Kind.JobEnv(env);
        PublishJob job;
        lock (_startLock)
        {
            if (_store.FindActive(jobKey, ctx.RepoName) is { } active)
                throw new InvalidOperationException($"По модулю «{ctx.RepoName}» на контуре {EnvTitle(ctx, env)} уже идёт операция «{active.Operation}» (запустил {active.PilotUser}).");

            job = new PublishJob
            {
                Operation = operation,
                Env = jobKey,
                EnvTitle = ctx.Kind.JobEnvTitle(EnvTitle(ctx, env)),
                Branch = branchText,
                AutomationName = ctx.RepoName,
                CommitMessage = operation,
                ChangeNotes = notes.ToList(),
                PilotUser = pilotUser,
                GitLabUser = gitLabUser,
            };
            job.Add("info", $"{(ctx.Kind == ModuleKind.Server ? "Серверный модуль" : "Модуль")} «{ctx.RepoName}»: {operation} ({branchText}).");
            _store.Add(job);
        }

        var ct = _lifetime.ApplicationStopping;
        var workflow = create(job, _gitLab.Create(ctx.GitLabToken));
        _ = Task.Run(() => ExecuteAsync(job, () => workflow.RunAsync(ct), doneText));
        return job;
    }

    private async Task ExecuteAsync(PublishJob job, Func<Task> run, string doneText)
    {
        try
        {
            await run();
            job.Update(j => j.Status = JobStatus.Succeeded);
            job.Add("ok", doneText);
        }
        catch (WorkflowException ex)
        {
            Fail(job, ex.Message, ex.Details);
        }
        catch (Exception ex) when (ex is GitLabApiException or PilotGatewayException)
        {
            Fail(job, ex.Message, null);
        }
        catch (OperationCanceledException)
        {
            Fail(job, "Операция прервана остановкой сервиса.", null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Сбой операции над модулем {JobId}", job.Id);
            Fail(job, $"Непредвиденная ошибка: {ex.Message}", ex.ToString());
        }
        finally
        {
            job.Update(j => j.FinishedAt = DateTimeOffset.UtcNow);
            _store.Persist();
        }
    }

    private static void Fail(PublishJob job, string message, string? details)
    {
        job.Update(j =>
        {
            j.Status = JobStatus.Failed;
            j.Error = message;
        });
        job.Add("error", message, details: details);
    }
}
