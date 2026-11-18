using ProAdmin.Automations;
using ProAdmin.GitLab;
using ProAdmin.Jobs;
using ProAdmin.Pilot;
using ProAdmin.Settings;

namespace ProAdmin.Reports;

/// <summary>Перенос хотфикса с прода в тест: отчёт и сообщение для MR.</summary>
public sealed class ReportBackportRequest
{
    public required string Name { get; init; }
    public required string CommitMessage { get; init; }
}

public sealed class ReportBackportContext
{
    public required PilotLogin TestLogin { get; init; }
    public required GitLabToken GitLabToken { get; init; }
    public required AdminSettings Settings { get; init; }
    public required string ProjectPath { get; init; }
    public required string RepoName { get; init; }
    public string? CiConfigPath { get; init; }
}

/// <summary>
/// Хотфикс на проде (модуль отправил отчёт прямо в <c>main</c>) возвращается в разработку:
/// MR <c>main</c> / <c>dev</c> / pipeline / запись .repx на тестовый сервер / merge (если merge не удался - откат сервера).
/// Настоящий merge, а не копия файла: после него dev содержит историю main, и следующий перенос dev / main не конфликтует.
/// При конфликте (отчёт менялся и в dev) MR остаётся открытым - объединить его можно в GitLab и запустить перенос снова.
/// </summary>
internal sealed class ReportBackportWorkflow
{
    private const string NotSaved = "Отчёт НЕ изменён на тестовом сервере.";
    private const string Dev = AutomationRepositories.DevBranch;
    private const string Main = AutomationRepositories.MainBranch;

    private readonly PilotReportGateway _pilot;
    private readonly GitLabClient _git;
    private readonly PublishJob _job;
    private readonly ReportBackportRequest _req;
    private readonly ReportBackportContext _ctx;
    private readonly EnvironmentSettings _test;

    public ReportBackportWorkflow(PilotReportGateway pilot, GitLabClient git, PublishJob job, ReportBackportRequest request, ReportBackportContext context)
    {
        _pilot = pilot;
        _git = git;
        _job = job;
        _req = request;
        _ctx = context;
        _test = ReportCatalog.DevEnv(context.Settings)
                ?? throw new WorkflowException("В настройках нет контура с сервером Pilot и веткой dev.");
    }

    private string Project => _ctx.ProjectPath;
    private int License => _ctx.Settings.ReportsLicenseType;

    public async Task RunAsync(CancellationToken ct)
    {
        // 1. Ветки
        Info($"Чтение веток {Main} и {Dev} ({Project})…");
        var mainTree = await _git.ListTreeAsync(Project, Main, ct) ?? throw new WorkflowException($"В {Project} нет ветки {Main}.");
        var devTree = await _git.ListTreeAsync(Project, Dev, ct) ?? throw new WorkflowException($"В {Project} нет ветки {Dev}.");
        var mainPath = ReportFiles.FindPath(mainTree, _ctx.RepoName)
                       ?? throw new WorkflowException($"В ветке {Main} нет файла .repx отчёта «{_ctx.RepoName}».");
        var mainBytes = await _git.GetRawFileAsync(Project, mainPath, Main, ct)
                        ?? throw new WorkflowException($"Не удалось прочитать {mainPath} из ветки {Main}.");
        var devPath = ReportFiles.FindPath(devTree, _ctx.RepoName);
        var devBytes = devPath is null ? null : await _git.GetRawFileAsync(Project, devPath, Dev, ct);

        // 2. Тестовый сервер
        Info($"Чтение отчётов сервера {_test.Title}…");
        var testList = await _pilot.ListAsync(_test.PilotUrl, _ctx.TestLogin, License, withContent: false, ct);
        var key = ProjectNames.Key(_ctx.RepoName);
        var testReport = testList.Reports.FirstOrDefault(r => ProjectNames.Key(r.Name) == key);
        var testBytes = testReport is null ? null : await _pilot.DownloadAsync(_test.PilotUrl, _ctx.TestLogin, License, testReport.Id, ct);

        // 3. Ветка dev уже содержит версию main - только сервер
        if (ReportFiles.SameContent(devBytes, mainBytes))
        {
            Info($"Ветка {Dev} уже содержит версию из {Main} - Merge Request не нужен.");
            await ApplyAsync(testReport, testBytes, testList, mainBytes, Path.GetFileName(mainPath), ct);
            return;
        }

        // 4. MR main / dev (открытый переиспользуется)
        var mainHead = (await _git.FindBranchAsync(Project, Main, ct))?.Commit?.Id
                       ?? throw new WorkflowException($"Не удалось прочитать ветку {Main}.");
        var ciConfigPath = await CentralCi.EnsureAsync(_git, Project, _ctx.CiConfigPath, _ctx.Settings.ReportsCiConfigPath, Main, (level, text) => Add(level, text), ct);
        var existing = (await _git.ListOpenMergeRequestsAsync(Project, Main, Dev, ct)).FirstOrDefault();
        GitLabMergeRequest mr;
        var createdMr = existing is null;
        if (existing is not null)
        {
            mr = existing;
            Info($"Используется открытый Merge Request !{mr.Iid} {Main} / {Dev}.", mr.WebUrl);
        }
        else
        {
            mr = await _git.CreateMergeRequestAsync(Project, Main, Dev, _req.CommitMessage,
                $"## Хотфикс с прода в разработку\n\nОтчёт менялся прямо в `{Main}` (на проде). Merge Request возвращает эту версию в `{Dev}` и на сервер {_test.Title}.\n\n---\n\nСоздано PilotProAdmin.\n\n- Отчёт: `{_ctx.RepoName}`\n- Сервер: {_test.Title} ({_test.PilotUrl})\n- Pilot: {_job.PilotUser}",
                ct, removeSourceBranch: false);
            Ok($"Создан Merge Request !{mr.Iid} {Main} / {Dev}.", mr.WebUrl);
        }
        _job.Update(j => j.MergeRequestUrl = mr.WebUrl);

        var keepMr = false;
        var merged = false;
        try
        {
            // 5. Конфликт - отчёт менялся и в dev: объединять человеку, MR остаётся открытым
            if (await HasConflictsAsync(mr.Iid, ct))
            {
                keepMr = true;
                throw new WorkflowException(
                    $"Отчёт менялся и в {Dev}, и в {Main} - Merge Request !{mr.Iid} {Main} / {Dev} с конфликтом. " +
                    $"Объедините изменения в GitLab (MR оставлен открытым) и запустите перенос с прода снова. {NotSaved}");
            }

            // 6. Pipeline
            var flow = new MergeRequestFlow(_git, Project, _job, _ctx.Settings, NotSaved);
            var ciPath = string.IsNullOrWhiteSpace(ciConfigPath) ? ".gitlab-ci.yml" : ciConfigPath!;
            var hasCi = ciPath.Contains(':') || ciPath.Contains('@') || await _git.FindFileAsync(Project, ciPath, Main, ct) is not null;
            if (hasCi)
            {
                _job.Update(j => j.Status = JobStatus.WaitingPipeline);
                await flow.WaitForPipelinesAsync(mr.Iid, mainHead, ct);
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

            // 7. Сервер, затем merge
            var applied = await ApplyAsync(testReport, testBytes, testList, mainBytes, Path.GetFileName(mainPath), ct);
            try
            {
                await flow.MergeAsync(mr.Iid, mainHead, Dev, ct, removeSourceBranch: false);
                merged = true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Add("error", $"Merge Request не влит: {ex.Message}");
                await RollbackAsync(testReport, testBytes, applied);
                throw new WorkflowException($"GitLab не принял изменения в {Dev}: {ex.Message}. Изменения на сервере {_test.Title} откатаны.");
            }
        }
        finally
        {
            if (!merged && createdMr && !keepMr)
            {
                Info($"Созданный Merge Request закрыт, ветки {Main} и {Dev} не тронуты.");
                try { await _git.CloseMergeRequestAsync(Project, mr.Iid, CancellationToken.None); }
                catch { /* очистка - не критично */ }
            }
        }
    }

    /// <summary>GitLab проверяет MR асинхронно - ждём, пока статус перестанет быть «проверяется».</summary>
    private async Task<bool> HasConflictsAsync(long iid, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (true)
        {
            var state = await _git.GetMergeRequestAsync(Project, iid, ct);
            if (state.HasConflicts || state.DetailedMergeStatus == "conflict")
                return true;
            if (state.DetailedMergeStatus is not (null or "checking" or "unchecked" or "preparing") || DateTimeOffset.UtcNow > deadline)
                return false;
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }
    }

    private enum Applied { None, Updated, Created }

    private Guid? _createdId;

    private string Reason => $"С прода ({Main}): {_req.CommitMessage}";

    private async Task<Applied> ApplyAsync(PilotReport? report, byte[]? current, PilotReportList list, byte[] content, string fileName, CancellationToken ct)
    {
        if (report is not null && ReportFiles.SameContent(current, content))
        {
            Ok($"На сервере {_test.Title} уже эта версия отчёта - запись не требуется.");
            return Applied.None;
        }

        if (report is not null)
        {
            Info($"Запись версии с прода на сервер {_test.Title}…");
            await _pilot.UpdateAsync(_test.PilotUrl, _ctx.TestLogin, License, report.Id, report.FileName ?? fileName, content, Reason, ct);
            Ok($"Отчёт «{report.Name}» обновлён на сервере {_test.Title} (новая версия).");
            return Applied.Updated;
        }

        var folder = list.RootFolderId ?? list.Folders.First().Id;
        Info($"Отчёта на сервере {_test.Title} нет - создание в корне папки отчётов…");
        _createdId = await _pilot.CreateAsync(_test.PilotUrl, _ctx.TestLogin, License, folder, _ctx.RepoName, fileName, content, ct);
        Ok($"Отчёт «{_ctx.RepoName}» создан на сервере {_test.Title}.");
        return Applied.Created;
    }

    private async Task RollbackAsync(PilotReport? report, byte[]? previous, Applied applied)
    {
        try
        {
            switch (applied)
            {
                case Applied.Updated when report is not null && previous is not null:
                    await _pilot.UpdateAsync(_test.PilotUrl, _ctx.TestLogin, License, report.Id, report.FileName ?? report.Name + ReportFiles.Extension,
                        previous, "Откат: перенос с прода не завершён", CancellationToken.None);
                    Ok($"На сервере {_test.Title} возвращена предыдущая версия отчёта.");
                    break;
                case Applied.Created when _createdId is { } id:
                    await _pilot.MoveToRecycleBinAsync(_test.PilotUrl, _ctx.TestLogin, License, id, CancellationToken.None);
                    Ok($"Созданный на сервере {_test.Title} отчёт перемещён в корзину.");
                    break;
            }
        }
        catch (Exception ex)
        {
            Add("error", $"ОТКАТ НЕ УДАЛСЯ - сервер {_test.Title} расходится с веткой {Dev}: {ex.Message}");
        }
    }

    private void Info(string text, string? url = null) => Add("info", text, url);
    private void Ok(string text, string? url = null) => Add("ok", text, url);
    private void Add(string level, string text, string? url = null, string? details = null) => _job.Add(level, text, url, details);
}
