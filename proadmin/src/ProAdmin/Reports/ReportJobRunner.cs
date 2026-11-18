using ProAdmin.GitLab;
using ProAdmin.Jobs;
using ProAdmin.Pilot;

namespace ProAdmin.Reports;

/// <summary>Запускает перенос отчёта на прод (и хотфикса с прода в тест) в фоне; журнал - общий с автоматизациями (вкладка «Операции»).</summary>
public sealed class ReportJobRunner
{
    public const string PromoteOperation = "перенос отчёта на прод";
    public const string RollbackOperation = "откат отчёта на прод";
    public const string BackportOperation = "перенос отчёта с прода в тест";

    private readonly JobStore _store;
    private readonly PilotReportGateway _pilot;
    private readonly GitLabClientFactory _gitLab;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<ReportJobRunner> _logger;
    private readonly object _startLock = new();

    public ReportJobRunner(JobStore store, PilotReportGateway pilot, GitLabClientFactory gitLab,
        IHostApplicationLifetime lifetime, ILogger<ReportJobRunner> logger)
    {
        _store = store;
        _pilot = pilot;
        _gitLab = gitLab;
        _lifetime = lifetime;
        _logger = logger;
    }

    public PublishJob StartPromote(ReportPromoteRequest request, ReportPromoteContext context, string pilotUser, string? gitLabUser)
    {
        var prod = ReportCatalog.MainEnv(context.Settings)
                   ?? throw new InvalidOperationException("В настройках нет контура с сервером Pilot и веткой main.");
        var jobKey = "report:" + prod.Key;
        PublishJob job;
        lock (_startLock)
        {
            if (_store.FindActive(jobKey, request.Name) is { } active)
                throw new InvalidOperationException($"По отчёту «{request.Name}» уже идёт перенос (запустил {active.PilotUser}).");

            job = new PublishJob
            {
                Operation = PromoteOperation,
                Env = jobKey,
                EnvTitle = $"{prod.Title} (отчёты)",
                Branch = $"{request.Source} / main",
                AutomationName = request.Name,
                CommitMessage = request.CommitMessage,
                ChangeNotes = request.ChangeNotes.ToList(),
                PilotUser = pilotUser,
                GitLabUser = gitLabUser,
            };
            job.Add("info", $"Перенос отчёта «{request.Name}»: ветка {request.Source} / ветка main / сервер {prod.Title}.");
            _store.Add(job);
        }

        var ct = _lifetime.ApplicationStopping;
        _ = Task.Run(() => ExecuteAsync(job,
            () => new ReportPromoteWorkflow(_pilot, _gitLab.Create(context.GitLabToken), job, request, context).RunAsync(ct),
            "Готово: ветка main и продовый сервер обновлены."));
        return job;
    }

    public PublishJob StartRollback(ReportRollbackRequest request, ReportPromoteContext context, string pilotUser, string? gitLabUser)
    {
        var prod = ReportCatalog.MainEnv(context.Settings)
                   ?? throw new InvalidOperationException("В настройках нет контура с сервером Pilot и веткой main.");
        var jobKey = "report:" + prod.Key;
        PublishJob job;
        lock (_startLock)
        {
            if (_store.FindActive(jobKey, request.Name) is { } active)
                throw new InvalidOperationException($"По отчёту «{request.Name}» уже идёт операция (запустил {active.PilotUser}).");

            job = new PublishJob
            {
                Operation = RollbackOperation,
                Env = jobKey,
                EnvTitle = $"{prod.Title} (отчёты)",
                Branch = $"{request.Tag} / main",
                AutomationName = request.Name,
                CommitMessage = request.CommitMessage,
                ChangeNotes = request.Reasons.ToList(),
                PilotUser = pilotUser,
                GitLabUser = gitLabUser,
            };
            job.Add("info", $"Откат отчёта «{request.Name}» на релиз {request.Tag}: ветка main / сервер {prod.Title}.");
            _store.Add(job);
        }

        var ct = _lifetime.ApplicationStopping;
        _ = Task.Run(() => ExecuteAsync(job,
            () => new ReportRollbackWorkflow(_pilot, _gitLab.Create(context.GitLabToken), job, request, context).RunAsync(ct),
            $"Готово: прод возвращён к {request.Tag} новой версией."));
        return job;
    }

    public PublishJob StartBackport(ReportBackportRequest request, ReportBackportContext context, string pilotUser, string? gitLabUser)
    {
        var test = ReportCatalog.DevEnv(context.Settings)
                   ?? throw new InvalidOperationException("В настройках нет контура с сервером Pilot и веткой dev.");
        var jobKey = "report:" + test.Key;
        PublishJob job;
        lock (_startLock)
        {
            if (_store.FindActive(jobKey, request.Name) is { } active)
                throw new InvalidOperationException($"По отчёту «{request.Name}» уже идёт операция (запустил {active.PilotUser}).");

            job = new PublishJob
            {
                Operation = BackportOperation,
                Env = jobKey,
                EnvTitle = $"{test.Title} (отчёты)",
                Branch = "main / dev",
                AutomationName = request.Name,
                CommitMessage = request.CommitMessage,
                PilotUser = pilotUser,
                GitLabUser = gitLabUser,
            };
            job.Add("info", $"Перенос хотфикса отчёта «{request.Name}» с прода: ветка main / ветка dev / сервер {test.Title}.");
            _store.Add(job);
        }

        var ct = _lifetime.ApplicationStopping;
        _ = Task.Run(() => ExecuteAsync(job,
            () => new ReportBackportWorkflow(_pilot, _gitLab.Create(context.GitLabToken), job, request, context).RunAsync(ct),
            "Готово: ветка dev и тестовый сервер содержат версию с прода."));
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
            _logger.LogError(ex, "Сбой переноса отчёта {JobId}", job.Id);
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
