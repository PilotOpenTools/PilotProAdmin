using ProAdmin.GitLab;
using ProAdmin.Jobs;
using ProAdmin.Modules;
using ProAdmin.Pilot;
using ProAdmin.Settings;

namespace ProAdmin.Types;

/// <summary>Операции над типами в фоне; журнал - общий (вкладка «Операции»).</summary>
public sealed class TypesJobRunner
{
    public const string JobName = "Типы";

    private readonly JobStore _jobs;
    private readonly PilotGateway _pilot;
    private readonly TypesStore _store;
    private readonly GitLabClientFactory _gitLab;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<TypesJobRunner> _logger;
    private readonly object _startLock = new();

    public TypesJobRunner(JobStore jobs, PilotGateway pilot, TypesStore store, GitLabClientFactory gitLab,
        IHostApplicationLifetime lifetime, ILogger<TypesJobRunner> logger)
    {
        _jobs = jobs;
        _pilot = pilot;
        _store = store;
        _gitLab = gitLab;
        _lifetime = lifetime;
        _logger = logger;
    }

    public PublishJob StartMergeBranch(TypesContext ctx, string branch, string message, string pilotUser, string? gitLabUser) =>
        Start(ctx, TypesEnv.Test, "типы: ветка / dev / тест", $"{branch} / {TypesBranches.Dev}", new[] { message }, pilotUser, gitLabUser,
            (job, git) => new TypesMergeBranchWorkflow(_pilot, git, job, ctx, _store, branch, message),
            "Готово: изменения в dev и на тестовом сервере.");

    public PublishJob StartSync(TypesContext ctx, string branch, string pilotUser, string? gitLabUser) =>
        Start(ctx, TypesBranches.Env(branch), "типы: запись ветки на сервер", $"{branch} / {EnvTitle(ctx, TypesBranches.Env(branch))}", Array.Empty<string>(),
            pilotUser, gitLabUser,
            (job, git) => new TypesSyncWorkflow(_pilot, git, job, ctx, _store, branch),
            $"Готово: сервер {EnvTitle(ctx, TypesBranches.Env(branch))} совпадает с {branch}.");

    public PublishJob StartPromote(TypesContext ctx, string target, IReadOnlyCollection<string> selected, ModuleVersion? version,
        IReadOnlyList<string> notes, string? task, string pilotUser, string? gitLabUser)
    {
        var source = TypesBranches.SourceOf(target) ?? target;
        var env = TypesBranches.Env(target);
        return Start(ctx, env, $"типы: перенос {source} / {target}",
            $"{source} / {target}" + (version is null ? "" : $" ({version})"), notes, pilotUser, gitLabUser,
            (job, git) => new TypesPromoteWorkflow(_pilot, git, job, ctx, _store, target, selected, version, notes, task),
            $"Готово: {target} на сервере {EnvTitle(ctx, env)}.");
    }

    public PublishJob StartInit(TypesContext ctx, string sourceEnv, string pilotUser, string? gitLabUser) =>
        Start(ctx, TypesEnv.Prod, "типы: инициализация репозитория", $"{EnvTitle(ctx, sourceEnv)} / {TypesBranches.Main}", Array.Empty<string>(),
            pilotUser, gitLabUser,
            (job, git) => new TypesInitWorkflow(_pilot, git, job, ctx, _store, sourceEnv),
            "Готово: репозиторий типов заполнен.");

    public PublishJob StartGitHubSync(AdminSettings settings, GitHubTypesClient github, string repo, long pull,
        string token, PilotLogin login, string pilotUser) =>
        Start(settings, TypesEnv.Test, "типы: GitHub / тест", $"dev (PR #{pull})", Array.Empty<string>(), pilotUser, null,
            async (job, ct) =>
            {
                if (TypesEnv.Find(settings, TypesEnv.Test) is not { HasServer: true } env)
                    throw new WorkflowException("Задайте сервер Pilot для тестового контура.");

                GitHubTypesClient.ApprovedRevision approved;
                TypesSnapshot snapshot;
                try
                {
                    approved = await github.VerifyAsync(repo, pull, token, ct);
                    job.Update(j => { j.MergeRequestUrl = approved.PullUrl; j.PipelineUrl = approved.RunUrl; });
                    job.Add("ok", $"Pull Request #{pull} влит, CI для {approved.Sha[..8]} прошёл.", approved.RunUrl);
                    snapshot = await github.ReadSnapshotAsync(repo, approved.Sha, token, ct);
                    if (snapshot.IsEmpty)
                        throw new WorkflowException("Архив GitHub не содержит типов, состояний или групп состояний.");
                    var validation = TypesValidator.Validate(snapshot);
                    if (!validation.Ok)
                        throw new WorkflowException("Проверка типов не прошла. Pilot не изменён.", string.Join("\n", validation.Errors));
                    await github.CheckHeadAsync(repo, approved.Sha, token, ct);
                }
                catch (Exception ex) when ((ex is HttpRequestException or System.Text.Json.JsonException or InvalidDataException
                    or KeyNotFoundException or InvalidOperationException or FormatException or TaskCanceledException)
                    && !ct.IsCancellationRequested)
                {
                    // Ссылка codeload может содержать временный токен.
                    throw new WorkflowException("Не удалось проверить ответ GitHub или прочитать архив. Pilot не изменён.");
                }

                var plan = await _pilot.ApplyTypesAsync(env.PilotUrl, login, snapshot, ct);
                if (!plan.CanApply)
                    throw new WorkflowException("Запись типов на тестовый Pilot невозможна.", string.Join("\n", plan.Errors));
                foreach (var warning in plan.Warnings.Take(10))
                    job.Add("warn", warning);
                _store.RecordDeployment(new TypesDeployment
                {
                    Env = env.Key, Reference = TypesBranches.Dev, Sha = approved.Sha, Version = snapshot.Config.Version,
                    At = DateTimeOffset.UtcNow, By = pilotUser, Changes = plan.Changes.Count,
                });
                job.Add("ok", $"Записано изменений: {plan.Changes.Count}.",
                    details: string.Join("\n", plan.Changes.Select(c => c.Line)));
            },
            "Готово: проверенный коммит GitHub записан на тестовый Pilot.");

    private static string EnvTitle(TypesContext ctx, string env) => EnvTitle(ctx.Settings, env);

    private static string EnvTitle(AdminSettings settings, string env) => TypesEnv.Find(settings, env)?.Title ?? env;

    private PublishJob Start(TypesContext ctx, string env, string operation, string branchText, IReadOnlyList<string> notes,
        string pilotUser, string? gitLabUser, Func<PublishJob, GitLabClient, TypesWorkflowBase> create, string doneText) =>
        Start(ctx.Settings, env, operation, branchText, notes, pilotUser, gitLabUser,
            (job, ct) => create(job, _gitLab.Create(ctx.GitLabToken)).RunAsync(ct), doneText);

    private PublishJob Start(AdminSettings settings, string env, string operation, string branchText, IReadOnlyList<string> notes,
        string pilotUser, string? gitLabUser, Func<PublishJob, CancellationToken, Task> run, string doneText)
    {
        var jobKey = "types:" + env;
        PublishJob job;
        lock (_startLock)
        {
            if (_jobs.FindActive(jobKey, JobName) is { } active)
                throw new InvalidOperationException($"С типами на контуре {EnvTitle(settings, env)} уже идёт операция «{active.Operation}» (запустил {active.PilotUser}).");

            job = new PublishJob
            {
                Operation = operation,
                Env = jobKey,
                EnvTitle = $"{EnvTitle(settings, env)} (типы)",
                Branch = branchText,
                AutomationName = JobName,
                CommitMessage = operation,
                ChangeNotes = notes.ToList(),
                PilotUser = pilotUser,
                GitLabUser = gitLabUser,
            };
            job.Add("info", $"Типы: {operation} ({branchText}).");
            _jobs.Add(job);
        }

        _ = Task.Run(() => ExecuteAsync(job, () => run(job, _lifetime.ApplicationStopping), doneText));
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
        catch (Exception ex) when (ex is GitLabApiException or PilotGatewayException or TypesFormatException)
        {
            Fail(job, ex.Message, null);
        }
        catch (OperationCanceledException)
        {
            Fail(job, "Операция прервана остановкой сервиса.", null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Сбой операции над типами {JobId}", job.Id);
            Fail(job, $"Непредвиденная ошибка: {ex.Message}", ex.ToString());
        }
        finally
        {
            job.Update(j => j.FinishedAt = DateTimeOffset.UtcNow);
            _jobs.Persist();
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
