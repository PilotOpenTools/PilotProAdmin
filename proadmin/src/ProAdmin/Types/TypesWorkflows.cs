using ProAdmin.Automations;
using ProAdmin.GitLab;
using ProAdmin.Jobs;
using ProAdmin.Modules;
using ProAdmin.Pilot;
using ProAdmin.Settings;

namespace ProAdmin.Types;

/// <summary>Всё, что нужно фоновой операции над типами от сессии пользователя.</summary>
public sealed class TypesContext
{
    public required AdminSettings Settings { get; init; }
    public required GitLabToken GitLabToken { get; init; }
    public required string ProjectPath { get; init; }
    public string? ProjectWebUrl { get; init; }

    /// <summary>Учётки администратора сервера Pilot по контурам (test / stage / prod).</summary>
    public required IReadOnlyDictionary<string, PilotLogin> Logins { get; init; }

    public PilotLogin Login(string env) =>
        Logins.TryGetValue(env, out var l) ? l : throw new WorkflowException($"Нет учётки администратора Pilot для контура {env}.");
}

/// <summary>Общая часть операций над репозиторием типов: запись на сервер, MR, pipeline, версия.</summary>
internal abstract class TypesWorkflowBase
{
    protected readonly PilotGateway Pilot;
    protected readonly GitLabClient Git;
    protected readonly PublishJob Job;
    protected readonly TypesContext Ctx;
    protected readonly TypesStore Store;

    protected TypesWorkflowBase(PilotGateway pilot, GitLabClient git, PublishJob job, TypesContext ctx, TypesStore store)
    {
        Pilot = pilot;
        Git = git;
        Job = job;
        Ctx = ctx;
        Store = store;
    }

    protected string Project => Ctx.ProjectPath;

    public abstract Task RunAsync(CancellationToken ct);

    protected TypesEnv Env(string branch) =>
        TypesEnv.ForBranch(Ctx.Settings, branch) is { HasServer: true } env
            ? env
            : throw new WorkflowException($"В настройках не задан сервер Pilot для ветки {branch}.");

    protected async Task<string> HeadAsync(string branch, CancellationToken ct) =>
        (await Git.FindBranchAsync(Project, branch, ct))?.Commit?.Id ?? throw new WorkflowException($"В {Project} нет ветки {branch}.");

    protected Task<TypesSnapshot> LoadAsync(string reference, CancellationToken ct) => TypesRepository.LoadAsync(Git, Project, reference, ct);

    /// <summary>Проверка содержимого ветки без сервера; ошибки - исключение.</summary>
    protected void Validate(TypesSnapshot snapshot, string what, string notSaved)
    {
        var v = TypesValidator.Validate(snapshot);
        foreach (var w in v.Warnings.Take(10))
            Add("warn", w);
        if (!v.Ok)
            throw new WorkflowException($"Проверка {what}: ошибок - {v.Errors.Count}. {notSaved}", string.Join("\n", v.Errors));
        Ok($"Проверка {what}: типов {snapshot.Types.Count}, состояний {snapshot.States.Count}, групп состояний {snapshot.Machines.Count} - ошибок нет.");
    }

    /// <summary>Запись ветки на сервер контура; журнал изменений - в операцию.</summary>
    protected async Task<TypesPlan> ApplyAsync(TypesEnv env, TypesSnapshot snapshot, string reference, string sha, CancellationToken ct)
    {
        Info($"Запись {reference} @ {ModuleDeploy.Short(sha)} на сервер {env.Title}…");
        var plan = await Pilot.ApplyTypesAsync(env.PilotUrl, Ctx.Login(env.Key), snapshot, ct);
        Report(plan, env);
        if (!plan.CanApply)
            throw new WorkflowException($"Ветку {reference} нельзя записать на сервер {env.Title}: ошибок - {plan.Errors.Count}. Сервер не изменён.",
                string.Join("\n", plan.Errors));
        Store.RecordDeployment(new TypesDeployment
        {
            Env = env.Key, Reference = reference, Sha = sha, Version = snapshot.Config.Version, At = DateTimeOffset.UtcNow,
            By = Job.PilotUser, Changes = plan.Changes.Count,
        });
        return plan;
    }

    protected void Report(TypesPlan plan, TypesEnv env)
    {
        foreach (var w in plan.Warnings.Take(10))
            Add("warn", w);
        if (!plan.CanApply)
            return;
        if (!plan.HasChanges)
        {
            Ok($"Сервер {env.Title} уже совпадает с веткой - изменений нет.");
            return;
        }
        Ok($"Сервер {env.Title}: записано изменений - {plan.Changes.Count}.", details: string.Join("\n", plan.Changes.Select(c => (c.Danger ? "Внимание " : "") + c.Line)));
        foreach (var c in plan.Changes.Where(c => c.Danger))
            Add("warn", c.Line);
        if (plan.Unmanaged.Count > 0)
            Info($"На сервере есть элементы не из репозитория - не тронуты: {plan.Unmanaged.Count}.", details: string.Join("\n", plan.Unmanaged));
    }

    protected async Task RevertAsync(TypesEnv env, TypesPlan plan)
    {
        if (!plan.HasChanges)
            return;
        try
        {
            var notes = await Pilot.RevertTypesAsync(env.PilotUrl, Ctx.Login(env.Key), plan, CancellationToken.None);
            Ok($"Сервер {env.Title}: изменения типов откатаны.");
            foreach (var n in notes)
                Add("warn", n);
        }
        catch (Exception ex)
        {
            Add("error", $"ОТКАТ ТИПОВ НЕ УДАЛСЯ - сервер {env.Title} расходится с веткой: {ex.Message}");
        }
    }

    /// <summary>Ждёт pipeline MR, если в репозитории есть .gitlab-ci.yml (или разрешено без него).</summary>
    protected async Task WaitPipelineAsync(GitLabMergeRequest mr, string sha, string notSaved, CancellationToken ct)
    {
        var tree = await Git.ListTreeAsync(Project, mr.SourceBranch ?? TypesBranches.Dev, ct) ?? new();
        if (tree.Any(t => t.Path == TypesCiTemplate.CiFile))
        {
            Job.Update(j => j.Status = JobStatus.WaitingPipeline);
            await Flow(notSaved).WaitForPipelinesAsync(mr.Iid, sha, ct);
            Job.Update(j => j.Status = JobStatus.Running);
        }
        else if (Ctx.Settings.AllowPublishWithoutCi)
        {
            Add("warn", $"В репозитории нет {TypesCiTemplate.CiFile} - pipeline не ожидается (разрешено в настройках).");
        }
        else
        {
            throw new WorkflowException($"В репозитории нет {TypesCiTemplate.CiFile}, а работа без pipeline запрещена в настройках. {notSaved}");
        }
    }

    protected MergeRequestFlow Flow(string notSaved) => new(Git, Project, Job, Ctx.Settings, notSaved);

    protected async Task CleanupAsync(long? mrIid, string? branch)
    {
        if (mrIid is { } iid)
            try { await Git.CloseMergeRequestAsync(Project, iid, CancellationToken.None); } catch { /* уборка */ }
        if (branch is not null)
            try { await Git.DeleteBranchAsync(Project, branch, CancellationToken.None); } catch { /* уборка */ }
    }

    /// <summary>Тег vX.Y.Z и Release на коммите main.</summary>
    protected async Task TagAsync(ModuleVersion version, string sha, IReadOnlyList<string> notes, string? task, CancellationToken ct)
    {
        var tag = TypesBranches.ProdTag(version);
        try
        {
            if (await Git.FindTagAsync(Project, tag, ct) is not null)
            {
                Add("warn", $"Тег {tag} уже есть - новый не создан.");
                return;
            }
            await Git.CreateTagAsync(Project, tag, sha, $"Типы {version}", ct);
            await Git.CreateReleaseAsync(Project, tag, $"Типы {version}",
                $"## Типы {version}\n\n{string.Join("\n", notes.Select(n => "- " + n))}\n\n" +
                (task is null ? "" : $"Задача: {task}\n\n") + $"Выпустил: {Job.PilotUser} (ProAdmin).", ct);
            Ok($"Релиз {tag} в GitLab.", Ctx.ProjectWebUrl is null ? null : $"{Ctx.ProjectWebUrl}/-/releases/{Uri.EscapeDataString(tag)}");
        }
        catch (GitLabApiException ex)
        {
            Add("warn", $"Не удалось создать тег {tag}: {ex.Message}");
        }
    }

    protected void Info(string text, string? url = null, string? details = null) => Add("info", text, url, details);
    protected void Ok(string text, string? url = null, string? details = null) => Add("ok", text, url, details);
    protected void Add(string level, string text, string? url = null, string? details = null) => Job.Add(level, text, url, details);
}

// ======================================================================================

/// <summary>
/// Ветка изменения / dev / тест: MR из ветки types/… в dev, pipeline, squash-merge одним коммитом
/// (его потом переносят на стейдж и прод отдельно от других или вместе со связанным модулем), запись головы dev на тестовый сервер.
/// </summary>
internal sealed class TypesMergeBranchWorkflow : TypesWorkflowBase
{
    private readonly string _branch;
    private readonly string _message;

    public TypesMergeBranchWorkflow(PilotGateway pilot, GitLabClient git, PublishJob job, TypesContext ctx, TypesStore store,
        string branch, string message) : base(pilot, git, job, ctx, store)
    {
        _branch = branch;
        _message = message;
    }

    public override async Task RunAsync(CancellationToken ct)
    {
        const string notSaved = "Изменения НЕ попали в dev.";
        var branchHead = await HeadAsync(_branch, ct);
        var commits = (await Git.CompareCommitsAsync(Project, TypesBranches.Dev, _branch, ct)).Where(c => c.ParentIds is not { Count: > 1 }).ToList();
        if (commits.Count == 0)
            throw new WorkflowException($"В ветке {_branch} нет изменений относительно dev.");
        Info($"Ветка {_branch}: правок - {commits.Count}.", details: string.Join("\n", commits.Select(c => $"{c.ShortId} {c.Title}")));

        Validate(await LoadAsync(branchHead, ct), $"ветки {_branch}", notSaved);
        var env = Env(TypesBranches.Dev);

        var existing = (await Git.ListOpenMergeRequestsAsync(Project, _branch, TypesBranches.Dev, ct)).FirstOrDefault();
        var mr = existing ?? await Git.CreateMergeRequestAsync(Project, _branch, TypesBranches.Dev, _message,
            $"{string.Join("\n", commits.Select(c => "- " + c.Title))}\n\n---\n\nСоздано ProAdmin (типы).\n\n- Pilot: {Job.PilotUser}", ct,
            removeSourceBranch: true, squash: true);
        Job.Update(j => j.MergeRequestUrl = mr.WebUrl);
        Ok(existing is null ? $"Создан Merge Request !{mr.Iid} {_branch} / dev." : $"Используется открытый Merge Request !{mr.Iid}.", mr.WebUrl);

        await WaitPipelineAsync(mr, branchHead, notSaved, ct);
        var baseSha = await HeadAsync(TypesBranches.Dev, ct);
        var mergeRef = await Git.GetMergeRefAsync(Project, mr.Iid, ct);
        var plan = await Pilot.PlanTypesAsync(env.PilotUrl, Ctx.Login(env.Key), await LoadAsync(mergeRef, ct), ct);
        if (!plan.CanApply)
            throw new WorkflowException($"Запись результата слияния на сервер {env.Title} невозможна. {notSaved}", string.Join("\n", plan.Errors));
        if (await HeadAsync(TypesBranches.Dev, ct) != baseSha)
            throw new WorkflowException($"В dev появились новые коммиты во время проверки. Повторите операцию. {notSaved}");

        await Flow(notSaved).MergeAsync(mr.Iid, branchHead, TypesBranches.Dev, ct, removeSourceBranch: true, squashMessage: _message);
        var merged = await Git.GetMergeRequestAsync(Project, mr.Iid, ct);
        var sha = merged.SquashCommitSha ?? merged.MergeCommitSha ?? await HeadAsync(TypesBranches.Dev, ct);
        Store.BranchMerged(_branch, sha, _message);
        Ok($"В dev один коммит {ModuleDeploy.Short(sha)}: «{_message}».");

        var head = await HeadAsync(TypesBranches.Dev, ct);
        try
        {
            await ApplyAsync(env, await LoadAsync(head, ct), TypesBranches.Dev, head, ct);
        }
        catch (Exception ex) when (ex is WorkflowException or PilotGatewayException)
        {
            throw new WorkflowException($"Изменения влиты в dev, но тестовый сервер не обновлён: {ex.Message} " +
                                        "Исправьте и нажмите «Записать dev на сервер» на вкладке «Типы».", (ex as WorkflowException)?.Details);
        }
    }
}

/// <summary>Записать голову ветки на её сервер (dev / тест, release / стейдж, main / прод) - когда сервер разошёлся с веткой.</summary>
internal sealed class TypesSyncWorkflow : TypesWorkflowBase
{
    private readonly string _branch;

    public TypesSyncWorkflow(PilotGateway pilot, GitLabClient git, PublishJob job, TypesContext ctx, TypesStore store, string branch)
        : base(pilot, git, job, ctx, store) => _branch = branch;

    public override async Task RunAsync(CancellationToken ct)
    {
        var head = await HeadAsync(_branch, ct);
        await ApplyAsync(Env(_branch), await LoadAsync(head, ct), _branch, head, ct);
    }
}

/// <summary>
/// Перенос выбранных коммитов: dev / release (стейдж) или release / main (прод).
/// Ветка types-promote/… от целевой, cherry-pick выбранных (с пометкой «cherry picked from commit»), для main - версия
/// в config.json и раздел CHANGELOG.md; MR / pipeline / запись на сервер / merge (не удался - откат сервера) / тег.
/// Выпуск модуля со связанными изменениями проходит те же шаги по отдельности (<see cref="TypesFollower"/>):
/// запись на прод - перед модулем, merge - после него.
/// </summary>
internal sealed class TypesPromoteWorkflow : TypesWorkflowBase
{
    private readonly string _target;
    private readonly IReadOnlyCollection<string> _selected;
    private readonly ModuleVersion? _version;
    private readonly IReadOnlyList<string> _notes;
    private readonly string? _task;

    /// <summary>Подготовленный перенос: ветка с cherry-pick'ами, MR с пройденным pipeline, содержимое для записи.</summary>
    public sealed class Prepared
    {
        public required TypesEnv Env { get; init; }
        public required string Branch { get; init; }
        public required string BaseSha { get; init; }
        public required string Head { get; init; }
        public required GitLabMergeRequest Mr { get; init; }
        public required TypesSnapshot Snapshot { get; init; }
        public required string NotSaved { get; init; }
    }

    public TypesPromoteWorkflow(PilotGateway pilot, GitLabClient git, PublishJob job, TypesContext ctx, TypesStore store,
        string target, IReadOnlyCollection<string> selected, ModuleVersion? version, IReadOnlyList<string> notes, string? task)
        : base(pilot, git, job, ctx, store)
    {
        _target = target;
        _selected = selected;
        _version = version;
        _notes = notes;
        _task = task;
    }

    public override async Task RunAsync(CancellationToken ct)
    {
        var p = await PrepareAsync(ct);
        var done = false;
        try
        {
            var plan = await ApplyPreparedAsync(p, ct);
            await MergePreparedAsync(p, plan, revertOnFail: true, ct);
            done = true;
        }
        finally
        {
            if (!done)
                await CleanupAsync(p.Mr.Iid, p.Branch);
        }
        Ok($"Перенесено в {_target} и записано на {p.Env.Title}.");
    }

    /// <summary>Ветка переноса, cherry-pick, версия (main), проверка, MR и pipeline. Сервер и целевая ветка не меняются.</summary>
    public async Task<Prepared> PrepareAsync(CancellationToken ct)
    {
        var source = TypesBranches.SourceOf(_target) ?? throw new WorkflowException($"В ветку {_target} коммиты не переносятся.");
        var env = Env(_target);
        var isMain = _target == TypesBranches.Main;
        var notSaved = $"Сервер {env.Title} не изменён, {_target} не изменена.";
        if (isMain && _version is null)
            throw new WorkflowException("Для выпуска в main нужна версия X.Y.Z.");

        var pending = await TypesRepository.PendingAsync(Git, Project, source, _target, ct);
        var chosen = pending.Where(p => _selected.Contains(p.Commit.Id)).Select(p => p.Commit).ToList();
        if (chosen.Count == 0)
            throw new WorkflowException($"Выбранные коммиты уже перенесены в {_target} или не найдены в {source}.");
        if (chosen.Count < _selected.Count)
            Add("warn", $"Часть выбранных коммитов уже перенесена в {_target} - переносится {chosen.Count}.");
        var skipped = pending.Count - chosen.Count;
        Info($"Перенос {source} / {_target}: коммитов {chosen.Count}" + (skipped > 0 ? $", остаются в {source}: {skipped}." : "."),
            details: string.Join("\n", chosen.Select(c => $"{c.ShortId} {c.Title}")));

        var baseSha = await HeadAsync(_target, ct);
        var branch = TypesBranches.Promote(_target);
        await Git.CreateBranchAsync(Project, branch, baseSha, ct);
        long? mrIid = null;
        try
        {
            foreach (var c in chosen) // старые первыми, как в исходной ветке
            {
                try
                {
                    var picked = await Git.CherryPickAsync(Project, c.Id, branch, TypesRepository.PickMessage(c), ct);
                    Ok($"cherry-pick {c.ShortId} «{c.Title}» / {picked.ShortId}");
                }
                catch (GitLabApiException ex)
                {
                    throw new WorkflowException($"cherry-pick {c.ShortId} «{c.Title}» не удался: {ex.Message}. Коммит меняет те же файлы, что и " +
                                                $"невыбранный, или конфликтует с {_target} - добавьте в перенос коммиты, от которых он зависит. {notSaved}");
                }
            }

            if (isMain)
                await CommitVersionAsync(branch, _version!, ct);

            var head = await HeadAsync(branch, ct);
            var snapshot = await LoadAsync(head, ct);
            Validate(snapshot, $"ветки {branch}", notSaved);

            var title = isMain ? $"Типы {_version}" + (_task is null ? "" : $" - {_task}") : $"Типы / {_target}: {chosen.Count} изм.";
            var description = $"## {title}\n\n{string.Join("\n", chosen.Select(c => $"- {c.Title} (`{c.ShortId}`)"))}\n" +
                              (_notes.Count > 0 && isMain ? $"\n### CHANGELOG\n\n{string.Join("\n", _notes.Select(n => "- " + n))}\n" : "") +
                              $"\n---\n\nСоздано ProAdmin (перенос типов).\n\n- Pilot: {Job.PilotUser}";
            var mr = await Git.CreateMergeRequestAsync(Project, branch, _target, title, description, ct, removeSourceBranch: true, squash: false);
            mrIid = mr.Iid;
            Job.Update(j => j.MergeRequestUrl ??= mr.WebUrl);
            Ok($"Создан Merge Request !{mr.Iid} {branch} / {_target}.", mr.WebUrl);

            await WaitPipelineAsync(mr, head, notSaved, ct);
            return new Prepared { Env = env, Branch = branch, BaseSha = baseSha, Head = head, Mr = mr, Snapshot = snapshot, NotSaved = notSaved };
        }
        catch
        {
            await CleanupAsync(mrIid, branch);
            throw;
        }
    }

    /// <summary>Запись подготовленного переноса на сервер (целевая ветка не должна была уйти вперёд).</summary>
    public async Task<TypesPlan> ApplyPreparedAsync(Prepared p, CancellationToken ct)
    {
        if (await HeadAsync(_target, ct) != p.BaseSha)
            throw new WorkflowException($"В {_target} появились новые коммиты, пока шла проверка. Повторите перенос. {p.NotSaved}");
        return await ApplyAsync(p.Env, p.Snapshot, _target == TypesBranches.Main ? $"v{_version}" : _target, p.Head, ct);
    }

    /// <summary>Merge MR переноса и тег (main). Не влилось: <paramref name="revertOnFail"/> - сервер откатывается.</summary>
    public async Task MergePreparedAsync(Prepared p, TypesPlan plan, bool revertOnFail, CancellationToken ct)
    {
        try
        {
            await Flow(p.NotSaved).MergeAsync(p.Mr.Iid, p.Head, _target, ct, removeSourceBranch: true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Add("error", $"Merge Request типов не влит: {ex.Message}");
            if (!revertOnFail)
                throw new WorkflowException($"Типы записаны на {p.Env.Title}, но GitLab не принял их в {_target}: {ex.Message}. " +
                                            $"Влейте Merge Request !{p.Mr.Iid} вручную.");
            await RevertAsync(p.Env, plan);
            throw new WorkflowException($"GitLab не принял перенос в {_target}: {ex.Message}. Сервер {p.Env.Title} откатан.");
        }
        if (_target == TypesBranches.Main)
            await TagAsync(_version!, await HeadAsync(_target, ct), _notes, _task, ct);
    }

    /// <summary>Отменить подготовленный перенос: вернуть сервер (если записан), закрыть MR, удалить ветку.</summary>
    public async Task AbortAsync(Prepared p, TypesPlan? plan)
    {
        if (plan is not null)
            await RevertAsync(p.Env, plan);
        await CleanupAsync(p.Mr.Iid, p.Branch);
    }

    /// <summary>Версия в config.json и раздел CHANGELOG.md - служебный коммит, в другие ветки не переносится.</summary>
    private async Task CommitVersionAsync(string branch, ModuleVersion version, CancellationToken ct)
    {
        var actions = new List<GitLabCommitAction>();
        var configFile = await Git.FindFileAsync(Project, TypesSnapshot.ConfigFile, branch, ct);
        var config = configFile is null ? new TypesConfigInfo() : TypesJson.Read<TypesConfigInfo>(configFile.Text, TypesSnapshot.ConfigFile);
        if (ModuleVersion.TryParse(config.Version, out var current) && current! >= version)
            throw new WorkflowException($"Версия {version} не выше текущей версии main ({current}).");
        config.Version = version.ToString();
        actions.Add(configFile is null
            ? GitLabCommitAction.Create(TypesSnapshot.ConfigFile, TypesJson.Write(config))
            : GitLabCommitAction.Update(TypesSnapshot.ConfigFile, TypesJson.Write(config)));

        var logFile = await Git.FindFileAsync(Project, Changelog.FileName, branch, ct);
        var log = Changelog.Parse(logFile?.Text, "Типы Pilot");
        var heading = $"[{version}] - {DateTimeOffset.Now:yyyy-MM-dd}" + (string.IsNullOrWhiteSpace(_task) ? "" : $" - {_task!.Trim()}");
        log.Release(heading, _notes.Count > 0 ? _notes : new[] { "Изменения типов" });
        var rendered = log.Render();
        actions.Add(logFile is null ? GitLabCommitAction.Create(Changelog.FileName, rendered) : GitLabCommitAction.Update(Changelog.FileName, rendered));

        var commit = await Git.CreateCommitAsync(Project, branch, null, $"{TypesRepository.ReleaseCommitPrefix}{version}: config.json и {Changelog.FileName}", actions, ct);
        Ok($"Версия {version} и раздел {Changelog.FileName} (коммит {commit.ShortId}).", commit.WebUrl);
    }
}

/// <summary>
/// Первое заполнение репозитория: снимок базы выбранного контура / main (версия 1.0.0, тег v1.0.0),
/// ветки release и dev от main, защита веток (только Merge Request), CI-проверка.
/// </summary>
internal sealed class TypesInitWorkflow : TypesWorkflowBase
{
    private readonly string _sourceEnv;

    public TypesInitWorkflow(PilotGateway pilot, GitLabClient git, PublishJob job, TypesContext ctx, TypesStore store, string sourceEnv)
        : base(pilot, git, job, ctx, store) => _sourceEnv = sourceEnv;

    public override async Task RunAsync(CancellationToken ct)
    {
        var project = await Git.FindProjectAsync(Project, ct) ?? throw new WorkflowException($"Проект {Project} не найден в GitLab.");
        var env = TypesEnv.Find(Ctx.Settings, _sourceEnv) is { HasServer: true } e ? e : throw new WorkflowException($"Не задан сервер контура {_sourceEnv}.");

        var main = await Git.FindBranchAsync(Project, TypesBranches.Main, ct);
        if (main is not null && await Git.FindFileAsync(Project, TypesSnapshot.ConfigFile, TypesBranches.Main, ct) is not null)
        {
            Info($"В {TypesBranches.Main} уже есть {TypesSnapshot.ConfigFile} - репозиторий инициализирован, создаются только недостающие ветки.");
        }
        else
        {
            Info($"Чтение типов с сервера {env.Title}…");
            var metadata = await Pilot.GetMetadataAsync(env.PilotUrl, Ctx.Login(env.Key), ct);
            var snapshot = TypesExport.FromMetadata(metadata, "1.0.0");
            var files = snapshot.Render();
            files[TypesCiTemplate.CiFile] = TypesCiTemplate.Ci;
            files[TypesCiTemplate.ValidatorFile] = TypesCiTemplate.Validator;
            files[".gitattributes"] = TypesCiTemplate.GitAttributes;
            files["README.md"] = TypesCiTemplate.Readme(project.Name);
            var log = Changelog.Parse(null, "Типы Pilot");
            log.Release($"[1.0.0] - {DateTimeOffset.Now:yyyy-MM-dd}", new[] { $"Начальное состояние - снимок сервера {env.Title}" });
            files[Changelog.FileName] = log.Render();

            // main ещё нет - коммит стартует от ветки по умолчанию, значит и существующие файлы надо смотреть в ней.
            var startBranch = main is null && project.DefaultBranch is { } d && await Git.FindBranchAsync(Project, d, ct) is not null ? d : null;
            var treeBranch = main is null ? startBranch : TypesBranches.Main;
            var tree = treeBranch is null ? new List<GitLabTreeItem>() : await Git.ListTreeAsync(Project, treeBranch, ct) ?? new();
            var existing = tree.Where(t => t.Type == "blob").Select(t => t.Path).ToHashSet(StringComparer.Ordinal);
            var actions = files.Select(f => existing.Contains(f.Key) ? GitLabCommitAction.Update(f.Key, f.Value) : GitLabCommitAction.Create(f.Key, f.Value)).ToList();
            var commit = await Git.CreateCommitAsync(Project, TypesBranches.Main, startBranch,
                $"{TypesRepository.InitCommitPrefix} (снимок сервера {env.Title})", actions, ct);
            Ok($"{TypesBranches.Main}: типов {snapshot.Types.Count}, состояний {snapshot.States.Count}, групп состояний {snapshot.Machines.Count} (коммит {commit.ShortId}).", commit.WebUrl);
            await TagAsync(new ModuleVersion(1, 0, 0), commit.Id, new[] { $"Начальное состояние - снимок сервера {env.Title}" }, null, ct);
            Store.RecordDeployment(new TypesDeployment
            {
                Env = env.Key, Reference = TypesBranches.Main, Sha = commit.Id, Version = "1.0.0", At = DateTimeOffset.UtcNow, By = Job.PilotUser,
            });
        }

        foreach (var branch in new[] { TypesBranches.Release, TypesBranches.Dev })
        {
            if (await Git.FindBranchAsync(Project, branch, ct) is not null)
                continue;
            await Git.CreateBranchAsync(Project, branch, TypesBranches.Main, ct);
            Ok($"Создана ветка {branch} от {TypesBranches.Main}.");
        }

        foreach (var branch in new[] { TypesBranches.Main, TypesBranches.Release, TypesBranches.Dev })
        {
            try
            {
                // push - никому, merge - Developer+: изменения только через Merge Request админки.
                if (await Git.ProtectBranchAsync(Project, branch, 0, 30, ct))
                    Ok($"Ветка {branch} защищена: только Merge Request.");
            }
            catch (GitLabApiException ex)
            {
                Add("warn", $"Не удалось защитить {branch}: {ex.Message}. Настройте Protected branches вручную (push: No one).");
            }
        }

        Info("Дальше: сравните ветки с серверами на вкладке «Типы». Если тест или стейдж отличаются от прода - «Сервер Тест / ветка изменения» (тест) " +
             "или «Записать ветку на сервер».");
    }
}
