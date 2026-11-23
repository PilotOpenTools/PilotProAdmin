using Ascon.Pilot.DataClasses;
using ProAdmin.Automations;
using ProAdmin.GitLab;
using ProAdmin.Pilot;
using ProAdmin.Settings;

namespace ProAdmin.Jobs;

public sealed class WorkflowException : Exception
{
    public WorkflowException(string message, string? details = null) : base(message) => Details = details;

    public string? Details { get; }
}

/// <summary>
/// Отправка изменения автоматизации:
/// проверки / коммит во временную ветку / MR в ветку контура / ожидание pipeline /
/// запись в Pilot / merge MR (если merge не удался - откат Pilot).
/// Так ветка контура и сервер Pilot всегда меняются вместе и только после успешных проверок GitLab.
/// </summary>
internal sealed class PublishWorkflow
{
    private readonly PilotGateway _pilot;
    private readonly Pilot.ServerModules.PilotServerModuleGateway _modules;
    private readonly GitLabClient _git;
    private readonly PublishJob _job;
    private readonly PublishRequest _req;
    private readonly PublishContext _ctx;
    private readonly EnvironmentSettings _env;

    public PublishWorkflow(PilotGateway pilot, Pilot.ServerModules.PilotServerModuleGateway modules, GitLabClient git, PublishJob job,
        PublishRequest request, PublishContext context)
    {
        _pilot = pilot;
        _modules = modules;
        _git = git;
        _job = job;
        _req = request;
        _ctx = context;
        _env = context.Settings.Find(request.Env) ?? throw new WorkflowException($"Контур «{request.Env}» не найден в настройках.");
        if (!_env.IsEditable)
            throw new WorkflowException($"Для контура «{_env.Title}» не заданы сервер Pilot и ветка.");
    }

    private string Project => _ctx.ProjectPath;

    private MergeRequestFlow Flow => _flow ??= new MergeRequestFlow(_git, Project, _job, _ctx.Settings, "Автоматизация НЕ сохранена на сервере.");
    private MergeRequestFlow? _flow;
    private string Branch => _env.Branch;

    public async Task RunAsync(CancellationToken ct)
    {
        var jsonPath = AutomationFiles.JsonPath(_req.Name);
        var csPath = AutomationFiles.CsPath(_req.Name);
        var triggers = AutomationFiles.PrepareForStorage(_req.Delete ? _req.Triggers : AutomationVersion.ToStorageLayout(_req.Triggers ?? ""));
        var cs = AutomationFiles.PrepareForStorage(_req.CSharpCode);
        if (!_req.Delete && !AutomationFiles.SameContent(triggers, _req.Triggers))
            Info("Версия перенесена в первый триггер (отдельная запись { \"_comment_\" } без \"when\" роняет клиент Pilot).");

        // 1. Состояние ветки и сервера
        Info($"Чтение ветки {Branch} ({Project})…");
        if (await _git.FindBranchAsync(Project, Branch, ct) is null)
            throw new WorkflowException($"Ветка «{Branch}» не найдена в {Project}.");

        var gitJson = await _git.FindFileAsync(Project, jsonPath, Branch, ct);
        var gitCs = await _git.FindFileAsync(Project, csPath, Branch, ct);
        CheckBase(_req.BaseGitJsonBlob, gitJson, jsonPath);
        CheckBase(_req.BaseGitCsBlob, gitCs, csPath);

        Info($"Чтение автоматизаций сервера {_env.Title}…");
        var current = await _pilot.GetAutomationAsync(_env.PilotUrl, _ctx.Pilot, _req.Name, ct);
        if (_req.BasePilotHash is not null && (current?.Hash ?? "") != _req.BasePilotHash)
            throw new WorkflowException($"Автоматизация на сервере {_env.Title} изменилась после открытия редактора. Обновите страницу и повторите.");
        if (_req.Delete && current is null && gitJson is null && gitCs is null)
            throw new WorkflowException("Удалять нечего: автоматизации нет ни на сервере, ни в ветке.");

        // 2. Проверки содержимого
        if (!_req.Delete)
        {
            await LintAsync(triggers, cs, ct);
            if (_ctx.Settings.RequireVersion)
                Report(AutomationChecks.CheckVersion(triggers, gitJson?.Text, Branch, !AutomationFiles.SameContent(gitCs?.Text, cs)));

            if (cs.Length > 0 && !AutomationFiles.SameContent(current?.CSharpCode, cs))
            {
                Info("Компиляция C#-кода на сервере Pilot…");
                var validation = await _pilot.ValidateCSharpAsync(_env.PilotUrl, _ctx.Pilot, cs, ct);
                if (!validation.IsSuccess)
                    throw new WorkflowException("C#-код не компилируется. Автоматизация не отправлена.", FormatDiagnostics(validation));
                Ok("C#-код скомпилирован сервером Pilot.");
            }
        }

        // 3. Изменения для коммита
        var actions = new List<GitLabCommitAction>();
        if (_req.Delete)
        {
            if (gitJson is not null) actions.Add(GitLabCommitAction.Delete(jsonPath));
            if (gitCs is not null) actions.Add(GitLabCommitAction.Delete(csPath));
        }
        else
        {
            if (gitJson is null) actions.Add(GitLabCommitAction.Create(jsonPath, triggers));
            else if (!AutomationFiles.SameContent(gitJson.Text, triggers)) actions.Add(GitLabCommitAction.Update(jsonPath, triggers));

            if (cs.Length > 0)
            {
                if (gitCs is null) actions.Add(GitLabCommitAction.Create(csPath, cs));
                else if (!AutomationFiles.SameContent(gitCs.Text, cs)) actions.Add(GitLabCommitAction.Update(csPath, cs));
            }
            else if (gitCs is not null)
            {
                actions.Add(GitLabCommitAction.Delete(csPath));
            }
        }

        if (actions.Count == 0)
        {
            Info($"Ветка {Branch} уже содержит это содержимое - коммит и pipeline не нужны.");
            await ApplyToPilotAsync(current, triggers, cs, ct);
            await TagReleaseIfMainAsync(triggers, ct);
            await AutoStageAsync(triggers, cs, ct);
            await DeleteSourceBranchAsync();
            return;
        }

        if (!_req.Delete)
            actions.Add(await ChangelogActionAsync(triggers, ct));

        // 4. Временная ветка + MR
        var workBranch = $"webadmin/{_env.Key}/{AutomationFiles.BranchSlug(_req.Name)}-{DateTime.UtcNow:yyyyMMdd-HHmmss}";
        var commit = await _git.CreateCommitAsync(Project, workBranch, Branch, FullCommitMessage, actions, ct);
        _job.Update(j => j.CommitUrl = commit.WebUrl);
        Ok($"Коммит {commit.ShortId} во временной ветке {workBranch}: {string.Join(", ", actions.Select(a => $"{ActionRu(a.Action)} {a.FilePath}"))}", commit.WebUrl);

        var ciConfigPath = await CentralCi.EnsureAsync(_git, Project, _ctx.CiConfigPath, _ctx.Settings.AutomationsCiConfigPath, workBranch, (level, text) => Add(level, text), ct);
        GitLabMergeRequest mr;
        try
        {
            mr = await _git.CreateMergeRequestAsync(Project, workBranch, Branch, _req.CommitMessage,
                $"## Что изменилось\n\n{NotesMarkdown}\n\n---\n\nСоздано PilotProAdmin.\n\n- Контур: {_env.Title} ({_env.PilotUrl})\n- Pilot: {_job.PilotUser}\n- Автоматизация: `{_req.Name}`", ct);
        }
        catch
        {
            await SafeAsync(() => _git.DeleteBranchAsync(Project, workBranch, CancellationToken.None));
            throw;
        }
        _job.Update(j => j.MergeRequestUrl = mr.WebUrl);
        Ok($"Создан Merge Request !{mr.Iid} / {Branch}.", mr.WebUrl);

        var keepMr = false;
        try
        {
            // 5. Pipeline
            var ciPath = string.IsNullOrWhiteSpace(ciConfigPath) ? ".gitlab-ci.yml" : ciConfigPath!;
            var hasCi = ciPath.Contains(':') || ciPath.Contains('@') || // внешний/удалённый конфиг - считаем, что есть
                        await _git.FindFileAsync(Project, ciPath, workBranch, ct) is not null;
            if (hasCi)
            {
                _job.Update(j => j.Status = JobStatus.WaitingPipeline);
                await Flow.WaitForPipelinesAsync(mr.Iid, commit.Id, ct);
                _job.Update(j => j.Status = JobStatus.Running);
            }
            else if (_ctx.Settings.AllowPublishWithoutCi)
            {
                Add("warn", $"В репозитории нет {ciPath} - проверки GitLab не настроены, pipeline не ожидается.");
            }
            else
            {
                throw new WorkflowException($"В репозитории нет {ciPath}, а отправка без pipeline запрещена в настройках. Автоматизация НЕ сохранена на сервере.");
            }

            // 6. Связанные изменения типов (выпуск в прод), Pilot, затем merge
            var types = Branch == AutomationRepositories.MainBranch && _req.ContentRef is not null ? _ctx.Types?.For(_git, _job) : null;
            Types.TypesFollowRun? typesRun = null;
            var typesDone = false;
            try
            {
                if (types is not null &&
                    (typesRun = await types.PrepareProdAsync(Types.TypesTargetKind.Automation, Project, _req.ContentRef!, _req.TaskReference,
                        "Автоматизация НЕ сохранена на сервере.", ct)) is not null)
                    await types.ApplyProdAsync(typesRun, ct);
                await ApplyToPilotAsync(current, triggers, cs, ct);
                try
                {
                    await Flow.MergeAsync(mr.Iid, commit.Id, Branch, ct);
                    keepMr = true;
                    await TagReleaseIfMainAsync(triggers, ct);
                    await SyncDevChangelogAsync(triggers, ct);
                    await AutoStageAsync(triggers, cs, ct);
                    await DeleteSourceBranchAsync();
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Error($"Merge Request не влит: {ex.Message}");
                    await RollbackPilotAsync(current, CancellationToken.None);
                    throw new WorkflowException($"GitLab не принял изменения в {Branch}: {ex.Message}. Изменения на сервере {_env.Title} откатаны.");
                }
                typesDone = true;
            }
            finally
            {
                if (!typesDone && typesRun is not null)
                    await types!.AbortProdAsync(typesRun);
            }
            if (typesRun is not null)
                await types!.CompleteProdAsync(typesRun, ct);
        }
        finally
        {
            if (!keepMr)
            {
                Info("Временный MR закрыт, временная ветка удалена.");
                await SafeAsync(() => _git.CloseMergeRequestAsync(Project, mr.Iid, CancellationToken.None));
                await SafeAsync(() => _git.DeleteBranchAsync(Project, workBranch, CancellationToken.None));
            }
        }
    }

    /// <summary>
    /// Режим «тест и стейдж вместе»: влитое в dev сразу записывается на сервер стейджа.
    /// Тест и dev уже обновлены, поэтому сбой стейджа - предупреждение, а не ошибка операции.
    /// </summary>
    private async Task AutoStageAsync(string triggers, string cs, CancellationToken ct)
    {
        if (_req.Delete || Branch != AutomationRepositories.DevBranch || !_ctx.Settings.AutomationsAutoStage ||
            _ctx.StagePilot is not { } login || ProAdmin.Content.ContentReleases.StageEnv(_ctx.Settings) is not { } stage)
            return;
        try
        {
            if (_ctx.Types is { } types)
                await types.For(_git, _job).StageAsync(Types.TypesTargetKind.Automation, Project, AutomationRepositories.DevBranch,
                    "Стейдж не обновлён.", ct);
            Info($"Запись на сервер {stage.Title} (режим «тест и стейдж вместе»)…");
            var version = await _pilot.UpdateAutomationsAsync(stage.PilotUrl, login, list =>
            {
                var index = list.FindIndex(a => a.Name == _req.Name);
                var script = index >= 0 ? list[index] : new AutomationScript { Name = _req.Name };
                if (index < 0) list.Add(script);
                script.Triggers = triggers;
                if (cs.Length > 0 || !string.IsNullOrWhiteSpace(script.CSharpCode))
                    script.CSharpCode = cs;
                script.IsPaused = _req.IsPaused;
            }, ct);
            Ok($"То же записано на сервер {stage.Title} (версия метаданных {version}).");
        }
        catch (Exception ex) when (ex is PilotGatewayException or WorkflowException or GitLabApiException)
        {
            Add("warn", $"Стейдж не обновлён: {ex.Message}. Тест и dev обновлены; повторите «Влить в тест» или запишите на стейдж со страницы автоматизации.");
        }
    }

    /// <summary>Ветка задачи / hotfix влита - удаляем её (не критично: останется в «Можно удалить»).</summary>
    private async Task DeleteSourceBranchAsync()
    {
        if (_req.SourceBranch is not { Length: > 0 } source)
            return;
        try
        {
            await _git.DeleteBranchAsync(Project, source, CancellationToken.None);
            Ok($"Ветка {source} влита в {Branch} и удалена.");
        }
        catch (GitLabApiException ex)
        {
            Add("warn", $"Ветку {source} удалить не удалось: {ex.Message}. Удалите её на странице автоматизации.");
        }
    }

    /// <summary>Сценарий по справочнику Pilot и метаданным сервера: ошибки блокируют, предупреждения - по настройке.</summary>
    private async Task LintAsync(string triggers, string cs, CancellationToken ct)
    {
        Automations.Validation.MetadataIndex? metadata = null;
        try
        {
            metadata = await _pilot.GetMetadataIndexAsync(_env.PilotUrl, _ctx.Pilot, ct);
        }
        catch (PilotGatewayException ex)
        {
            Add("warn", $"Проверка по базе сервера недоступна ({ex.Message}) - сверка только со справкой.");
        }

        var hosted = await _modules.GetHostedActivitiesAsync(_env.PilotUrl, _ctx.ModulesLogin ?? _ctx.Pilot, _ctx.Settings.ReportsLicenseType, ct);
        var issues = Automations.Validation.AutomationLinter.Lint(triggers, cs, _req.Name, metadata, hosted);
        var errors = issues.Where(i => i.IsError).ToList();
        var warnings = issues.Where(i => i.Level == "warn").ToList();
        string Format(IEnumerable<Automations.Validation.LintIssue> list) => string.Join("\n", list.Select(i => $"строка {i.Line}: {i.Message}"));

        if (errors.Count > 0)
            throw new WorkflowException($"Сценарий содержит ошибки ({errors.Count}). Автоматизация не отправлена.", Format(errors.Concat(warnings)));
        if (warnings.Count > 0 && _ctx.Settings.BlockPublishOnLintWarnings)
            throw new WorkflowException($"Проверка сценария дала предупреждения ({warnings.Count}), а отправка с предупреждениями запрещена настройками.", Format(warnings));
        if (warnings.Count > 0)
            Add("warn", $"Проверка сценария: предупреждений {warnings.Count}.", details: Format(warnings));
        else
            Ok("Сценарий проверен по справочнику Pilot" + (metadata is null ? "." : " и базе сервера - замечаний нет."));
    }

    /// <summary>После попадания версии в main - тег vX.Y.Z и GitLab Release на актуальном коммите main.</summary>
    private async Task TagReleaseIfMainAsync(string triggers, CancellationToken ct)
    {
        if (_req.Delete || Branch != AutomationRepositories.MainBranch)
            return;
        try
        {
            var head = await _git.FindBranchAsync(Project, Branch, ct);
            if (head?.Commit is null)
                return;
            var project = await _git.FindProjectAsync(Project, ct);
            var (level, text, url) = await AutomationRepositories.TagReleaseAsync(_git, Project, project?.WebUrl ?? "", AutomationVersion.Find(triggers),
                head.Commit.Id, _req.CommitMessage,
                $"{NotesMarkdown}\n\n- Сервер: {_env.Title} ({_env.PilotUrl})\n- Выполнил: {_job.GitLabUser ?? _job.PilotUser}\n- Дата: {DateTimeOffset.Now:dd.MM.yyyy HH:mm}", ct);
            Add(level, text, url);
        }
        catch (GitLabApiException ex)
        {
            Add("warn", $"Не удалось создать тег/Release: {ex.Message}");
        }
    }

    /// <summary>Пункты «что изменилось»; если форма их не передала - описание из сообщения коммита.</summary>
    private IReadOnlyList<string> Notes => _req.ChangeNotes.Count > 0
        ? _req.ChangeNotes
        : new[] { ChangelogSource.StripName(_req.CommitMessage, _req.Name) };

    private string NotesMarkdown => string.Join("\n", Notes.Select(n => "- " + n));

    private string FullCommitMessage => Notes.Count > 1 ? $"{_req.CommitMessage}\n\n{NotesMarkdown}" : _req.CommitMessage;

    private string? _mainChangelog;

    /// <summary>
    /// CHANGELOG.md в том же коммите: в dev - пункты в «Не выпущено» (выпущенные разделы сверяются с main),
    /// в main - новый раздел с версией.
    /// </summary>
    private async Task<GitLabCommitAction> ChangelogActionAsync(string triggers, CancellationToken ct)
    {
        var version = AutomationVersion.Find(triggers);
        var file = await _git.FindFileAsync(Project, Changelog.FileName, Branch, ct);
        var log = Changelog.Parse(file?.Text, _req.Name);

        if (Branch == AutomationRepositories.MainBranch)
        {
            log.Release(Changelog.ReleaseHeading(version, DateTimeOffset.Now, _req.TaskReference), Notes);
            _mainChangelog = log.Render();
            Info($"{Changelog.FileName}: раздел {log.Released[0].Heading}.");
        }
        else
        {
            var mainLog = await _git.FindFileAsync(Project, Changelog.FileName, AutomationRepositories.MainBranch, ct);
            var mainJson = await _git.FindFileAsync(Project, AutomationFiles.JsonPath(_req.Name), AutomationRepositories.MainBranch, ct);
            log.SyncReleased(Changelog.Parse(mainLog?.Text, _req.Name), AutomationVersion.Find(mainJson?.Text));
            log.AddUnreleased(Notes, version, $"{DateTimeOffset.Now:dd.MM.yyyy}, {_job.GitLabUser ?? _job.PilotUser}");
            Info($"{Changelog.FileName}: в «Не выпущено» пунктов - {log.Unreleased.Count}.");
        }

        var text = log.Render();
        return file is null ? GitLabCommitAction.Create(Changelog.FileName, text) : GitLabCommitAction.Update(Changelog.FileName, text);
    }

    /// <summary>
    /// После попадания в main - те же выпущенные разделы в dev, чтобы «Не выпущено» там не показывало уже перенесённое.
    /// Не критично: если dev защищена от push, журнал dev сверится при следующей отправке в dev.
    /// </summary>
    private async Task SyncDevChangelogAsync(string triggers, CancellationToken ct)
    {
        if (_mainChangelog is null)
            return;
        const string dev = AutomationRepositories.DevBranch;
        try
        {
            var devFile = await _git.FindFileAsync(Project, Changelog.FileName, dev, ct);
            if (devFile is null && await _git.FindBranchAsync(Project, dev, ct) is null)
                return;
            var log = Changelog.Parse(devFile?.Text, _req.Name);
            log.SyncReleased(Changelog.Parse(_mainChangelog, _req.Name), AutomationVersion.Find(triggers));
            var text = log.Render();
            if (devFile is not null && AutomationFiles.SameContent(devFile.Text, text))
                return;

            var action = devFile is null ? GitLabCommitAction.Create(Changelog.FileName, text) : GitLabCommitAction.Update(Changelog.FileName, text);
            var commit = await _git.CreateCommitAsync(Project, dev, null, $"{_req.Name}: {Changelog.FileName} - выпуск на прод", new[] { action }, ct);
            Ok($"{Changelog.FileName} в ветке {dev} сверен с {Branch} ({commit.ShortId}).", commit.WebUrl);
        }
        catch (GitLabApiException ex)
        {
            Info($"{Changelog.FileName} в ветке {dev} не обновлён ({ex.Message}) - он сверится при следующей отправке в {dev}.");
        }
    }

    private void CheckBase(string? expectedBlob, GitLabFile? file, string path)
    {
        if (expectedBlob is not null && (file?.BlobId ?? "") != expectedBlob)
            throw new WorkflowException($"Файл {path} в ветке {Branch} изменился после открытия редактора. Обновите страницу и повторите.");
    }

    private async Task ApplyToPilotAsync(AutomationSnapshot? current, string triggers, string cs, CancellationToken ct)
    {
        if (_req.Delete)
        {
            if (current is null)
            {
                Info($"На сервере {_env.Title} автоматизации нет - удалять нечего.");
                return;
            }
        }
        else if (current is not null &&
                 AutomationFiles.SameContent(current.Triggers, triggers) &&
                 AutomationFiles.SameContent(current.CSharpCode, cs) &&
                 current.IsPaused == _req.IsPaused)
        {
            Ok($"На сервере {_env.Title} уже эта версия - запись не требуется.");
            return;
        }

        Info($"Запись на сервер {_env.Title}…");
        var version = await _pilot.UpdateAutomationsAsync(_env.PilotUrl, _ctx.Pilot, list =>
        {
            var index = list.FindIndex(a => a.Name == _req.Name);
            if (_req.Delete)
            {
                if (index >= 0) list.RemoveAt(index);
                return;
            }

            var script = index >= 0 ? list[index] : new AutomationScript { Name = _req.Name };
            if (index < 0) list.Add(script);
            script.Triggers = triggers;
            if (cs.Length > 0 || !string.IsNullOrWhiteSpace(script.CSharpCode))
                script.CSharpCode = cs;
            script.IsPaused = _req.IsPaused;
        }, ct);

        Ok(_req.Delete
            ? $"Автоматизация удалена с сервера {_env.Title} (версия метаданных {version})."
            : $"Автоматизация сохранена на сервере {_env.Title} (версия метаданных {version}).");
    }

    private async Task RollbackPilotAsync(AutomationSnapshot? previous, CancellationToken ct)
    {
        try
        {
            await _pilot.UpdateAutomationsAsync(_env.PilotUrl, _ctx.Pilot, list =>
            {
                var index = list.FindIndex(a => a.Name == _req.Name);
                if (previous is null)
                {
                    if (index >= 0) list.RemoveAt(index);
                    return;
                }

                var script = index >= 0 ? list[index] : new AutomationScript { Name = previous.Name };
                if (index < 0) list.Insert(Math.Clamp(previous.Index, 0, list.Count), script);
                script.Triggers = previous.Triggers;
                script.CSharpCode = previous.CSharpCode;
                script.IsPaused = previous.IsPaused;
            }, ct);
            Ok($"Сервер {_env.Title} возвращён к предыдущему состоянию.");
        }
        catch (Exception ex)
        {
            Error($"ОТКАТ НЕ УДАЛСЯ - сервер {_env.Title} расходится с веткой {Branch}: {ex.Message}");
        }
    }

    private static string FormatDiagnostics(ScriptValidationResult result) =>
        result.Diagnostics is { Count: > 0 }
            ? string.Join("\n", result.Diagnostics.Select(d => $"[{d.Severity}] {d.Location}: {d.Message}"))
            : "(сервер не вернул диагностику)";

    private static string ActionRu(string action) => action switch
    {
        "create" => "добавлен",
        "update" => "изменён",
        "delete" => "удалён",
        _ => action,
    };

    private void Report(CheckMessage message)
    {
        if (message.Level == "error")
            throw new WorkflowException(message.Text, message.Details);
        Add(message.Level, message.Text, details: message.Details);
    }

    private void Info(string text, string? url = null) => Add("info", text, url);
    private void Ok(string text, string? url = null) => Add("ok", text, url);
    private void Error(string text) => Add("error", text);
    private void Add(string level, string text, string? url = null, string? details = null) => _job.Add(level, text, url, details);

    private static async Task SafeAsync(Func<Task> action)
    {
        try { await action(); }
        catch { /* очистка - не критично */ }
    }
}
