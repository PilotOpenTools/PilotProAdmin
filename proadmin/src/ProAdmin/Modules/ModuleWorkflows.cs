using ProAdmin.Automations;
using ProAdmin.GitLab;
using ProAdmin.Jobs;
using ProAdmin.Pilot;
using ProAdmin.Settings;
using ProAdmin.Types;

namespace ProAdmin.Modules;

/// <summary>Всё, что нужно фоновой операции над модулем от сессии пользователя.</summary>
public sealed class ModuleContext
{
    public required AdminSettings Settings { get; init; }
    public required GitLabToken GitLabToken { get; init; }
    public required string ProjectPath { get; init; }
    public required string RepoName { get; init; }
    public string? ProjectWebUrl { get; init; }
    public string? CiConfigPath { get; init; }
    public string? ProjectFile { get; init; }
    public string? AssemblyInfoFile { get; init; }

    /// <summary>Клиентский или серверный модуль: группа GitLab, главная сборка, куда выкладывается zip.</summary>
    public ModuleKind Kind { get; init; } = ModuleKind.Client;

    /// <summary>Учётки Pilot по контурам (test / stage / prod).</summary>
    public required IReadOnlyDictionary<string, PilotLogin> Logins { get; init; }

    /// <summary>Спецификация библиотек модулей этого вида на момент запуска операции; null - без проверки библиотек.</summary>
    public AssemblySpec? Assemblies { get; init; }

    /// <summary>Репозиторий типов: изменения, связанные с коммитами модуля, уходят на стейдж и прод вместе с ним; null - без типов.</summary>
    public TypesFollowDeps? Types { get; init; }

    /// <summary>Отключённые модули (только клиентские): такой модуль не выкладывается, иначе появился бы второй объект.</summary>
    public DisabledModuleStore? Disabled { get; init; }

    public PilotLogin Login(string env) =>
        Logins.TryGetValue(env, out var l) ? l : throw new WorkflowException($"Нет учётки Pilot для контура {env}.");

    public TypesTargetKind TypesKind => Kind == ModuleKind.Server ? TypesTargetKind.Server : TypesTargetKind.Client;
}

/// <summary>Общая часть сценариев модулей: выкладка артефакта на сервер, откат, журнал, теги, CHANGELOG.</summary>
internal abstract class ModuleWorkflowBase
{
    protected readonly IModuleTarget Pilot;
    protected readonly GitLabClient Git;
    protected readonly PublishJob Job;
    protected readonly ModuleContext Ctx;

    protected ModuleWorkflowBase(IModuleTarget pilot, GitLabClient git, PublishJob job, ModuleContext ctx)
    {
        Pilot = pilot;
        Git = git;
        Job = job;
        Ctx = ctx;
    }

    protected string Project => Ctx.ProjectPath;
    protected int License => Ctx.Settings.ReportsLicenseType;
    protected ModuleArtifacts Artifacts => new(Git, Project, Ctx.Settings, Job, Ctx.Kind);
    protected MergeRequestFlow Flow(string notSaved) => new(Git, Project, Job, Ctx.Settings, notSaved);

    public abstract Task RunAsync(CancellationToken ct);

    protected ModuleEnv Env(string key) =>
        ModuleEnv.Find(Ctx.Settings, key) ?? throw new WorkflowException($"В настройках не задан сервер Pilot для контура «{key}».");

    protected async Task<GitLabBranch> BranchAsync(string name, CancellationToken ct) =>
        await Git.FindBranchAsync(Project, name, ct) ?? throw new WorkflowException($"В {Project} нет ветки {name}.");

    protected async Task<string> HeadAsync(string branch, CancellationToken ct) =>
        (await BranchAsync(branch, ct)).Commit?.Id ?? throw new WorkflowException($"Не удалось прочитать ветку {branch}.");

    /// <summary>Выложенная версия и что нужно для отката.</summary>
    protected sealed record Deployed(ModuleEnv Env, PilotModule? Previous, byte[]? PreviousZip, Guid? CreatedId, bool Changed);

    /// <summary>
    /// Выкладка zip из CI на сервер контура: новая версия объекта Extension (или новый объект, если модуля там нет).
    /// Если на сервере уже этот коммит - ничего не делает.
    /// </summary>
    protected async Task<Deployed> DeployAsync(ModuleEnv env, string reference, string sha, ModulePackageInfo package,
        IReadOnlyList<string> notes, CancellationToken ct, string? reasonPrefix = null)
    {
        Add("info", $"Чтение модулей сервера {env.Title}…");
        var list = await Pilot.ListAsync(env.PilotUrl, Ctx.Login(env.Key), License, ct);
        await CheckAssembliesAsync(env, package, list, ct);
        var existing = MatchPackage(list.Modules, package);
        if (existing is null && Ctx.Disabled is { } disabled &&
            MatchPackage(disabled.For(env.Key).Select(m => m.ToModule()), package) is { } off)
            throw new WorkflowException($"Модуль «{off.Name}» отключён на сервере {env.Title}: {off.Reason}. " +
                                        "Включите его на странице модуля и повторите выкладку. Модуль НЕ выложен.");
        var reason = (reasonPrefix ?? "") + ModuleDeploy.Reason(package.Version, reference, sha, notes);

        if (existing is not null && ModuleDeploy.SameCommit(existing.DeployedSha, sha))
        {
            Ok($"На сервере {env.Title} уже этот коммит ({ModuleDeploy.Short(sha)}) - выкладка не нужна.");
            return new Deployed(env, existing, null, null, false);
        }

        if (existing is not null)
        {
            var previousZip = await Pilot.DownloadAsync(env.PilotUrl, Ctx.Login(env.Key), License, existing, ct);
            Add("info", $"Запись новой версии модуля на сервер {env.Title} (было: {existing.Version ?? "?"})…");
            await Pilot.UpdateAsync(env.PilotUrl, Ctx.Login(env.Key), License, existing.Id, package, reason, ct);
            Ok($"Сервер {env.Title}: «{package.ExtensionName}» {package.Version} ({reference} @ {ModuleDeploy.Short(sha)}). " +
               Pilot.AfterUpdateNote);
            return new Deployed(env, existing, previousZip, null, true);
        }

        var folder = list.RootFolderId ?? throw new WorkflowException($"На сервере {env.Title} нет папки расширений.");
        Add("info", Pilot.Kind == ModuleKind.Server
            ? $"Модуля нет на сервере {env.Title} - устанавливается в ServerHost…"
            : $"Модуля нет на сервере {env.Title} - создаётся новый объект в папке расширений…");
        var id = await Pilot.CreateAsync(env.PilotUrl, Ctx.Login(env.Key), License, folder, package, reason, ct);
        if (Pilot.AfterCreateWarning(package.ExtensionName, env.Title) is { } warning)
            Add("warn", warning);
        else
            Ok($"Сервер {env.Title}: «{package.ExtensionName}» {package.Version} установлен ({reference} @ {ModuleDeploy.Short(sha)}). {Pilot.AfterUpdateNote}");
        return new Deployed(env, null, null, id, true);
    }

    /// <summary>
    /// Библиотеки модуля против режима библиотек (Настройки / «Библиотеки модулей»): режимы 1 и 2 - нарушение запрещает
    /// выкладку на любой контур, режим 3 - предупреждение в журнале.
    /// </summary>
    protected async Task CheckAssembliesAsync(ModuleEnv env, ModulePackageInfo package, PilotModuleList list, CancellationToken ct)
    {
        if (Ctx.Assemblies is not { } spec)
            return;
        AssemblyCheckResult check;
        try
        {
            // Режим общего модуля: где он лежит на контуре и какие библиотеки в нём - для проверки Preload и зависимостей.
            SharedModuleState? shared = null;
            try
            {
                shared = await AssemblyScanService.LoadSharedAsync(spec, list.Modules,
                    m => Pilot.AssembliesAsync(env.PilotUrl, Ctx.Login(env.Key), License, m, ct));
            }
            catch (PilotGatewayException ex)
            {
                Add("warn", $"Общий модуль «{spec.SharedModule}» на сервере {env.Title} не прочитан ({ex.Message}) - зависимости не проверены.");
            }
            check = AssemblyChecker.Check(spec, package, Ctx.RepoName, shared, Pilot.Kind);
        }
        catch (InvalidDataException ex)
        {
            Add("warn", $"Библиотеки не проверены: {ex.Message}");
            return;
        }
        var count = check.Items.Count(i => i.IsFile);
        if (!check.HasProblems)
        {
            Ok($"Библиотеки модуля ({count} файлов): {check.Summary}.", LibrariesLink, check.Details);
            return;
        }
        var problems = string.Join(", ", check.Problems.Select(p => p.IsFile ? p.Assembly.Name : p.Subject).Distinct(StringComparer.OrdinalIgnoreCase).Take(10));
        if (spec.Blocks)
            throw new WorkflowException(
                $"Библиотеки не по правилам ({AssemblySpec.ModeTitle(spec.Mode)}: {check.Summary}): {problems}. Выкладка на {env.Title} запрещена - " +
                "уберите лишние dll и зависимости из модуля или дополните список библиотек. Модуль НЕ выложен.", check.Details);
        Add("warn", $"Библиотеки модуля ({count} файлов): {check.Summary} - {problems}.", LibrariesLink, check.Details);
    }

    private string LibrariesLink => (Pilot.Kind == ModuleKind.Server ? "/Modules?kind=server" : "/Modules") + "#libraries";


    /// <summary>Вернуть сервер к состоянию до <see cref="DeployAsync"/>.</summary>
    protected async Task RollbackAsync(Deployed deployed)
    {
        if (!deployed.Changed)
            return;
        var env = deployed.Env;
        try
        {
            if (deployed.Previous is not null && deployed.PreviousZip is not null)
            {
                await Pilot.RestoreAsync(env.PilotUrl, Ctx.Login(env.Key), License, deployed.Previous, deployed.PreviousZip,
                    "Откат: " + (deployed.Previous.Reason ?? "выкладка не завершена"), CancellationToken.None);
                Ok($"На сервере {env.Title} возвращена прежняя версия модуля ({deployed.Previous.Version}).");
            }
            else if (deployed.CreatedId is { } id)
            {
                await Pilot.MoveToRecycleBinAsync(env.PilotUrl, Ctx.Login(env.Key), License, id, CancellationToken.None);
                Ok($"Созданный на сервере {env.Title} модуль перемещён в корзину.");
            }
        }
        catch (Exception ex)
        {
            Add("error", $"ОТКАТ НЕ УДАЛСЯ - сервер {env.Title} расходится с веткой: {ex.Message}");
        }
    }

    /// <summary>Модуль сервера, соответствующий пакету: по «Наименованию», коду в скобках, имени репозитория или прежнему имени модуля.</summary>
    internal static PilotModule? MatchPackage(IEnumerable<PilotModule> modules, ModulePackageInfo package, string? repoName = null)
    {
        var list = modules.ToList();
        var keys = new List<string> { ProjectNames.Key(package.ExtensionName) };
        if (ModulePackage.CodeFromName(package.ExtensionName) is { } code) keys.Add(ProjectNames.Key(code));
        keys.Add(ProjectNames.Key(package.AssemblyName));
        keys.Add(ProjectNames.Key(ModuleKinds.CoreName(package.AssemblyName)));
        if (repoName is not null) keys.Add(ProjectNames.Key(repoName));
        // Переименованный модуль: объект на сервере ещё со старым именем - обновляем его, а не создаём второй.
        foreach (var previous in package.Previous)
        {
            keys.Add(ProjectNames.Key(previous));
            keys.Add(ProjectNames.Key(ModuleKinds.CoreName(previous)));
        }

        foreach (var key in keys.Where(k => k.Length > 0))
        {
            var m = list.FirstOrDefault(p => ProjectNames.Key(p.Name) == key) ??
                    list.FirstOrDefault(p => ModulePackage.CodeFromName(p.Name) is { } c && ProjectNames.Key(c) == key);
            if (m is not null)
                return m;
        }
        return null;
    }

    /// <summary>
    /// Задачи ветки относительно main: заголовки коммитов (без merge-коммитов и служебных). Задачи, уже выпущенные
    /// в main копией (выборочный релиз), не считаются - иначе они снова попали бы в следующий релиз.
    /// </summary>
    protected async Task<List<GitLabCommit>> TaskCommitsAsync(string from, string to, CancellationToken ct)
    {
        var commits = (await Git.CompareCommitsAsync(Project, from, to, ct))
            .Where(c => c.ParentIds is not { Count: > 1 } && !IsServiceCommit(c.Title))
            .ToList();
        return from == ModuleBranches.Main ? await Git.WithoutPickedIntoAsync(Project, from, commits, ct) : commits;
    }

    /// <summary>Заголовок коммита с шаблоном CI (подключение репозитория и «Обновить CI»).</summary>
    internal const string CiCommitPrefix = "CI/CD модуля:";

    internal static bool IsServiceCommit(string? title) =>
        title is not null && (title.Contains(Changelog.FileName, StringComparison.OrdinalIgnoreCase) ||
                              title.StartsWith(CiCommitPrefix, StringComparison.Ordinal) ||
                              title.StartsWith("Релиз ", StringComparison.Ordinal) && title.Contains("версия", StringComparison.OrdinalIgnoreCase) ||
                              title.StartsWith("Merge branch", StringComparison.Ordinal));

    /// <summary>
    /// Версия X.Y.Z в csproj/AssemblyInfo и раздел «[X.Y.Z] - дата» в CHANGELOG.md одним коммитом в ветку.
    /// Уже поднятая версия и неизменный журнал - коммита нет. Возвращает текущую голову ветки.
    /// </summary>
    protected async Task<string> CommitVersionAsync(string branch, ModuleVersion version, IReadOnlyList<string> notes, string? task, CancellationToken ct)
    {
        var actions = new List<GitLabCommitAction>();
        foreach (var (path, bump) in new (string?, Func<string, ModuleVersion, string>)[]
                 {
                     (Ctx.ProjectFile, ModuleSources.BumpProject),
                     (Ctx.AssemblyInfoFile, ModuleSources.BumpAssemblyInfo),
                 })
        {
            if (path is null)
                continue;
            var file = await Git.FindFileAsync(Project, path, branch, ct);
            if (file is null)
                continue;
            var text = file.Text;
            var bumped = bump(text, version);
            if (bumped != text)
                actions.Add(GitLabCommitAction.Update(path, bumped));
        }

        var logFile = await Git.FindFileAsync(Project, Changelog.FileName, branch, ct);
        var log = Changelog.Parse(logFile?.Text, Ctx.RepoName);
        var heading = $"[{version}] - {DateTimeOffset.Now.ToLocalTime():yyyy-MM-dd}" + (string.IsNullOrWhiteSpace(task) ? "" : $" - {task!.Trim()}");
        var body = string.Join("\n", notes.Select(n => "- " + n));
        var index = log.Released.FindIndex(s => s.Heading.StartsWith($"[{version}]", StringComparison.Ordinal));
        if (index >= 0)
        {
            if (log.Released[index].Body.Trim() != body.Trim())
                log.Released[index] = new ChangelogSection(log.Released[index].Heading, body);
        }
        else
        {
            log.Released.Insert(0, new ChangelogSection(heading, body));
        }
        log.Unreleased.Clear();
        var rendered = log.Render();
        if (logFile is null)
            actions.Add(GitLabCommitAction.Create(Changelog.FileName, rendered));
        else if (AutomationFiles.NormalizeText(logFile.Text) != AutomationFiles.NormalizeText(rendered))
            actions.Add(GitLabCommitAction.Update(Changelog.FileName, rendered));

        if (actions.Count == 0)
        {
            Info($"Версия {version} и раздел {Changelog.FileName} в {branch} уже на месте.");
            return await HeadAsync(branch, ct);
        }

        var commit = await Git.CreateCommitAsync(Project, branch, null, $"Релиз {version}: версия и {Changelog.FileName}", actions, ct);
        Ok($"{branch}: версия {version}, {Changelog.FileName} (коммит {commit.ShortId}).", commit.WebUrl);
        return commit.Id;
    }

    /// <summary>
    /// Тег vX.Y.Z-rc.N на коммите, выложенном на стейдж. Ставится, даже если коммит на стейдж уже выложил CI
    /// (авто-выкладка release/* успевает раньше админки); повторно на тот же коммит не ставится.
    /// </summary>
    protected async Task StageTagAsync(ModuleVersion version, string sha, string release, CancellationToken ct)
    {
        var tags = await Git.ListVersionTagsAsync(Project, ct) ?? new();
        var prefix = $"v{version}-rc.";
        if (tags.Any(t => t.Name.StartsWith(prefix, StringComparison.Ordinal) && ModuleDeploy.SameCommit(t.Commit?.Id, sha)))
            return;
        await TagAsync(ModuleBranches.StageTag(version, ModuleBranches.NextRc(version, tags.Select(t => t.Name))), sha, $"Стейдж: {release}", ct);
    }

    /// <summary>Тег на коммите (существующий с тем же именем не трогается).</summary>
    protected async Task TagAsync(string tag, string sha, string message, CancellationToken ct, string? releaseNotes = null,
        ModulePackageInfo? package = null)
    {
        try
        {
            if (await Git.FindTagAsync(Project, tag, ct) is { } existing)
            {
                Add("warn", $"Тег {tag} уже есть (коммит {existing.Commit?.ShortId}) - новый не создан.");
                return;
            }
            await Git.CreateTagAsync(Project, tag, sha, message, ct);
            if (releaseNotes is not null)
            {
                await Git.CreateReleaseAsync(Project, tag, tag, releaseNotes, ct);
                Ok($"Релиз {tag} в GitLab.", Ctx.ProjectWebUrl is null ? null : $"{Ctx.ProjectWebUrl}/-/releases/{Uri.EscapeDataString(tag)}");
                if (package is not null)
                    await AttachPackageAsync(tag, package, ct);
            }
            else
            {
                Ok($"Тег {tag} на {ModuleDeploy.Short(sha)}.");
            }
        }
        catch (GitLabApiException ex)
        {
            Add("warn", $"Не удалось создать тег {tag}: {ex.Message}");
        }
    }

    /// <summary>Zip модуля, выложенный на прод, - ассетом релиза. Не прикрепился - релиз остаётся, только предупреждение.</summary>
    private async Task AttachPackageAsync(string tag, ModulePackageInfo package, CancellationToken ct)
    {
        try
        {
            var url = await Git.AddReleaseFileAsync(Project, tag, package.FileName, package.Zip, ct);
            Ok($"К релизу {tag} прикреплён {package.FileName} ({ModulePackage.FormatSize(package.Zip.LongLength)}).", url);
        }
        catch (GitLabApiException ex)
        {
            Add("warn", $"Zip модуля не прикреплён к релизу {tag}: {ex.Message}");
        }
    }

    /// <summary>Открытый MR source / target или новый. Возвращает MR и признак «создан сейчас».</summary>
    protected async Task<(GitLabMergeRequest Mr, bool Created)> EnsureMergeRequestAsync(string source, string target, string title, string description,
        bool removeSourceBranch, bool squash, CancellationToken ct)
    {
        var existing = (await Git.ListOpenMergeRequestsAsync(Project, source, target, ct)).FirstOrDefault();
        if (existing is not null)
        {
            Info($"Используется открытый Merge Request !{existing.Iid} {source} / {target}.", existing.WebUrl);
            return (existing, false);
        }
        var mr = await Git.CreateMergeRequestAsync(Project, source, target, title, description, ct, removeSourceBranch, squash);
        Ok($"Создан Merge Request !{mr.Iid} {source} / {target}.", mr.WebUrl);
        return (mr, true);
    }

    /// <summary>Ждёт pipeline MR, если в репозитории есть CI (или разрешено без него).</summary>
    protected async Task WaitMergeRequestAsync(GitLabMergeRequest mr, string sha, string notSaved, CancellationToken ct)
    {
        var tree = await Git.ListTreeAsync(Project, mr.SourceBranch ?? ModuleBranches.Dev, ct) ?? new();
        if (ModuleSources.HasCi(tree, Ctx.CiConfigPath))
        {
            Job.Update(j => j.Status = JobStatus.WaitingPipeline);
            await Flow(notSaved).WaitForPipelinesAsync(mr.Iid, sha, ct);
            Job.Update(j => j.Status = JobStatus.Running);
        }
        else if (Ctx.Settings.AllowPublishWithoutCi)
        {
            Add("warn", "В репозитории нет .gitlab-ci.yml - pipeline MR не ожидается. Но для выкладки модуля нужна сборка в CI.");
        }
        else
        {
            throw new WorkflowException($"В репозитории нет .gitlab-ci.yml, а работа без pipeline запрещена в настройках. {notSaved}");
        }
    }

    /// <summary>Пробует сразу влить MR (обратное слияние); не удалось - MR остаётся открытым с предупреждением.</summary>
    protected async Task<bool> TryMergeSyncAsync(string source, string target, string title, bool removeSourceBranch, CancellationToken ct)
    {
        try
        {
            var (mr, _) = await EnsureMergeRequestAsync(source, target, title,
                $"Синхронизация веток после выпуска (схема Pilot-BIM: изменения прода возвращаются в {target}).\n\nСоздано PilotProAdmin.",
                removeSourceBranch, squash: false, ct);
            await Task.Delay(TimeSpan.FromSeconds(3), ct); // GitLab считает mergeability асинхронно
            var fresh = await Git.GetMergeRequestAsync(Project, mr.Iid, ct);
            if (fresh.HasConflicts || fresh.DetailedMergeStatus == "conflict")
            {
                Add("warn", $"Merge Request !{mr.Iid} {source} / {target}: конфликт - разрешите его в GitLab.", mr.WebUrl);
                return false;
            }
            try
            {
                await Flow("").MergeAsync(mr.Iid, fresh.Sha ?? "", target, ct, removeSourceBranch);
                return true;
            }
            catch (WorkflowException) when (fresh.Sha is not null)
            {
                // Обычно «pipeline должен пройти»: включаем автослияние - MR вольётся сам после pipeline.
                await Git.MergeWhenPipelineSucceedsAsync(Project, mr.Iid, fresh.Sha, removeSourceBranch, ct);
                Add("warn", $"Merge Request !{mr.Iid} {source} / {target} вольётся автоматически после pipeline (auto-merge). Проверьте его позже.", mr.WebUrl);
                return false;
            }
        }
        catch (Exception ex) when (ex is GitLabApiException or WorkflowException)
        {
            Add("warn", $"Синхронизация {source} / {target} не выполнена автоматически: {ex.Message}. Влейте Merge Request вручную.");
            return false;
        }
    }

    protected void Info(string text, string? url = null) => Add("info", text, url);
    protected void Ok(string text, string? url = null, string? details = null) => Add("ok", text, url, details);
    protected void Add(string level, string text, string? url = null, string? details = null) => Job.Add(level, text, url, details);
}

// ======================================================================================

/// <summary>Выложить ветку (dev / release / hotfix) или тег на сервер контура: pipeline / артефакт / сервер.</summary>
internal sealed class ModuleDeployWorkflow : ModuleWorkflowBase
{
    private readonly string _env;
    private readonly string _reference;
    private readonly IReadOnlyList<string> _notes;
    private readonly bool _isTag;

    public ModuleDeployWorkflow(IModuleTarget pilot, GitLabClient git, PublishJob job, ModuleContext ctx,
        string env, string reference, IReadOnlyList<string> notes, bool isTag = false) : base(pilot, git, job, ctx)
    {
        _env = env;
        _reference = reference;
        _notes = notes;
        _isTag = isTag;
    }

    public override async Task RunAsync(CancellationToken ct)
    {
        var env = Env(_env);
        var sha = _isTag
            ? (await Git.FindTagAsync(Project, _reference, ct))?.Commit?.Id ?? throw new WorkflowException($"Нет тега {_reference}.")
            : await HeadAsync(_reference, ct);
        var package = await Artifacts.BuildAsync(_reference, sha, ct);
        var deployed = await DeployAsync(env, _reference, sha, package, _notes, ct, _isTag ? $"Откат на {_reference}: " : null);

        // Стейдж из release/X.Y.Z - тег vX.Y.Z-rc.N.
        if (env.Key == ModuleEnv.Stage && !_isTag && ModuleBranches.ReleaseVersion(_reference) is { } v)
            await StageTagAsync(v, sha, _reference, ct);
    }
}

/// <summary>
/// feature/bugfix / dev: squash-merge MR (одна задача = один коммит в dev), затем dev / тестовый сервер.
/// Code Review проходит в GitLab: если MR не одобрен, GitLab не даст влить.
/// </summary>
internal sealed class ModuleMergeToDevWorkflow : ModuleWorkflowBase
{
    private readonly long _iid;
    private readonly string? _skipReview;

    /// <param name="skipReview">Причина влития без Code Review (только администратор GitLab); null - нужен апрув.</param>
    public ModuleMergeToDevWorkflow(IModuleTarget pilot, GitLabClient git, PublishJob job, ModuleContext ctx, long iid, string? skipReview = null)
        : base(pilot, git, job, ctx)
    {
        _iid = iid;
        _skipReview = skipReview;
    }

    public override async Task RunAsync(CancellationToken ct)
    {
        const string notSaved = "Задача НЕ влита в dev.";
        var mr = await Git.GetMergeRequestAsync(Project, _iid, ct);
        if (mr.State != "opened")
            throw new WorkflowException($"Merge Request !{_iid} не открыт (состояние: {mr.State}).");
        if (mr.TargetBranch != ModuleBranches.Dev)
            throw new WorkflowException($"Merge Request !{_iid} идёт в {mr.TargetBranch}, а не в {ModuleBranches.Dev}.");
        if (mr.Draft)
            throw new WorkflowException($"Merge Request !{_iid} в статусе Draft - снимите Draft после Code Review.");
        Job.Update(j => j.MergeRequestUrl = mr.WebUrl);

        if (Ctx.Settings.RequireDevReview)
        {
            var reviewers = (await Git.GetApprovalsAsync(Project, _iid, ct)).Reviewers(mr);
            if (reviewers.Count > 0)
                Ok($"Code Review: одобрил {string.Join(", ", reviewers.Select(r => "@" + r))}.", mr.WebUrl);
            else if (_skipReview is not null)
                Add("warn", $"Влито БЕЗ Code Review (администратор @{Job.GitLabUser}): {_skipReview}", mr.WebUrl);
            else
                throw new WorkflowException($"Merge Request !{_iid} не одобрен: нужен Approve в GitLab от ревьюера (не автора). {notSaved}");
        }

        var task = ModuleBranches.Task(mr.SourceBranch, Ctx.Settings.TaskPattern) ?? ModuleBranches.Task(mr.Title, Ctx.Settings.TaskPattern);
        var title = (mr.Title ?? mr.SourceBranch ?? "").Trim();
        var message = task is not null && !title.Contains(task, StringComparison.OrdinalIgnoreCase) ? $"{task}: {title}" : title;
        if (task is null)
            Add("warn", $"В имени ветки и заголовке MR нет номера задачи ({Ctx.Settings.TaskPattern}) - его не будет в журнале релиза.");

        await WaitMergeRequestAsync(mr, mr.Sha ?? "", notSaved, ct);
        await CheckBeforeMergeAsync(mr, ct);
        await Flow(notSaved).MergeAsync(mr.Iid, mr.Sha ?? "", ModuleBranches.Dev, ct, removeSourceBranch: true, squashMessage: message);
        Ok($"В dev один коммит: «{message}».");

        var env = Env(ModuleEnv.Test);
        var head = await HeadAsync(ModuleBranches.Dev, ct);
        // После merge сборка dev проверяется ещё раз при выкладке (DeployAsync).
        var package = await Artifacts.BuildAsync(ModuleBranches.Dev, head, ct);
        await DeployAsync(env, ModuleBranches.Dev, head, package, new[] { message }, ct);
    }

    /// <summary>
    /// Состав сборок по спецификации - на сборке ветки задачи, до влития в dev (против тестового сервера).
    /// Если спецификация запрещает такое на тесте - MR не вливается; иначе предупреждение видно до merge.
    /// </summary>
    private async Task CheckBeforeMergeAsync(GitLabMergeRequest mr, CancellationToken ct)
    {
        if (Ctx.Assemblies is null || mr.SourceBranch is null || mr.Sha is null)
            return;
        ModulePackageInfo package;
        try
        {
            Add("info", $"Проверка сборок по спецификации до влития: сборка {mr.SourceBranch} @ {ModuleDeploy.Short(mr.Sha)}…");
            package = await Artifacts.BuildAsync(mr.SourceBranch, mr.Sha, ct);
        }
        catch (WorkflowException ex)
        {
            Add("warn", $"Сборка ветки для проверки не найдена ({ex.Message}) - состав сборок проверится при выкладке dev на тест.");
            return;
        }
        var env = Env(ModuleEnv.Test);
        var list = await Pilot.ListAsync(env.PilotUrl, Ctx.Login(env.Key), License, ct);
        await CheckAssembliesAsync(env, package, list, ct); // запрет на тесте - исключение, задача НЕ вливается
    }
}

/// <summary>
/// Обновить .gitlab-ci.yml до шаблона админки и оставить версию только в &lt;Version&gt; csproj:
/// ветка ci/template-… от dev, коммит, MR в dev (dev защищена),
/// ожидание pipeline и squash merge. Без Code Review - меняется только шаблон админки; операция в журнале.
/// </summary>
internal sealed class ModuleCiUpdateWorkflow : ModuleWorkflowBase
{
    private readonly string _content;

    public ModuleCiUpdateWorkflow(IModuleTarget pilot, GitLabClient git, PublishJob job, ModuleContext ctx, string content)
        : base(pilot, git, job, ctx) => _content = content;

    public override async Task RunAsync(CancellationToken ct)
    {
        const string notSaved = "Шаблон CI в dev НЕ обновлён.";
        var path = Ctx.CiConfigPath ?? ".gitlab-ci.yml";
        var actions = new List<GitLabCommitAction>();
        var changes = new List<string>();

        var current = await Git.FindFileAsync(Project, path, ModuleBranches.Dev, ct);
        if (current is null || !ModuleCiTemplate.Same(current.Text, _content))
        {
            actions.Add(current is null ? GitLabCommitAction.Create(path, _content) : GitLabCommitAction.Update(path, _content));
            changes.Add($"{path} по шаблону админки");
        }

        // Версия модуля - в одном месте: <Version> в csproj (AssemblyVersion/FileVersion генерирует SDK).
        var info = Ctx.AssemblyInfoFile is { } infoPath ? await Git.FindFileAsync(Project, infoPath, ModuleBranches.Dev, ct) : null;
        if (Ctx.ProjectFile is { } projPath && await Git.FindFileAsync(Project, projPath, ModuleBranches.Dev, ct) is { } proj)
        {
            var fixedProj = ModuleSources.SingleVersionProject(proj.Text, info?.Text);
            if (fixedProj != proj.Text)
            {
                actions.Add(GitLabCommitAction.Update(projPath, fixedProj));
                changes.Add($"{projPath}: версия только в <Version>");
            }
            if (info is not null && ModuleSources.SingleVersionAssemblyInfo(info.Text) is var fixedInfo && fixedInfo != info.Text)
            {
                actions.Add(GitLabCommitAction.Update(Ctx.AssemblyInfoFile!, fixedInfo));
                changes.Add($"{Ctx.AssemblyInfoFile}: без атрибутов версии");
            }
        }

        if (actions.Count == 0)
        {
            Ok($"{path} в dev уже по шаблону, версия - только в <Version>.");
            return;
        }

        var branch = $"ci/template-{DateTime.UtcNow:yyyyMMdd-HHmmss}";
        await Git.CreateBranchAsync(Project, branch, ModuleBranches.Dev, ct);
        var message = $"{CiCommitPrefix} шаблон {path} и версия в одном месте (веб-админка)";
        await Git.CreateCommitAsync(Project, branch, null, message, actions, ct);
        Ok($"Ветка {branch}: {string.Join("; ", changes)}.");

        var (mr, _) = await EnsureMergeRequestAsync(branch, ModuleBranches.Dev, message,
            "PilotProAdmin:\n\n" + string.Join("\n", changes.Select(c => "- " + c)) +
            $"\n\nСборка в pipeline MR проверяет, что проект собирается; при ошибке MR не вливается.\n\n- Pilot: {Job.PilotUser}",
            removeSourceBranch: true, squash: true, ct);
        Job.Update(j => j.MergeRequestUrl = mr.WebUrl);
        for (var i = 0; i < 15 && string.IsNullOrEmpty((mr = await Git.GetMergeRequestAsync(Project, mr.Iid, ct)).Sha); i++)
            await Task.Delay(TimeSpan.FromSeconds(2), ct); // sha нового MR GitLab заполняет асинхронно
        await WaitMergeRequestAsync(mr, mr.Sha ?? "", notSaved, ct);
        await Flow(notSaved).MergeAsync(mr.Iid, mr.Sha ?? "", ModuleBranches.Dev, ct, removeSourceBranch: true, squashMessage: message);
        Ok("Шаблон CI и версия в dev обновлены. В main попадут с ближайшим релизом.", mr.WebUrl);
    }
}

/// <summary>
/// Подготовка релиза: release/X.Y.Z от main и Merge Request в него из release-prep/X.Y.Z - снимка dev
/// (все задачи) или ветки от main, куда cherry-pick'ом собраны только выбранные задачи; MR сразу вливается
/// и релиз выкладывается на стейдж (<see cref="ModuleReleaseToStageWorkflow"/>).
/// </summary>
internal sealed class ModulePrepareReleaseWorkflow : ModuleWorkflowBase
{
    private readonly ModuleVersion _version;
    private readonly IReadOnlyCollection<string>? _selected;
    private readonly IReadOnlyList<string> _notes;
    private readonly bool _deployToStage;
    private readonly string? _replaces;

    /// <param name="replaces">Активный релиз, который новый заменяет: его MR закрываются и ветки удаляются,
    /// когда новая ветка уже собрана (не удалось собрать - старый релиз остаётся).</param>
    public ModulePrepareReleaseWorkflow(IModuleTarget pilot, GitLabClient git, PublishJob job, ModuleContext ctx,
        ModuleVersion version, IReadOnlyCollection<string>? selectedShas, IReadOnlyList<string> notes, bool deployToStage = true, string? replaces = null)
        : base(pilot, git, job, ctx)
    {
        _version = version;
        _selected = selectedShas;
        _notes = notes;
        _deployToStage = deployToStage;
        _replaces = replaces;
    }

    public override async Task RunAsync(CancellationToken ct)
    {
        var release = ModuleBranches.Release(_version);
        if (await Git.FindBranchAsync(Project, release, ct) is not null)
            throw new WorkflowException($"Ветка {release} уже есть - выберите другую версию или продолжите этот релиз.");

        // Голова dev фиксируется сразу: всё, что вольют в dev позже, в этот релиз не попадёт.
        var devHead = await HeadAsync(ModuleBranches.Dev, ct);
        var commits = await TaskCommitsAsync(ModuleBranches.Main, devHead, ct);
        if (commits.Count == 0)
            throw new WorkflowException($"В {ModuleBranches.Dev} нет задач, которых нет в {ModuleBranches.Main}, - выпускать нечего.");

        var chosen = _selected is null ? commits : commits.Where(c => _selected.Contains(c.Id)).ToList();
        if (chosen.Count == 0)
            throw new WorkflowException("Не выбрано ни одной задачи для релиза.");
        var full = chosen.Count == commits.Count;
        var list = string.Join("\n", chosen.Select(c => $"- {c.Title} (`{c.ShortId}`)"));
        var skipped = commits.Where(c => !chosen.Contains(c)).ToList();

        await Git.CreateBranchAsync(Project, release, ModuleBranches.Main, ct);
        Ok($"Создана ветка {release} от {ModuleBranches.Main}.");

        var source = ModuleBranches.ReleasePrep(_version);
        if (full)
        {
            // Снимок dev: MR из живой dev затянул бы в релиз задачи, влитые во время Code Review.
            await Git.CreateBranchAsync(Project, source, devHead, ct);
            Info($"В релиз идут все задачи dev ({chosen.Count}) - снимок dev @ {ModuleDeploy.Short(devHead)} в {source}, Merge Request {source} / {release}.");
        }
        else
        {
            await Git.CreateBranchAsync(Project, source, ModuleBranches.Main, ct);
            Info($"Выборочный релиз: {chosen.Count} из {commits.Count} задач - cherry-pick в {source}.");
            foreach (var c in chosen) // старые первыми, как в dev
            {
                try
                {
                    var picked = await Git.CherryPickAsync(Project, c.Id, source, ct);
                    Ok($"cherry-pick {c.ShortId} «{c.Title}» / {picked.ShortId}");
                }
                catch (GitLabApiException ex)
                {
                    await CleanupAsync(release, source);
                    throw new WorkflowException(
                        $"cherry-pick {c.ShortId} «{c.Title}» не удался: {ex.Message}. Задача зависит от невыбранной или конфликтует с main - " +
                        "включите в релиз задачи, от которых она зависит. Ветки релиза удалены.");
                }
            }
        }

        if (_replaces is { } old)
        {
            foreach (var oldMr in await Git.ListOpenMergeRequestsToAsync(Project, old, ct) ?? new())
                await Git.CloseMergeRequestAsync(Project, oldMr.Iid, ct);
            if (ModuleBranches.ReleaseVersion(old) is { } oldVersion)
                try { await Git.DeleteBranchAsync(Project, ModuleBranches.ReleasePrep(oldVersion), ct); } catch (GitLabApiException) { /* уже нет */ }
            await Git.DeleteBranchAsync(Project, old, ct);
            Ok($"Релиз {old} заменён на {release}: его Merge Request закрыты, ветка удалена (в прод он не пойдёт).");
        }

        var description =
            $"## Релиз {_version}\n\n" +
            (_replaces is { } replaced ? $"Заменяет {replaced} (не выпущен).\n\n" : "") +
            $"### Задачи\n\n{list}\n" +
            (skipped.Count > 0 ? $"\n### Не входят в релиз (остаются в dev)\n\n{string.Join("\n", skipped.Select(c => $"- {c.Title}"))}\n" : "") +
            (_notes.Count > 0 ? $"\n### Заметки\n\n{string.Join("\n", _notes.Select(n => "- " + n))}\n" : "") +
            $"\n---\n\nСоздано PilotProAdmin: Merge Request вливается сразу, релиз выкладывается на стейдж.\n\n- Pilot: {Job.PilotUser}";
        var (mr, _) = await EnsureMergeRequestAsync(source, release, $"Релиз {_version}", description,
            removeSourceBranch: true, squash: false, ct);
        Job.Update(j => j.MergeRequestUrl = mr.WebUrl);
        Ok($"Ветка {release} и Merge Request готовы - релиз выкладывается на стейдж.", mr.WebUrl);

        // dev / стейдж одним шагом: влить снимок, версия и CHANGELOG, сборка, выкладка, тег rc.
        // Не удалось (pipeline, сервер) - ветка и MR остаются, повтор - «Влить / стейдж».
        if (!_deployToStage)
            return;
        for (var i = 0; i < 15 && string.IsNullOrEmpty((await Git.GetMergeRequestAsync(Project, mr.Iid, ct)).Sha); i++)
            await Task.Delay(TimeSpan.FromSeconds(2), ct); // sha нового MR GitLab заполняет асинхронно
        await new ModuleReleaseToStageWorkflow(Pilot, Git, Job, Ctx, release, mr.Iid).RunAsync(ct);
    }

    private async Task CleanupAsync(string release, string prep)
    {
        try { await Git.DeleteBranchAsync(Project, prep, CancellationToken.None); } catch { /* уборка */ }
        try { await Git.DeleteBranchAsync(Project, release, CancellationToken.None); } catch { /* уборка */ }
    }
}

/// <summary>
/// Релиз на стейдж: влить MR в release/X.Y.Z (из dev / release-prep / bugfix), поднять версию и CHANGELOG,
/// дождаться сборки и выложить на стейдж, тег vX.Y.Z-rc.N.
/// </summary>
internal sealed class ModuleReleaseToStageWorkflow : ModuleWorkflowBase
{
    private readonly string _release;
    private readonly long? _mrIid;

    public ModuleReleaseToStageWorkflow(IModuleTarget pilot, GitLabClient git, PublishJob job, ModuleContext ctx, string release, long? mrIid)
        : base(pilot, git, job, ctx)
    {
        _release = release;
        _mrIid = mrIid;
    }

    public override async Task RunAsync(CancellationToken ct)
    {
        var version = ModuleBranches.ReleaseVersion(_release) ?? throw new WorkflowException($"{_release} - не ветка релиза (release/X.Y.Z).");
        const string notSaved = "На стейдж НЕ выложено.";

        if (_mrIid is { } iid)
        {
            var mr = await Git.GetMergeRequestAsync(Project, iid, ct);
            if (mr.TargetBranch != _release)
                throw new WorkflowException($"Merge Request !{iid} идёт в {mr.TargetBranch}, а не в {_release}.");
            if (mr.State == "opened")
            {
                if (mr.Draft)
                    throw new WorkflowException($"Merge Request !{iid} в статусе Draft - завершите Code Review.");
                Job.Update(j => j.MergeRequestUrl = mr.WebUrl);
                await WaitMergeRequestAsync(mr, mr.Sha ?? "", notSaved, ct);
                // Багфикс - одним коммитом, как задачи в dev; dev и release-prep - без squash, чтобы сохранить коммиты задач.
                var isFix = ModuleBranches.IsTaskBranch(mr.SourceBranch);
                var task = ModuleBranches.Task(mr.SourceBranch, Ctx.Settings.TaskPattern);
                var title = (mr.Title ?? "").Trim();
                await Flow(notSaved).MergeAsync(mr.Iid, mr.Sha ?? "", _release, ct,
                    removeSourceBranch: mr.SourceBranch != ModuleBranches.Dev && !ModuleBranches.IsHotfix(mr.SourceBranch),
                    squashMessage: isFix ? (task is not null && !title.Contains(task, StringComparison.OrdinalIgnoreCase) ? $"{task}: {title}" : title) : null);
            }
            else if (mr.State != "merged")
            {
                throw new WorkflowException($"Merge Request !{iid} закрыт - откройте его заново или создайте новый.");
            }
        }

        var tasks = (await TaskCommitsAsync(ModuleBranches.Main, _release, ct)).Select(c => c.Title ?? c.ShortId).Distinct().ToList();
        var head = await CommitVersionAsync(_release, version, tasks, null, ct);
        var package = await Artifacts.BuildAsync(_release, head, ct);
        if (ModuleVersion.TryParse(package.Version, out var built) && built != version)
            Add("warn", $"Собранная сборка имеет версию {package.Version}, а релиз - {version}. Проверьте Version в csproj/AssemblyInfo.");

        // Связанные изменения типов - на стейдж перед модулем (модулю могут быть нужны новые атрибуты).
        if (Ctx.Types is { } types)
            await types.For(Git, Job).StageAsync(Ctx.TypesKind, Project, _release, notSaved, ct);

        await DeployAsync(Env(ModuleEnv.Stage), _release, head, package, tasks, ct);
        await StageTagAsync(version, head, _release, ct);
        Ok($"Релиз {version} на стейдже. Проверьте его и нажмите «Выпустить в прод» (или влейте мелкие багфиксы в {_release}).");
    }
}

/// <summary>
/// Добавить в активный релиз выбранные задачи dev: ветка release-prep/X.Y.Z-add-… от головы релиза, cherry-pick задач
/// (старые первыми), MR в release/X.Y.Z, затем то же, что «На стейдж»: merge, версия и CHANGELOG, сборка, выкладка, тег rc.
/// </summary>
internal sealed class ModuleAddToReleaseWorkflow : ModuleWorkflowBase
{
    private readonly string _release;
    private readonly IReadOnlyCollection<string> _selected;

    public ModuleAddToReleaseWorkflow(IModuleTarget pilot, GitLabClient git, PublishJob job, ModuleContext ctx, string release, IReadOnlyCollection<string> selected)
        : base(pilot, git, job, ctx)
    {
        _release = release;
        _selected = selected;
    }

    public override async Task RunAsync(CancellationToken ct)
    {
        var version = ModuleBranches.ReleaseVersion(_release) ?? throw new WorkflowException($"{_release} - не ветка релиза (release/X.Y.Z).");
        var releaseHead = await HeadAsync(_release, ct);
        var inRelease = (await TaskCommitsAsync(ModuleBranches.Main, _release, ct)).Select(c => c.Title).ToHashSet();
        var chosen = (await TaskCommitsAsync(ModuleBranches.Main, ModuleBranches.Dev, ct))
            .Where(c => _selected.Contains(c.Id) && !inRelease.Contains(c.Title)).ToList();
        if (chosen.Count == 0)
            throw new WorkflowException($"Выбранные задачи уже в {_release} или их нет в {ModuleBranches.Dev}.");

        var source = $"{ModuleBranches.ReleasePrepPrefix}{version}-add-{DateTime.Now:MMdd-HHmmss}";
        await Git.CreateBranchAsync(Project, source, releaseHead, ct);
        Info($"В релиз {version} добавляются задачи ({chosen.Count}) - cherry-pick в {source}: {string.Join("; ", chosen.Select(c => c.Title))}.");
        foreach (var c in chosen) // старые первыми, как в dev
        {
            try
            {
                var picked = await Git.CherryPickAsync(Project, c.Id, source, ct);
                Ok($"cherry-pick {c.ShortId} «{c.Title}» / {picked.ShortId}");
            }
            catch (GitLabApiException ex)
            {
                try { await Git.DeleteBranchAsync(Project, source, CancellationToken.None); } catch { /* уборка */ }
                throw new WorkflowException($"cherry-pick {c.ShortId} «{c.Title}» не удался: {ex.Message}. Задача зависит от другой, которой нет в релизе, " +
                                            $"или конфликтует с ним - добавьте и её. {_release} не изменён.");
            }
        }

        var (mr, _) = await EnsureMergeRequestAsync(source, _release, $"Релиз {version}: + {ModuleNextSteps.Plural(chosen.Count, "задача", "задачи", "задач")}",
            $"## В релиз {version} добавлены\n\n{string.Join("\n", chosen.Select(c => $"- {c.Title} (`{c.ShortId}`)"))}\n\n---\n\n" +
            $"Создано PilotProAdmin: MR вливается сразу, релиз выкладывается на стейдж.\n\n- Pilot: {Job.PilotUser}",
            removeSourceBranch: true, squash: false, ct);
        Job.Update(j => j.MergeRequestUrl = mr.WebUrl);
        for (var i = 0; i < 15 && string.IsNullOrEmpty((await Git.GetMergeRequestAsync(Project, mr.Iid, ct)).Sha); i++)
            await Task.Delay(TimeSpan.FromSeconds(2), ct); // sha нового MR GitLab заполняет асинхронно
        await new ModuleReleaseToStageWorkflow(Pilot, Git, Job, Ctx, _release, mr.Iid).RunAsync(ct);
    }
}

/// <summary>
/// Выпуск в прод: MR release/X.Y.Z / main / pipeline / на прод тот же артефакт, что проверен на стейдже /
/// merge (не удался - откат прода) / тег vX.Y.Z + Release / обратное слияние release / dev.
/// </summary>
internal sealed class ModuleReleaseToProdWorkflow : ModuleWorkflowBase
{
    private readonly string _release;
    private readonly string? _task;
    private readonly bool _allowUntested;

    public ModuleReleaseToProdWorkflow(IModuleTarget pilot, GitLabClient git, PublishJob job, ModuleContext ctx,
        string release, string? task, bool allowUntested) : base(pilot, git, job, ctx)
    {
        _release = release;
        _task = task;
        _allowUntested = allowUntested;
    }

    public override async Task RunAsync(CancellationToken ct)
    {
        var version = ModuleBranches.ReleaseVersion(_release) ?? throw new WorkflowException($"{_release} - не ветка релиза.");
        const string notSaved = "Модуль НЕ выложен на прод.";
        var head = await HeadAsync(_release, ct);

        // 1. На стейдже должен быть ровно этот коммит.
        var stage = ModuleEnv.Find(Ctx.Settings, ModuleEnv.Stage);
        if (stage is null)
        {
            Add("warn", "Сервер стейджа не настроен - проверка «на стейдже та же версия» пропущена.");
        }
        else
        {
            var stageList = await Pilot.ListAsync(stage.PilotUrl, Ctx.Login(stage.Key), License, ct);
            var onStage = stageList.Modules.FirstOrDefault(m => ModuleDeploy.SameCommit(m.DeployedSha, head));
            if (onStage is null && !_allowUntested)
                throw new WorkflowException($"На стейдже нет коммита {ModuleDeploy.Short(head)} ветки {_release} - сначала «Выложить на стейдж» и проверьте. {notSaved}");
            if (onStage is null)
                Add("warn", $"На стейдже другой коммит - выпуск без проверки на стейдже (выбрано явно).");
            else
                Ok($"На стейдже проверен этот же коммит {ModuleDeploy.Short(head)}.");
        }

        var tasks = (await TaskCommitsAsync(ModuleBranches.Main, _release, ct)).Select(c => c.Title ?? c.ShortId).Distinct().ToList();

        // 2. MR release / main и его pipeline.
        var (mr, created) = await EnsureMergeRequestAsync(_release, ModuleBranches.Main, $"Релиз {version}" + (_task is null ? "" : $" - {_task}"),
            $"## Релиз {version}\n\n{string.Join("\n", tasks.Select(t => "- " + t))}\n\n---\n\nСоздано PilotProAdmin (выпуск в прод).\n\n- Pilot: {Job.PilotUser}",
            removeSourceBranch: false, squash: false, ct);
        Job.Update(j => j.MergeRequestUrl = mr.WebUrl);

        var merged = false;
        var types = Ctx.Types?.For(Git, Job);
        TypesFollowRun? typesRun = null;
        ModulePackageInfo package;
        try
        {
            await WaitMergeRequestAsync(mr, head, notSaved, ct);

            // 3. Артефакт той же сборки ветки релиза, что и на стейдже.
            package = await Artifacts.BuildAsync(_release, head, ct);

            // 3а. Связанные изменения типов: перенос в main готовится (MR, pipeline) и пишется на прод перед модулем.
            if (types is not null)
            {
                typesRun = await types.PrepareProdAsync(Ctx.TypesKind, Project, _release, _task, notSaved, ct);
                if (typesRun is not null)
                    await types.ApplyProdAsync(typesRun, ct);
            }

            var deployed = await DeployAsync(Env(ModuleEnv.Prod), $"v{version}", head, package,
                _task is null ? tasks : tasks.Prepend(_task).ToList(), ct);

            // 4. Merge; не удался - прод откатывается (и типы, если связаны).
            try
            {
                await Flow(notSaved).MergeAsync(mr.Iid, head, ModuleBranches.Main, ct, removeSourceBranch: false);
                merged = true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Add("error", $"Merge Request не влит: {ex.Message}");
                await RollbackAsync(deployed);
                throw new WorkflowException($"GitLab не принял релиз в {ModuleBranches.Main}: {ex.Message}. Прод откатан.");
            }
        }
        finally
        {
            if (!merged && typesRun is not null)
                await types!.AbortProdAsync(typesRun);
            if (!merged && created)
            {
                try { await Git.CloseMergeRequestAsync(Project, mr.Iid, CancellationToken.None); }
                catch { /* уборка */ }
            }
        }

        // 5. Тег и Release на main.
        var mainHead = await HeadAsync(ModuleBranches.Main, ct);
        await TagAsync(ModuleBranches.ProdTag(version), mainHead, $"Релиз {version}", ct,
            $"## Релиз {version}\n\n{string.Join("\n", tasks.Select(t => "- " + t))}\n\n" +
            (_task is null ? "" : $"Задача: {_task}\n\n") + $"Выпустил: {Job.PilotUser} (PilotProAdmin).", package);

        // 5а. Связанные типы: MR в main репозитория типов, тег. Не влилось - типы уже на проде, MR вливается вручную.
        if (typesRun is not null)
            await types!.CompleteProdAsync(typesRun, ct);

        // 6. Обратное слияние release / dev, чтобы ветки не разошлись (версия, CHANGELOG, багфиксы релиза).
        // 7. Ветка релиза больше не нужна: код в main и dev, история - теги vX.Y.Z-rc.N и vX.Y.Z. Не влилась в dev - остаётся до ручного merge.
        if (await TryMergeSyncAsync(_release, ModuleBranches.Dev, $"Обратное слияние {_release} / {ModuleBranches.Dev}", removeSourceBranch: false, ct))
        {
            try
            {
                await Git.DeleteBranchAsync(Project, _release, ct);
                Info($"Ветка {_release} удалена: релиз в main и dev, история - в тегах v{version}-rc.N и v{version}.");
            }
            catch (GitLabApiException ex)
            {
                Add("warn", $"Ветку {_release} не удалось удалить: {ex.Message}");
            }
        }
        Ok($"Релиз {version} на проде. Задаче в Kaiten можно ставить статус «Прод» после smoke-check.");
    }
}

/// <summary>
/// Hotfix в прод: версия X.Y.(Z+1) и CHANGELOG в hotfix-ветку / MR hotfix / main / pipeline / прод / merge /
/// тег / синхронизация hotfix / dev и в активный release.
/// </summary>
internal sealed class ModuleHotfixWorkflow : ModuleWorkflowBase
{
    private readonly string _branch;
    private readonly IReadOnlyList<string> _notes;
    private readonly string? _activeRelease;

    public ModuleHotfixWorkflow(IModuleTarget pilot, GitLabClient git, PublishJob job, ModuleContext ctx,
        string branch, IReadOnlyList<string> notes, string? activeRelease) : base(pilot, git, job, ctx)
    {
        _branch = branch;
        _notes = notes;
        _activeRelease = activeRelease;
    }

    public override async Task RunAsync(CancellationToken ct)
    {
        const string notSaved = "Hotfix НЕ выложен на прод.";
        var task = ModuleBranches.Task(_branch, Ctx.Settings.TaskPattern);

        // Версия: следующий патч от большей из (последний тег прода, версия в main).
        var tags = await Git.ListVersionTagsAsync(Project, ct) ?? new();
        var lastProd = ModuleBranches.ProdReleases(tags).Select(r => r.Version).FirstOrDefault();
        var mainInfo = Ctx.ProjectFile is null ? null : await Git.FindFileAsync(Project, Ctx.ProjectFile, ModuleBranches.Main, ct);
        var infoFile = Ctx.AssemblyInfoFile is null ? null : await Git.FindFileAsync(Project, Ctx.AssemblyInfoFile, ModuleBranches.Main, ct);
        var mainVersion = ModuleSources.Version(mainInfo?.Text, infoFile?.Text);
        var baseVersion = new[] { lastProd, mainVersion }.Where(v => v is not null).Max() ?? new ModuleVersion(1, 0, 0);
        var version = baseVersion.NextPatch();
        Info($"Версия hotfix: {version} (прод: {lastProd?.ToString() ?? "-"}, main: {mainVersion?.ToString() ?? "-"}).");

        var notes = _notes.Count > 0 ? _notes : (await TaskCommitsAsync(ModuleBranches.Main, _branch, ct)).Select(c => c.Title ?? c.ShortId).ToList();
        var head = await CommitVersionAsync(_branch, version, notes, task, ct);

        var (mr, created) = await EnsureMergeRequestAsync(_branch, ModuleBranches.Main, $"Hotfix {version}" + (task is null ? "" : $" - {task}"),
            $"## Hotfix {version}\n\n{string.Join("\n", notes.Select(n => "- " + n))}\n\n---\n\nСоздано PilotProAdmin (hotfix).\n\n- Pilot: {Job.PilotUser}",
            removeSourceBranch: false, squash: false, ct);
        Job.Update(j => j.MergeRequestUrl = mr.WebUrl);

        var merged = false;
        ModulePackageInfo package;
        try
        {
            await WaitMergeRequestAsync(mr, head, notSaved, ct);
            package = await Artifacts.BuildAsync(_branch, head, ct);
            var deployed = await DeployAsync(Env(ModuleEnv.Prod), $"v{version}", head, package, notes, ct);
            try
            {
                await Flow(notSaved).MergeAsync(mr.Iid, head, ModuleBranches.Main, ct, removeSourceBranch: false);
                merged = true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Add("error", $"Merge Request не влит: {ex.Message}");
                await RollbackAsync(deployed);
                throw new WorkflowException($"GitLab не принял hotfix в {ModuleBranches.Main}: {ex.Message}. Прод откатан.");
            }
        }
        finally
        {
            if (!merged && created)
            {
                try { await Git.CloseMergeRequestAsync(Project, mr.Iid, CancellationToken.None); }
                catch { /* уборка */ }
            }
        }

        var mainHead = await HeadAsync(ModuleBranches.Main, ct);
        await TagAsync(ModuleBranches.ProdTag(version), mainHead, $"Hotfix {version}", ct,
            $"## Hotfix {version}\n\n{string.Join("\n", notes.Select(n => "- " + n))}\n\nВыпустил: {Job.PilotUser} (PilotProAdmin).", package);

        // Исправление возвращается во все актуальные ветки, иначе баг вернётся следующим релизом.
        var synced = await TryMergeSyncAsync(_branch, ModuleBranches.Dev, $"Hotfix {version} / {ModuleBranches.Dev}", removeSourceBranch: false, ct);
        if (_activeRelease is not null)
            synced &= await TryMergeSyncAsync(_branch, _activeRelease, $"Hotfix {version} / {_activeRelease}", removeSourceBranch: false, ct);
        if (synced)
        {
            try
            {
                await Git.DeleteBranchAsync(Project, _branch, ct);
                Info($"Ветка {_branch} удалена: hotfix в main, dev" + (_activeRelease is null ? "." : $" и {_activeRelease}."));
            }
            catch (GitLabApiException ex)
            {
                Add("warn", $"Ветку {_branch} не удалось удалить: {ex.Message}");
            }
        }
        Ok($"Hotfix {version} на проде. После smoke-check поставьте задаче статус «Прод».");
    }
}
