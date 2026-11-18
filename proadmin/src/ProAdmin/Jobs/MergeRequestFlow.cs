using ProAdmin.GitLab;
using ProAdmin.Settings;

namespace ProAdmin.Jobs;

/// <summary>
/// Общая часть сценариев отправки через Merge Request: ожидание pipeline GitLab и merge.
/// Используется и для автоматизаций, и для отчётов.
/// </summary>
internal sealed class MergeRequestFlow
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    private readonly GitLabClient _git;
    private readonly string _project;
    private readonly PublishJob _job;
    private readonly AdminSettings _settings;
    private readonly string _notSaved;

    /// <param name="notSaved">Хвост сообщений об отказе, например «Автоматизация НЕ сохранена на сервере.»</param>
    public MergeRequestFlow(GitLabClient git, string project, PublishJob job, AdminSettings settings, string notSaved)
    {
        _git = git;
        _project = project;
        _job = job;
        _settings = settings;
        _notSaved = notSaved;
    }

    private string Project => _project;

    public async Task WaitForPipelinesAsync(long mrIid, string sha, CancellationToken ct)
    {
        var started = DateTimeOffset.UtcNow;
        var startTimeout = TimeSpan.FromSeconds(_settings.PipelineStartTimeoutSeconds);
        var overallTimeout = TimeSpan.FromMinutes(_settings.PipelineTimeoutMinutes);
        var seen = new Dictionary<long, string>();
        string? previousFinishedSet = null;

        Info("Ожидание pipeline GitLab…");
        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var all = await _git.ListMergeRequestChecksAsync(Project, mrIid, sha, ct);

            foreach (var p in all.OrderBy(p => p.Id))
            {
                if (seen.TryGetValue(p.Id, out var status) && status == p.Status)
                    continue;
                seen[p.Id] = p.Status;
                _job.Update(j => j.PipelineUrl ??= p.WebUrl);
                Add(p.Status == "success" ? "ok" : p.Status is "failed" or "canceled" ? "error" : "info",
                    $"Pipeline #{p.Id} ({SourceRu(p.Source)}): {GitLabText.PipelineStatus(p.Status)}", p.WebUrl);
            }

            var latest = MergeRequestChecks.Latest(all);
            var elapsed = DateTimeOffset.UtcNow - started;

            if (latest.Count == 0)
            {
                if (elapsed > startTimeout)
                    throw new WorkflowException(
                        $"Pipeline не запустился за {startTimeout.TotalSeconds:0} с. Проверьте, что в репозитории есть .gitlab-ci.yml, " +
                        $"его rules срабатывают для веток/Merge Request и runner доступен. {_notSaved}");
            }
            else if (latest.All(p => p.IsFinished))
            {
                var failed = latest.Where(p => p.Status != "success").ToList();
                if (failed.Count > 0)
                {
                    await ReportFailedJobsAsync(failed[0], ct);
                    throw new WorkflowException(
                        $"Проверки GitLab не пройдены: pipeline #{failed[0].Id} - {GitLabText.PipelineStatus(failed[0].Status)}. {_notSaved}");
                }

                // Pipeline MR создаётся чуть позже push-pipeline: успех засчитываем, когда набор стабилен два опроса подряд.
                var set = string.Join(",", latest.Select(p => p.Id).OrderBy(id => id));
                if (set == previousFinishedSet)
                {
                    Ok("Все проверки GitLab пройдены.");
                    return;
                }
                previousFinishedSet = set;
            }
            else
            {
                previousFinishedSet = null;
            }

            if (elapsed > overallTimeout)
                throw new WorkflowException($"Pipeline не завершился за {overallTimeout.TotalMinutes:0} мин. {_notSaved}");

            await Task.Delay(PollInterval, ct);
        }
    }

    private async Task ReportFailedJobsAsync(GitLabPipeline pipeline, CancellationToken ct)
    {
        try
        {
            var jobs = await _git.ListFailedJobsAsync(Project, pipeline.Id, ct);
            foreach (var job in jobs.Where(j => !j.AllowFailure).Take(3))
            {
                string? tail = null;
                try { tail = GitLabText.TraceTail(await _git.GetJobTraceAsync(Project, job.Id, ct), 40); }
                catch (GitLabApiException) { /* лог недоступен - ссылки достаточно */ }
                Add("error", $"Job «{job.Name}» (stage {job.Stage}) завершился с ошибкой", job.WebUrl, tail);
            }
        }
        catch (GitLabApiException ex)
        {
            Add("warn", $"Не удалось получить список упавших job: {ex.Message}");
        }
    }

    /// <param name="squashMessage">Не null - squash-merge с этим сообщением итогового коммита.</param>
    public async Task MergeAsync(long mrIid, string sha, string branch, CancellationToken ct, bool removeSourceBranch = true, string? squashMessage = null)
    {
        var deadline = DateTimeOffset.UtcNow.AddMinutes(3);
        while (true)
        {
            try
            {
                var merged = squashMessage is null
                    ? await _git.MergeAsync(Project, mrIid, sha, ct, removeSourceBranch)
                    : await _git.SquashMergeAsync(Project, mrIid, sha, squashMessage, ct, removeSourceBranch);
                Ok($"Merge Request !{mrIid} влит в {branch}.", merged.WebUrl);
                return;
            }
            catch (GitLabApiException ex) when (ex.StatusCode is 405 or 406 or 422)
            {
                var state = await _git.GetMergeRequestAsync(Project, mrIid, ct);
                if (state.State == "merged")
                {
                    Ok($"Merge Request !{mrIid} влит в {branch}.", state.WebUrl);
                    return;
                }

                var transient = state.DetailedMergeStatus is null or "checking" or "unchecked" or "preparing"
                    or "ci_still_running" or "ci_must_pass" or "approvals_syncing" or "mergeable";
                if (!transient || DateTimeOffset.UtcNow > deadline)
                    throw new WorkflowException($"{ex.Message} (состояние MR: {GitLabText.MergeStatus(state.DetailedMergeStatus)})");

                await Task.Delay(PollInterval, ct);
            }
        }
    }

    private static string SourceRu(string? source) => source switch
    {
        "push" => "ветка",
        "merge_request_event" => "merge request",
        _ => source ?? "?",
    };

    private void Info(string text, string? url = null) => Add("info", text, url);
    private void Ok(string text, string? url = null) => Add("ok", text, url);
    private void Add(string level, string text, string? url = null, string? details = null) => _job.Add(level, text, url, details);
}
