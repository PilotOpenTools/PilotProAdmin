using ProAdmin.Automations;
using ProAdmin.GitLab;
using ProAdmin.Jobs;
using ProAdmin.Pilot;
using ProAdmin.Settings;

namespace ProAdmin.Reports;

/// <summary>Что переносит пользователь: отчёт и сообщение для MR.</summary>
public sealed class ReportPromoteRequest
{
    public required string Name { get; init; }
    public required string CommitMessage { get; init; }

    /// <summary>Что изменилось - пункты нового раздела CHANGELOG.md.</summary>
    public IReadOnlyList<string> ChangeNotes { get; init; } = Array.Empty<string>();
    public string? TaskReference { get; init; }

    /// <summary>Папка отчётов на продовом сервере - если отчёта там ещё нет.</summary>
    public Guid? TargetFolderId { get; init; }

    /// <summary>Откуда переносится: dev или активный релиз release/&lt;дата&gt; (он же - номер версии).</summary>
    public string Source { get; init; } = AutomationRepositories.DevBranch;
}

public sealed class ReportPromoteContext
{
    public required PilotLogin ProdLogin { get; init; }
    public required GitLabToken GitLabToken { get; init; }
    public required AdminSettings Settings { get; init; }
    public required string ProjectPath { get; init; }
    public required string RepoName { get; init; }
    public string? CiConfigPath { get; init; }
}

/// <summary>
/// Перенос отчёта на прод: MR <c>dev</c> (или <c>release/&lt;дата&gt;</c> со стейджа) / <c>main</c> / pipeline / запись .repx на продовый сервер /
/// merge (если merge не удался - откат сервера). Ветка dev не удаляется.
/// Сам отчёт в админке не меняется: в main и на прод уходит ровно то, что лежит в dev.
/// </summary>
internal sealed class ReportPromoteWorkflow
{
    private const string NotSaved = "Отчёт НЕ сохранён на продовом сервере.";

    private readonly PilotReportGateway _pilot;
    private readonly GitLabClient _git;
    private readonly PublishJob _job;
    private readonly ReportPromoteRequest _req;
    private readonly ReportPromoteContext _ctx;
    private readonly EnvironmentSettings _prod;

    public ReportPromoteWorkflow(PilotReportGateway pilot, GitLabClient git, PublishJob job, ReportPromoteRequest request, ReportPromoteContext context)
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
    private const string Dev = AutomationRepositories.DevBranch;
    private string Source => _req.Source;
    private bool IsHotfix => ProAdmin.Modules.ModuleBranches.IsHotfix(Source);
    private const string Main = AutomationRepositories.MainBranch;
    private int License => _ctx.Settings.ReportsLicenseType;

    public async Task RunAsync(CancellationToken ct)
    {
        // 1. Ветки
        Info($"Чтение веток {Source} и {Main} ({Project})…");
        var devTree = await _git.ListTreeAsync(Project, Source, ct) ?? throw new WorkflowException($"В {Project} нет ветки {Source}.");
        var mainTree = await _git.ListTreeAsync(Project, Main, ct) ?? throw new WorkflowException($"В {Project} нет ветки {Main}.");
        var devPath = ReportFiles.FindPath(devTree, _ctx.RepoName)
                      ?? throw new WorkflowException($"В ветке {Source} нет файла .repx отчёта «{_ctx.RepoName}».");
        var devBytes = await _git.GetRawFileAsync(Project, devPath, Source, ct)
                       ?? throw new WorkflowException($"Не удалось прочитать {devPath} из ветки {Source}.");
        var mainPath = ReportFiles.FindPath(mainTree, _ctx.RepoName);
        var mainBytes = mainPath is null ? null : await _git.GetRawFileAsync(Project, mainPath, Main, ct);

        // Номер версии - дата переноса (второй за день - .2): тег, CHANGELOG.md, причина версии в Pilot.
        _releases = await ReportVersion.ListReleasesAsync(_git, Project, ct);
        _version = ProAdmin.Content.ContentReleases.Version(Source) ?? ReportVersion.Next(DateTimeOffset.Now, _releases.Select(r => r.Tag.Name));
        Info($"Номер версии: {_version} (релиз {ReportVersion.TagName(_version)}).");

        // 2. Продовый сервер
        Info($"Чтение отчётов сервера {_prod.Title}…");
        var prodList = await _pilot.ListAsync(_prod.PilotUrl, _ctx.ProdLogin, License, withContent: false, ct);
        var key = ProjectNames.Key(_ctx.RepoName);
        var prodReport = prodList.Reports.FirstOrDefault(r => ProjectNames.Key(r.Name) == key);
        byte[]? prodBytes = prodReport is null ? null : await _pilot.DownloadAsync(_prod.PilotUrl, _ctx.ProdLogin, License, prodReport.Id, ct);
        if (prodReport is null)
        {
            var folder = prodList.Folders.FirstOrDefault(f => f.Id == _req.TargetFolderId) ?? prodList.Folders.First();
            Info($"На сервере {_prod.Title} отчёта нет - он будет создан в папке «{(folder.Path.Length == 0 ? "корень папки отчётов" : folder.Path)}».");
        }

        var target = new PilotTarget(prodReport, prodBytes, _req.TargetFolderId ?? prodList.RootFolderId!.Value, Path.GetFileName(devPath));

        // 3. Ветка main уже содержит версию dev - только сервер
        if (ReportFiles.SameContent(devBytes, mainBytes))
        {
            Info($"Ветка {Main} уже содержит версию из {Source} - Merge Request не нужен.");
            await ApplyToPilotAsync(target, devBytes, ct);
            await TagReleaseAsync((target.FileName, devBytes), ct);
            return;
        }

        // Хотфикс в main, которого нет в dev, - перенос dev перезаписал бы его на проде.
        // Hotfix сделан от main - сверять с dev нечего.
        var drift = IsHotfix ? ReportDrift.DevAhead : await ReportCatalog.DriftAsync(_git, Project, devPath, mainPath, ct);
        if (drift is ReportDrift.MainAhead or ReportDrift.Diverged)
            throw new WorkflowException(
                $"В ветке {Main} есть изменения отчёта, которых нет в {Dev} (хотфикс на проде). " +
                $"Сначала перенесите их с прода в тест (кнопка «В тест» на вкладке «Отчёты»). {NotSaved}");

        // 4. Раздел CHANGELOG.md коммитом в ветку-источник - MR перенесёт его в main вместе с отчётом
        await ReleaseChangelogAsync(ct);

        // 5. MR dev / main (открытый переиспользуется)
        var devHead = (await _git.FindBranchAsync(Project, Source, ct))?.Commit?.Id
                      ?? throw new WorkflowException($"Не удалось прочитать ветку {Source}.");
        var ciConfigPath = await CentralCi.EnsureAsync(_git, Project, _ctx.CiConfigPath, _ctx.Settings.ReportsCiConfigPath, Source, (level, text) => Add(level, text), ct);
        var existing = (await _git.ListOpenMergeRequestsAsync(Project, Source, Main, ct)).FirstOrDefault();
        GitLabMergeRequest mr;
        var createdMr = existing is null;
        if (existing is not null)
        {
            mr = existing;
            Info($"Используется открытый Merge Request !{mr.Iid} {Source} / {Main}.", mr.WebUrl);
        }
        else
        {
            mr = await _git.CreateMergeRequestAsync(Project, Source, Main, _req.CommitMessage,
                $"## Что изменилось\n\n{NotesMarkdown}\n\n---\n\nСоздано PilotProAdmin (перенос отчёта на прод).\n\n- Отчёт: `{_ctx.RepoName}`\n- Сервер: {_prod.Title} ({_prod.PilotUrl})\n- Pilot: {_job.PilotUser}",
                ct, removeSourceBranch: IsHotfix);
            Ok($"Создан Merge Request !{mr.Iid} {Source} / {Main}.", mr.WebUrl);
        }
        _job.Update(j => j.MergeRequestUrl = mr.WebUrl);

        var flow = new MergeRequestFlow(_git, Project, _job, _ctx.Settings, NotSaved);
        var merged = false;
        try
        {
            // 6. Pipeline
            var ciPath = string.IsNullOrWhiteSpace(ciConfigPath) ? ".gitlab-ci.yml" : ciConfigPath!;
            var hasCi = ciPath.Contains(':') || ciPath.Contains('@') || await _git.FindFileAsync(Project, ciPath, Source, ct) is not null;
            if (hasCi)
            {
                _job.Update(j => j.Status = JobStatus.WaitingPipeline);
                await flow.WaitForPipelinesAsync(mr.Iid, devHead, ct);
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
            var applied = await ApplyToPilotAsync(target, devBytes, ct);
            try
            {
                await flow.MergeAsync(mr.Iid, devHead, Main, ct, removeSourceBranch: IsHotfix);
                merged = true;
                await TagReleaseAsync((target.FileName, devBytes), ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Add("error", $"Merge Request не влит: {ex.Message}");
                await RollbackPilotAsync(target, applied, CancellationToken.None);
                throw new WorkflowException($"GitLab не принял изменения в {Main}: {ex.Message}. Изменения на сервере {_prod.Title} откатаны.");
            }
        }
        finally
        {
            if (!merged && createdMr)
            {
                Info($"Созданный Merge Request закрыт, ветка {Source} не тронута.");
                try { await _git.CloseMergeRequestAsync(Project, mr.Iid, CancellationToken.None); }
                catch { /* очистка - не критично */ }
            }
        }
    }

    private IReadOnlyList<string> Notes => _req.ChangeNotes.Count > 0
        ? _req.ChangeNotes
        : new[] { ChangelogSource.StripName(_req.CommitMessage, _ctx.RepoName) };

    private string _version = "";
    private IReadOnlyList<(GitLabTag Tag, string Version)> _releases = Array.Empty<(GitLabTag, string)>();

    /// <summary>
    /// Причина новой версии отчёта в Pilot (видна в списке версий рядом с датой и автором):
    /// номер версии, пункты «Что изменилось» через «; » и номер задачи.
    /// </summary>
    private string SnapshotReason => ReportVersion.Reason(_version, Notes, _req.TaskReference);

    /// <summary>Релиз v{номер} на коммите main - точка, к которой можно откатиться.</summary>
    private async Task TagReleaseAsync((string FileName, byte[] Content) report, CancellationToken ct)
    {
        try
        {
            var (level, text, url) = await ReportVersion.TagReleaseAsync(_git, Project, _releases, _version,
                string.Join("; ", Notes),
                ReportVersion.ReleaseDescription(Notes, _req.TaskReference, _prod, _job), ct, report);
            Add(level, text, url);
        }
        catch (GitLabApiException ex)
        {
            Add("warn", $"Не удалось создать тег/Release: {ex.Message}");
        }
    }

    private string NotesMarkdown => string.Join("\n", Notes.Select(n => "- " + n));

    /// <summary>
    /// «Не выпущено» в dev (выпущенные разделы сверяются с main) / новый раздел с датой.
    /// Если перенос потом не удастся, при следующем переносе раздел, которого нет в main, снова станет «Не выпущено».
    /// </summary>
    private async Task ReleaseChangelogAsync(CancellationToken ct)
    {
        try
        {
            var devFile = await _git.FindFileAsync(Project, Changelog.FileName, Source, ct);
            var mainFile = await _git.FindFileAsync(Project, Changelog.FileName, Main, ct);
            var log = Changelog.Parse(devFile?.Text, _ctx.RepoName);
            log.SyncReleased(Changelog.Parse(mainFile?.Text, _ctx.RepoName), null);
            log.Release(ReportVersion.Heading(_version, _req.TaskReference), Notes);
            var text = log.Render();

            var action = devFile is null ? GitLabCommitAction.Create(Changelog.FileName, text) : GitLabCommitAction.Update(Changelog.FileName, text);
            var commit = await _git.CreateCommitAsync(Project, Source, null,
                $"{_ctx.RepoName}: {Changelog.FileName} - {log.Released[0].Heading}", new[] { action }, ct);
            Ok($"{Changelog.FileName}: раздел {log.Released[0].Heading} (коммит {commit.ShortId} в {Source}).", commit.WebUrl);
        }
        catch (GitLabApiException ex)
        {
            Add("warn", $"{Changelog.FileName} не обновлён: {ex.Message}. Перенос продолжается без записи в журнал.");
        }
    }

    private sealed record PilotTarget(PilotReport? Report, byte[]? CurrentContent, Guid FolderId, string FileName);

    private enum Applied { None, Updated, Created }

    private Guid? _createdId;

    private async Task<Applied> ApplyToPilotAsync(PilotTarget target, byte[] content, CancellationToken ct)
    {
        if (target.Report is not null && ReportFiles.SameContent(target.CurrentContent, content))
        {
            Ok($"На сервере {_prod.Title} уже эта версия отчёта - запись не требуется.");
            return Applied.None;
        }

        if (target.Report is not null)
        {
            Info($"Запись новой версии отчёта на сервер {_prod.Title}…");
            await _pilot.UpdateAsync(_prod.PilotUrl, _ctx.ProdLogin, License, target.Report.Id, target.Report.FileName ?? target.FileName, content, SnapshotReason, ct);
            Ok($"Отчёт «{target.Report.Name}» обновлён на сервере {_prod.Title} (новая версия).");
            return Applied.Updated;
        }

        Info($"Создание отчёта на сервере {_prod.Title}…");
        _createdId = await _pilot.CreateAsync(_prod.PilotUrl, _ctx.ProdLogin, License, target.FolderId, _ctx.RepoName, target.FileName, content, ct);
        Ok($"Отчёт «{_ctx.RepoName}» создан на сервере {_prod.Title}.");
        return Applied.Created;
    }

    private async Task RollbackPilotAsync(PilotTarget target, Applied applied, CancellationToken ct)
    {
        try
        {
            switch (applied)
            {
                case Applied.Updated when target.Report is not null && target.CurrentContent is not null:
                    await _pilot.UpdateAsync(_prod.PilotUrl, _ctx.ProdLogin, License, target.Report.Id, target.Report.FileName ?? target.FileName,
                        target.CurrentContent, "Откат: перенос на прод не завершён", ct);
                    Ok($"На сервере {_prod.Title} возвращена предыдущая версия отчёта.");
                    break;
                case Applied.Created when _createdId is { } id:
                    await _pilot.MoveToRecycleBinAsync(_prod.PilotUrl, _ctx.ProdLogin, License, id, ct);
                    Ok($"Созданный на сервере {_prod.Title} отчёт перемещён в корзину.");
                    break;
            }
        }
        catch (Exception ex)
        {
            Add("error", $"ОТКАТ НЕ УДАЛСЯ - сервер {_prod.Title} расходится с веткой {Main}: {ex.Message}");
        }
    }

    private void Info(string text, string? url = null) => Add("info", text, url);
    private void Ok(string text, string? url = null) => Add("ok", text, url);
    private void Add(string level, string text, string? url = null, string? details = null) => _job.Add(level, text, url, details);
}
