using ProAdmin.GitLab;
using ProAdmin.Pilot;

namespace ProAdmin.Jobs;

/// <summary>Запускает операции отправки в фоне; одна активная операция на автоматизацию в контуре.</summary>
public sealed class PublishJobRunner
{
    private readonly JobStore _store;
    private readonly PilotGateway _pilot;
    private readonly Pilot.ServerModules.PilotServerModuleGateway _modules;
    private readonly GitLabClientFactory _gitLab;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<PublishJobRunner> _logger;
    private readonly object _startLock = new();

    public PublishJobRunner(JobStore store, PilotGateway pilot, Pilot.ServerModules.PilotServerModuleGateway modules, GitLabClientFactory gitLab,
        IHostApplicationLifetime lifetime, ILogger<PublishJobRunner> logger)
    {
        _store = store;
        _pilot = pilot;
        _modules = modules;
        _gitLab = gitLab;
        _lifetime = lifetime;
        _logger = logger;
    }

    public PublishJob Start(PublishRequest request, PublishContext context, string pilotUser, string? gitLabUser, string? operation = null)
    {
        var env = context.Settings.Find(request.Env)!;
        PublishJob job;
        lock (_startLock)
        {
            if (_store.FindActive(request.Env, request.Name) is { } active)
                throw new InvalidOperationException($"По «{request.Name}» в контуре {env.Title} уже идёт операция (запущена {active.PilotUser}).");

            job = new PublishJob
            {
                IsDelete = request.Delete,
                Operation = operation,
                Env = env.Key,
                EnvTitle = env.Title,
                Branch = env.Branch,
                AutomationName = request.Name,
                CommitMessage = request.CommitMessage,
                ChangeNotes = request.ChangeNotes.ToList(),
                PilotUser = pilotUser,
                GitLabUser = gitLabUser,
            };
            job.Add("info", request.Delete
                ? $"Удаление «{request.Name}»: ветка {env.Branch} / сервер {env.Title}."
                : $"Отправка «{request.Name}»: ветка {env.Branch} / сервер {env.Title}.");
            _store.Add(job);
        }

        _ = Task.Run(() => ExecuteAsync(job, request, context, _lifetime.ApplicationStopping));
        return job;
    }

    private async Task ExecuteAsync(PublishJob job, PublishRequest request, PublishContext context, CancellationToken ct)
    {
        try
        {
            var workflow = new PublishWorkflow(_pilot, _modules, _gitLab.Create(context.GitLabToken), job, request, context);
            await workflow.RunAsync(ct);
            job.Update(j => j.Status = JobStatus.Succeeded);
            job.Add("ok", request.Delete ? "Готово: автоматизация удалена." : "Готово: ветка и сервер обновлены.");
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
            _logger.LogError(ex, "Сбой операции {JobId}", job.Id);
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
