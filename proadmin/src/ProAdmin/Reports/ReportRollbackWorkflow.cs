using ProAdmin.Automations;
using ProAdmin.GitLab;
using ProAdmin.Jobs;
using ProAdmin.Pilot;
using ProAdmin.Settings;

namespace ProAdmin.Reports;

/// <summary>Откат отчёта на прод: какой релиз вернуть и почему.</summary>
public sealed class ReportRollbackRequest
{
    public required string Name { get; init; }

    /// <summary>Тег релиза, содержимое которого возвращается (v2026.09.20).</summary>
    public required string Tag { get; init; }

    public required string CommitMessage { get; init; }

    /// <summary>Причина отката - пункты раздела CHANGELOG.md.</summary>
    public IReadOnlyList<string> Reasons { get; init; } = Array.Empty<string>();
    public string? TaskReference { get; init; }
}

/// <summary>
/// Откат отчёта на прод: .repx выбранного релиза уходит в main <b>новой версией</b> (номер - сегодняшняя дата),
/// обычным путём: временная ветка / MR в main / pipeline / запись на продовый сервер / merge / релиз.
/// Номера только растут, в истории видно «2026.09.27.2 - откат на v2026.09.20». Ветка dev не меняется.
/// </summary>
internal sealed class ReportRollbackWorkflow
{
    private const string Main = AutomationRepositories.MainBranch;
    private const string NotSaved = "Отчёт НЕ изменён на продовом сервере.";

    private readonly PilotReportGateway _pilot;
    private readonly GitLabClient _git;
    private readonly PublishJob _job;
    private readonly ReportRollbackRequest _req;
    private readonly ReportPromoteContext _ctx;
    private readonly EnvironmentSettings _prod;

    public ReportRollbackWorkflow(PilotReportGateway pilot, GitLabClient git, PublishJob job, ReportRollbackRequest request, ReportPromoteContext context)
    {
        _pilot = pilot;
        _git = git;
        _job = job;
        _req = request;
        _ctx = context;
        _prod = ReportCatalog.MainEnv(context.Settings)
                ?? throw new WorkflowException("В настройках нет контура с сервером Pilot и веткой main.");
    }

    private string Project => _ctx.ProjectPath;
    private int License => _ctx.Settings.ReportsLicenseType;

    private IReadOnlyList<string> Notes => new[] { $"Откат на {_req.Tag}: {string.Join("; ", _req.Reasons)}" };

    public async Task RunAsync(CancellationToken ct)
    {
        // 1. Релиз и ветка main
        var releases = await ReportVersion.ListReleasesAsync(_git, Project, ct);
        if (releases.All(r => r.Tag.Name != _req.Tag))
            throw new WorkflowException($"Релиз {_req.Tag} не найден в {Project}.");
        var tagTree = await _git.ListTreeAsync(Project, _req.Tag, ct) ?? throw new WorkflowException($"Не удалось прочитать релиз {_req.Tag}.");
        var tagPath = ReportFiles.FindPath(tagTree, _ctx.RepoName) ?? throw new WorkflowException($"В релизе {_req.Tag} нет файла .repx отчёта.");
        var tagBytes = await _git.GetRawFileAsync(Project, tagPath, _req.Tag, ct) ?? throw new WorkflowException($"Не удалось прочитать {tagPath} из {_req.Tag}.");

        var mainTree = await _git.ListTreeAsync(Project, Main, ct) ?? throw new WorkflowException($"В {Project} нет ветки {Main}.");
        var mainPath = ReportFiles.FindPath(mainTree, _ctx.RepoName);
        var mainBytes = mainPath is null ? null : await _git.GetRawFileAsync(Project, mainPath, Main, ct);

        var version = ReportVersion.Next(DateTimeOffset.Now, releases.Select(r => r.Tag.Name));
        Info($"Откат на {_req.Tag}: новый номер версии {version} (релиз {ReportVersion.TagName(version)}).");

        // 2. Продовый сервер
        Info($"Чтение отчётов сервера {_prod.Title}…");
        var prodList = await _pilot.ListAsync(_prod.PilotUrl, _ctx.ProdLogin, License, withContent: false, ct);
        var key = ProjectNames.Key(_ctx.RepoName);
        var prodReport = prodList.Reports.FirstOrDefault(r => ProjectNames.Key(r.Name) == key)
                         ?? throw new WorkflowException($"Отчёта «{_ctx.RepoName}» нет на сервере {_prod.Title} - откатывать нечего.");
        var prodBytes = await _pilot.DownloadAsync(_prod.PilotUrl, _ctx.ProdLogin, License, prodReport.Id, ct);

        if (ReportFiles.SameContent(tagBytes, mainBytes) && ReportFiles.SameContent(tagBytes, prodBytes))
            throw new WorkflowException($"В {Main} и на сервере {_prod.Title} уже содержимое {_req.Tag} - откатывать нечего.");

        // 3. Коммит во временную ветку: .repx релиза + раздел CHANGELOG.md
        var path = mainPath ?? tagPath;
        var actions = new List<GitLabCommitAction>();
        if (!ReportFiles.SameContent(tagBytes, mainBytes))
            actions.Add(mainPath is null ? GitLabCommitAction.CreateBinary(path, tagBytes) : GitLabCommitAction.UpdateBinary(path, tagBytes));
        var changelog = await _git.FindFileAsync(Project, Changelog.FileName, Main, ct);
        var log = Changelog.Parse(changelog?.Text, _ctx.RepoName);
        log.Release(ReportVersion.Heading(version, _req.TaskReference), Notes);
        actions.Add(changelog is null ? GitLabCommitAction.Create(Changelog.FileName, log.Render()) : GitLabCommitAction.Update(Changelog.FileName, log.Render()));

        var workBranch = $"webadmin/report-rollback/{ProjectNames.Slug(_ctx.RepoName)}-{DateTime.UtcNow:yyyyMMdd-HHmmss}";
        var commit = await _git.CreateCommitAsync(Project, workBranch, Main, _req.CommitMessage, actions, ct);
        _job.Update(j => j.CommitUrl = commit.WebUrl);
        Ok($"Коммит {commit.ShortId} во временной ветке {workBranch}.", commit.WebUrl);

        var ciConfigPath = await CentralCi.EnsureAsync(_git, Project, _ctx.CiConfigPath, _ctx.Settings.ReportsCiConfigPath, workBranch, (level, text) => Add(level, text), ct);
        GitLabMergeRequest mr;
        try
        {
            mr = await _git.CreateMergeRequestAsync(Project, workBranch, Main, _req.CommitMessage,
                $"## Откат отчёта на {_req.Tag}\n\n{string.Join("\n", Notes.Select(n => "- " + n))}\n\n---\n\nСоздано PilotProAdmin.\n\n- Отчёт: `{_ctx.RepoName}`\n- Новая версия: {version}\n- Сервер: {_prod.Title} ({_prod.PilotUrl})\n- Pilot: {_job.PilotUser}", ct);
        }
        catch
        {
            await SafeAsync(() => _git.DeleteBranchAsync(Project, workBranch, CancellationToken.None));
            throw;
        }
        _job.Update(j => j.MergeRequestUrl = mr.WebUrl);
        Ok($"Создан Merge Request !{mr.Iid} / {Main}.", mr.WebUrl);

        var flow = new MergeRequestFlow(_git, Project, _job, _ctx.Settings, NotSaved);
        var merged = false;
        try
        {
            // 4. Pipeline
            var ciPath = string.IsNullOrWhiteSpace(ciConfigPath) ? ".gitlab-ci.yml" : ciConfigPath!;
            var hasCi = ciPath.Contains(':') || ciPath.Contains('@') || await _git.FindFileAsync(Project, ciPath, workBranch, ct) is not null;
            if (hasCi)
            {
                _job.Update(j => j.Status = JobStatus.WaitingPipeline);
                await flow.WaitForPipelinesAsync(mr.Iid, commit.Id, ct);
                _job.Update(j => j.Status = JobStatus.Running);
            }
            else if (_ctx.Settings.AllowPublishWithoutCi)
            {
                Add("warn", $"В репозитории нет {ciPath} - проверки GitLab не настроены, pipeline не ожидается.");
            }
            else
            {
                throw new WorkflowException($"В репозитории нет {ciPath}, а перенос без pipeline запрещён в настройках. {NotSaved}");
            }

            // 5. Сервер, затем merge и релиз
            var reason = ReportVersion.Reason(version, Notes, _req.TaskReference);
            var fileName = prodReport.FileName ?? Path.GetFileName(path);
            var applied = false;
            if (ReportFiles.SameContent(prodBytes, tagBytes))
            {
                Ok($"На сервере {_prod.Title} уже содержимое {_req.Tag} - запись не требуется.");
            }
            else
            {
                Info($"Запись версии {version} (содержимое {_req.Tag}) на сервер {_prod.Title}…");
                await _pilot.UpdateAsync(_prod.PilotUrl, _ctx.ProdLogin, License, prodReport.Id, fileName, tagBytes, reason, ct);
                applied = true;
                Ok($"Отчёт «{prodReport.Name}» на сервере {_prod.Title} возвращён к {_req.Tag} (новая версия {version}).");
            }

            try
            {
                await flow.MergeAsync(mr.Iid, commit.Id, Main, ct);
                merged = true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Add("error", $"Merge Request не влит: {ex.Message}");
                if (applied && prodBytes is not null)
                {
                    try
                    {
                        await _pilot.UpdateAsync(_prod.PilotUrl, _ctx.ProdLogin, License, prodReport.Id, fileName, prodBytes,
                            "Откат не завершён - возвращена предыдущая версия", CancellationToken.None);
                        Ok($"На сервере {_prod.Title} возвращена версия, что была до отката.");
                    }
                    catch (Exception rollbackError)
                    {
                        Add("error", $"ОТКАТ НЕ УДАЛСЯ - сервер {_prod.Title} расходится с веткой {Main}: {rollbackError.Message}");
                    }
                }
                throw new WorkflowException($"GitLab не принял изменения в {Main}: {ex.Message}.");
            }

            try
            {
                var (level, text, url) = await ReportVersion.TagReleaseAsync(_git, Project, releases, version,
                    $"откат на {_req.Tag}", ReportVersion.ReleaseDescription(Notes, _req.TaskReference, _prod, _job), ct,
                    (Path.GetFileName(path), tagBytes));
                Add(level, text, url);
            }
            catch (GitLabApiException ex)
            {
                Add("warn", $"Не удалось создать тег/Release: {ex.Message}");
            }
            Add("warn", "Ветка dev не менялась: в ней по-прежнему версия, от которой откатились. Исправьте отчёт и отправьте в dev заново.");
        }
        finally
        {
            if (!merged)
            {
                Info("Временный MR закрыт, временная ветка удалена.");
                await SafeAsync(() => _git.CloseMergeRequestAsync(Project, mr.Iid, CancellationToken.None));
                await SafeAsync(() => _git.DeleteBranchAsync(Project, workBranch, CancellationToken.None));
            }
        }
    }

    private static async Task SafeAsync(Func<Task> action)
    {
        try { await action(); } catch { /* очистка - не критично */ }
    }

    private void Info(string text, string? url = null) => Add("info", text, url);
    private void Ok(string text, string? url = null) => Add("ok", text, url);
    private void Add(string level, string text, string? url = null, string? details = null) => _job.Add(level, text, url, details);
}
