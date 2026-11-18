using ProAdmin.GitLab;
using ProAdmin.Jobs;
using ProAdmin.Settings;

namespace ProAdmin.Modules;

/// <summary>
/// Сборка модуля из GitLab CI: pipeline коммита / job сборки (настройка ModulesBuildJob) / zip модуля из артефактов.
/// На сервер уходит именно собранный CI артефакт: на стейдж и на прод - один и тот же, проверенный.
/// </summary>
internal sealed class ModuleArtifacts
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    private readonly GitLabClient _git;
    private readonly string _project;
    private readonly AdminSettings _settings;
    private readonly PublishJob _job;
    private readonly ModuleKind _kind;

    public ModuleArtifacts(GitLabClient git, string project, AdminSettings settings, PublishJob job, ModuleKind kind = ModuleKind.Client)
    {
        _kind = kind;
        _git = git;
        _project = project;
        _settings = settings;
        _job = job;
    }

    /// <summary>
    /// Ждёт успешный pipeline коммита <paramref name="sha"/> (для ветки/тега <paramref name="reference"/>) и возвращает
    /// разобранный zip модуля из job сборки. Если pipeline нет - запускает его для <paramref name="reference"/>.
    /// </summary>
    public async Task<ModulePackageInfo> BuildAsync(string reference, string sha, CancellationToken ct)
    {
        var pipeline = await WaitPipelineAsync(reference, sha, ct);
        var jobs = await _git.ListPipelineJobsAsync(_project, pipeline.Id, ct);
        var build = jobs.Where(j => j.Name == _settings.ModulesBuildJob).OrderByDescending(j => j.Id).FirstOrDefault()
                    ?? jobs.Where(j => j.Status == "success" && (j.ArtifactsFile is not null || j.Artifacts?.Any(a => a.FileType == "archive") == true))
                        .OrderByDescending(j => j.Id).FirstOrDefault()
                    ?? throw new WorkflowException(
                        $"В pipeline #{pipeline.Id} нет job «{_settings.ModulesBuildJob}» с артефактами. Проверьте .gitlab-ci.yml модуля (шаблон - в README вкладки «Модули»).",
                        string.Join("\n", jobs.Select(j => $"{j.Name}: {j.Status}")));
        if (build.Status != "success")
            throw new WorkflowException($"Job сборки «{build.Name}» в pipeline #{pipeline.Id}: {GitLabText.PipelineStatus(build.Status)}.");

        Add("info", $"Скачивание артефактов job «{build.Name}» (#{build.Id})…", build.WebUrl);
        var artifacts = await _git.DownloadJobArtifactsAsync(_project, build.Id, ct);
        try
        {
            var (zip, fileName) = ModulePackage.FromArtifacts(artifacts);
            var package = ModulePackage.Analyze(zip, fileName, _kind);
            Add("ok", $"Модуль из CI: {fileName} - {package.AssemblyName} {package.Version} ({ModulePackage.FormatSize(zip.LongLength)}).", build.WebUrl);
            return package;
        }
        catch (InvalidDataException ex)
        {
            throw new WorkflowException($"Артефакт job «{build.Name}» не подходит: {ex.Message}");
        }
    }

    private async Task<GitLabPipeline> WaitPipelineAsync(string reference, string sha, CancellationToken ct)
    {
        var started = DateTimeOffset.UtcNow;
        var startTimeout = TimeSpan.FromSeconds(_settings.PipelineStartTimeoutSeconds);
        var overall = TimeSpan.FromMinutes(_settings.PipelineTimeoutMinutes);
        var triggered = false;
        long? triggeredId = null;
        string? lastStatus = null;

        Add("info", $"Ожидание pipeline сборки для {reference} @ {ModuleDeploy.Short(sha)}…");
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            // Все pipeline именно этого коммита (в т.ч. pipeline MR: он собирает голову ветки-источника).
            var pipelines = (await _git.ListPipelinesForShaAsync(_project, sha, ct))
                .Where(p => p.Sha is null || string.Equals(p.Sha, sha, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(p => p.Id)
                .ToList();
            // Предпочитаем pipeline этой ветки/тега, затем любой успешный на том же коммите, затем последний.
            var current = pipelines.FirstOrDefault(p => p.Ref == reference && p.Status == "success")
                          ?? pipelines.FirstOrDefault(p => p.Status == "success")
                          ?? pipelines.FirstOrDefault(p => p.Ref == reference)
                          ?? pipelines.FirstOrDefault();
            var elapsed = DateTimeOffset.UtcNow - started;

            // Коммит с [skip ci] (например, правка nuget.config установщиком) оставляет pipeline «пропущен».
            // Запуск через API [skip ci] не учитывает, поэтому один раз запускаем сборку, а не считаем это провалом.
            if (current is { Status: "skipped" } && (!triggered || (triggeredId is { } id && current.Id < id)))
            {
                if (!triggered)
                {
                    triggered = true;
                    var created = await _git.CreatePipelineAsync(_project, reference, ct);
                    triggeredId = created.Id;
                    Add("info", $"Pipeline #{current.Id} коммита пропущен ([skip ci]) - запущен #{created.Id}.", created.WebUrl);
                }
                else if (elapsed > startTimeout)
                {
                    throw new WorkflowException($"Запущенный pipeline #{triggeredId} для {reference} не появился за {startTimeout.TotalSeconds:0} с.");
                }
                await Task.Delay(PollInterval, ct);
                continue;
            }

            if (current is null)
            {
                if (!triggered && elapsed > TimeSpan.FromSeconds(20))
                {
                    triggered = true;
                    var created = await _git.CreatePipelineAsync(_project, reference, ct);
                    Add("info", $"Pipeline для {reference} не найден - запущен #{created.Id}.", created.WebUrl);
                }
                else if (elapsed > startTimeout)
                {
                    throw new WorkflowException($"Pipeline для {reference} @ {ModuleDeploy.Short(sha)} не запустился за {startTimeout.TotalSeconds:0} с. " +
                                                "Проверьте .gitlab-ci.yml (rules для веток dev, release/*, main, hotfix/*) и runner.");
                }
            }
            else
            {
                if (current.Status != lastStatus)
                {
                    lastStatus = current.Status;
                    _job.Update(j => j.PipelineUrl ??= current.WebUrl);
                    Add(current.Status == "success" ? "ok" : current.Status is "failed" or "canceled" ? "error" : "info",
                        $"Pipeline #{current.Id} ({current.Ref}): {GitLabText.PipelineStatus(current.Status)}", current.WebUrl);
                }
                if (current.Status == "success")
                    return current;
                if (current.Status is "failed" or "canceled" or "skipped")
                {
                    await ReportFailedJobsAsync(current, ct);
                    throw new WorkflowException($"Сборка не прошла: pipeline #{current.Id} - {GitLabText.PipelineStatus(current.Status)}. Модуль НЕ выложен.");
                }
                if (current.Status == "manual")
                    throw new WorkflowException($"Pipeline #{current.Id} ждёт ручного запуска job в GitLab - запустите сборку и повторите.");
            }

            if (elapsed > overall)
                throw new WorkflowException($"Pipeline не завершился за {overall.TotalMinutes:0} мин. Модуль НЕ выложен.");
            _job.Update(j => j.Status = JobStatus.WaitingPipeline);
            await Task.Delay(PollInterval, ct);
            _job.Update(j => j.Status = JobStatus.Running);
        }
    }

    private async Task ReportFailedJobsAsync(GitLabPipeline pipeline, CancellationToken ct)
    {
        try
        {
            foreach (var job in (await _git.ListFailedJobsAsync(_project, pipeline.Id, ct)).Where(j => !j.AllowFailure).Take(3))
            {
                string? tail = null;
                try { tail = GitLabText.TraceTail(await _git.GetJobTraceAsync(_project, job.Id, ct), 40); }
                catch (GitLabApiException) { /* лог недоступен - ссылки достаточно */ }
                Add("error", $"Job «{job.Name}» (stage {job.Stage}) завершился с ошибкой", job.WebUrl, tail);
            }
        }
        catch (GitLabApiException ex)
        {
            Add("warn", $"Не удалось получить список упавших job: {ex.Message}");
        }
    }

    private void Add(string level, string text, string? url = null, string? details = null) => _job.Add(level, text, url, details);
}
