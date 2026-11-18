using ProAdmin.GitLab;

namespace ProAdmin.Automations;

/// <summary>
/// Репозиторий одной автоматизации (или отчёта): проект GitLab внутри группы.
/// <see cref="Name"/> - имя проекта (Project name), по нему идёт сопоставление с Pilot; путь (slug) - любой.
/// </summary>
public sealed class AutomationRepo
{
    public required string Name { get; init; }
    public required string ProjectPath { get; init; }

    /// <summary>Последний сегмент пути проекта (slug).</summary>
    public string Slug => ProjectPath.Contains('/') ? ProjectPath[(ProjectPath.LastIndexOf('/') + 1)..] : ProjectPath;

    /// <summary>Папка - путь подгруппы относительно корневой группы («» - корень, «notification»).</summary>
    public required string Folder { get; init; }

    public required string WebUrl { get; init; }
    public string? Description { get; init; }
    public string? CiConfigPath { get; init; }

    public static AutomationRepo From(GitLabProject p, string groupPath) => new()
    {
        Name = string.IsNullOrWhiteSpace(p.Name) ? p.Path : p.Name,
        ProjectPath = p.PathWithNamespace,
        Folder = FolderOf(p.Namespace?.FullPath ?? "", groupPath),
        WebUrl = p.WebUrl,
        Description = p.Description,
        CiConfigPath = p.CiConfigPath,
    };

    public static string FolderOf(string namespacePath, string groupPath) =>
        namespacePath.Length > groupPath.Length && namespacePath.StartsWith(groupPath + "/", StringComparison.OrdinalIgnoreCase)
            ? namespacePath[(groupPath.Length + 1)..]
            : "";
}

/// <summary>
/// Структура в GitLab: группа автоматизаций (например automation) / подгруппы-папки (automation/notification)
/// / проект на каждую автоматизацию. В проекте ветки main и dev, файлы Имя.json, Имя/Имя.cs, README.md.
/// </summary>
public static class AutomationRepositories
{
    public const string MainBranch = "main";
    public const string DevBranch = "dev";

    /// <summary>Защита main: push - никто, merge - Maintainer; в main попадает только то, что прошло Merge Request и pipeline.</summary>
    public const int MainPushLevel = 0, MainMergeLevel = 40;

    /// <summary>
    /// Защитить main нового репозитория. Без <paramref name="log"/> ошибка GitLab пробрасывается, с ним - идёт предупреждением
    /// (проект уже создан, защиту добавит установщик или группа).
    /// </summary>
    public static async Task ProtectMainAsync(GitLabClient git, string project, Action<string, string>? log, CancellationToken ct)
    {
        try
        {
            if (await git.ProtectBranchAsync(project, MainBranch, MainPushLevel, MainMergeLevel, ct))
                log?.Invoke("ok", "Ветка main защищена: push запрещён, merge только через Merge Request с успешным pipeline.");
        }
        catch (GitLabApiException ex) when (log is not null)
        {
            log("warn", $"Не удалось защитить main: {ex.Message}. Настройте Protected branches вручную (push: No one).");
        }
    }

    /// <summary>
    /// Прямой коммит в main (переименование). «No one» не пускает и Maintainer: правило на время коммита снимается и сразу возвращается.
    /// </summary>
    public static async Task<GitLabCommit> CommitToMainAsync(GitLabClient git, string project, string message,
        IReadOnlyCollection<GitLabCommitAction> actions, CancellationToken ct)
    {
        try
        {
            return await git.CreateCommitAsync(project, MainBranch, null, message, actions, ct);
        }
        catch (GitLabApiException ex) when (ex.StatusCode == 403 || (ex.StatusCode == 400 && ex.Message.Contains("not allowed to push", StringComparison.OrdinalIgnoreCase)))
        {
            // Нет прав снять защиту (Developer) - понятнее показать исходный отказ push, чем отказ снятия.
            // Защита могла быть снята, как только запрос на снятие ушёл: возвращаем её при любой ошибке, в том числе
            // отмене и сетевой, и не зависим от токена отмены запроса (иначе сама отмена оставила бы main открытым).
            var protectionMayBeOff = false;
            GitLabCommit commit;
            try
            {
                try
                {
                    await git.UnprotectBranchAsync(project, MainBranch, ct);
                }
                catch (GitLabApiException)
                {
                    throw ex; // отказ GitLab: правило осталось как было
                }
                catch
                {
                    protectionMayBeOff = true;
                    throw;
                }
                protectionMayBeOff = true;
                commit = await git.CreateCommitAsync(project, MainBranch, null, message, actions, ct);
            }
            catch (Exception failure) when (protectionMayBeOff)
            {
                try
                {
                    await git.ProtectBranchAsync(project, MainBranch, MainPushLevel, MainMergeLevel, CancellationToken.None);
                }
                catch (GitLabApiException protectEx) when (failure is GitLabApiException commitEx)
                {
                    throw new GitLabApiException(commitEx.StatusCode, $"{commitEx.Message} (защиту main вернуть тоже не удалось: {protectEx.Message}; проверьте её в GitLab)");
                }
                catch (Exception protectEx)
                {
                    throw new AggregateException("Не удалось ни выполнить коммит, ни вернуть защиту main: проверьте её в GitLab.", failure, protectEx);
                }
                throw;
            }
            // Коммит уже в main: сбой возврата защиты не должен его скрыть, но и молчать о нём нельзя.
            await git.ProtectBranchAsync(project, MainBranch, MainPushLevel, MainMergeLevel, CancellationToken.None);
            return commit;
        }
    }

    public static async Task<List<AutomationRepo>> ListAsync(GitLabClient git, string groupPath, CancellationToken ct)
    {
        var projects = await git.ListGroupProjectsAsync(groupPath, null, ct)
                       ?? throw new GitLabApiException(404, $"Группа «{groupPath}» не найдена в GitLab (или нет доступа).");
        return projects.Select(p => AutomationRepo.From(p, groupPath)).ToList();
    }

    /// <summary>
    /// Репозиторий по имени проекта. Для репозиториев, созданных до перехода на имена, допускается совпадение пути.
    /// </summary>
    public static async Task<AutomationRepo?> FindAsync(GitLabClient git, string groupPath, string name, CancellationToken ct)
    {
        var projects = await git.ListGroupProjectsAsync(groupPath, name, ct) ?? new();
        var project = projects.FirstOrDefault(p => p.Name == name) ?? projects.FirstOrDefault(p => p.Path == name);
        return project is null ? null : AutomationRepo.From(project, groupPath);
    }

    /// <summary>Создаёт проект с именем <paramref name="name"/>; путь - транслит, при занятом пути добавляется -2, -3…</summary>
    public static async Task<GitLabProject> CreateProjectAsync(GitLabClient git, string name, long namespaceId, string description, string? ciConfigPath, CancellationToken ct)
    {
        var slug = ProjectNames.Slug(name);
        for (var attempt = 1; ; attempt++)
        {
            var path = attempt == 1 ? slug : $"{slug}-{attempt}";
            try
            {
                return await git.CreateProjectAsync(name, path, namespaceId, description, MainBranch, ct, ciConfigPath);
            }
            catch (GitLabApiException ex) when (attempt < 10 && ex.StatusCode == 400 && ex.Message.Contains("path", StringComparison.OrdinalIgnoreCase)
                                                && ex.Message.Contains("taken", StringComparison.OrdinalIgnoreCase))
            {
                // путь занят другим проектом - пробуем следующий
            }
        }
    }

    /// <summary>Папки для выбора: корень + все подгруппы.</summary>
    public static async Task<List<(string Folder, long GroupId)>> ListFoldersAsync(GitLabClient git, string groupPath, CancellationToken ct)
    {
        var root = await git.FindGroupAsync(groupPath, ct)
                   ?? throw new GitLabApiException(404, $"Группа «{groupPath}» не найдена в GitLab (или нет доступа).");
        var result = new List<(string, long)> { ("", root.Id) };
        foreach (var g in await git.ListDescendantGroupsAsync(groupPath, ct) ?? new())
            result.Add((AutomationRepo.FolderOf(g.FullPath, groupPath), g.Id));
        return result.OrderBy(f => f.Item1, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Начальный JSON: первая запись - только версия, описание живёт в README.</summary>
    public static string InitialJson() => "[\n  {\n    \"_comment_\": \"Version: 1.0.0\"\n  }\n]\n";

    public static string TagName(AutomationVersion version) => $"v{version}";

    /// <summary>
    /// Тег vX.Y.Z и GitLab Release на коммите ветки main - история того, что и когда было на проде.
    /// Если тег уже есть: на том же коммите - ничего не делаем, на другом - предупреждение (версию не повысили?).
    /// </summary>
    public static async Task<(string Level, string Text, string? Url)> TagReleaseAsync(GitLabClient git, string project, string webUrl,
        AutomationVersion? version, string commitSha, string title, string description, CancellationToken ct)
    {
        if (version is null)
            return ("warn", "Тег не создан: в JSON нет «Version: X.Y.Z».", null);

        var tag = TagName(version.Value);
        if (await git.FindTagAsync(project, tag, ct) is { } existing)
        {
            return existing.Commit?.Id == commitSha
                ? ("info", $"Тег {tag} уже стоит на этом коммите.", $"{webUrl}/-/tags/{tag}")
                : ("warn", $"Тег {tag} уже есть на другом коммите - новый тег не создан. Похоже, версию не повысили.", $"{webUrl}/-/tags/{tag}");
        }

        await git.CreateTagAsync(project, tag, commitSha, title, ct);
        await git.CreateReleaseAsync(project, tag, $"{tag} - {title}", description, ct);
        return ("ok", $"Создан тег {tag} и Release.", $"{webUrl}/-/releases/{tag}");
    }

    public static string Readme(string name, string description) =>
        $"# {name}\n\n{description}\n\n" +
        "## Ветки\n\n" +
        $"- `{DevBranch}` - версия на тестовом сервере Pilot\n" +
        $"- `{MainBranch}` - версия на продовом сервере Pilot\n\n" +
        "## Файлы\n\n" +
        $"- `{name}.json` - триггеры автоматизации (версия в `_comment_`: `Version: X.Y.Z`)\n" +
        $"- `{name}/{name}.cs` - C#-вставка, если есть\n";

    /// <summary>
    /// Создаёт проект: начальный коммит в main (README + JSON), ветка dev от main.
    /// Если переданы версии с серверов - main получает версию прода, dev - версию теста.
    /// </summary>
    public static async Task<GitLabProject> CreateAsync(GitLabClient git, string name, long namespaceId, string description,
        Pilot.AutomationSnapshot? mainContent, Pilot.AutomationSnapshot? devContent, string? ciConfigPath, CancellationToken ct,
        Action<string, string>? log = null)
    {
        var project = await CreateProjectAsync(git, name, namespaceId, description, ciConfigPath, ct);
        var path = project.PathWithNamespace;

        var mainActions = new List<GitLabCommitAction> { GitLabCommitAction.Create(AutomationFiles.ReadmePath, Readme(name, description)) };
        mainActions.AddRange(FileActions(name, mainContent, description));
        mainActions.Add(GitLabCommitAction.Create(Changelog.FileName, Changelog.Initial(name, mainContent?.Version,
            mainContent is null ? null : "Импорт: версия, работавшая на проде на момент создания репозитория.")));
        var mainCommit = await git.CreateCommitAsync(path, MainBranch, null,
            mainContent is null ? $"{name}: создание автоматизации" : $"{name}: импорт с продового сервера", mainActions, ct);

        // Версия, которая уже работает на проде, сразу получает тег.
        if (mainContent?.Version is { } prodVersion)
            await TagReleaseAsync(git, path, project.WebUrl, prodVersion, mainCommit.Id,
                "импорт с продового сервера", $"Версия, работавшая на проде на момент создания репозитория.\n\n{description}", ct);

        await git.CreateBranchAsync(path, DevBranch, MainBranch, ct);

        if (devContent is not null && !SameContent(devContent, mainContent))
        {
            var devActions = new List<GitLabCommitAction>
            {
                GitLabCommitAction.Update(AutomationFiles.JsonPath(name), AutomationFiles.PrepareForStorage(devContent.Triggers)),
            };
            var devCs = AutomationFiles.PrepareForStorage(devContent.CSharpCode);
            var mainHasCs = mainContent?.HasCSharp == true;
            if (devCs.Length > 0)
                devActions.Add(mainHasCs ? GitLabCommitAction.Update(AutomationFiles.CsPath(name), devCs) : GitLabCommitAction.Create(AutomationFiles.CsPath(name), devCs));
            else if (mainHasCs)
                devActions.Add(GitLabCommitAction.Delete(AutomationFiles.CsPath(name)));
            await git.CreateCommitAsync(path, DevBranch, null, $"{name}: импорт с тестового сервера", devActions, ct);
        }

        await ProtectMainAsync(git, path, log, ct);
        return project;
    }

    private static IEnumerable<GitLabCommitAction> FileActions(string name, Pilot.AutomationSnapshot? content, string description)
    {
        if (content is null)
        {
            yield return GitLabCommitAction.Create(AutomationFiles.JsonPath(name), InitialJson());
            yield break;
        }

        yield return GitLabCommitAction.Create(AutomationFiles.JsonPath(name), AutomationFiles.PrepareForStorage(content.Triggers));
        if (content.HasCSharp)
            yield return GitLabCommitAction.Create(AutomationFiles.CsPath(name), AutomationFiles.PrepareForStorage(content.CSharpCode));
    }

    private static bool SameContent(Pilot.AutomationSnapshot a, Pilot.AutomationSnapshot? b) =>
        b is not null && AutomationFiles.SameContent(a.Triggers, b.Triggers) && AutomationFiles.SameContent(a.CSharpCode, b.CSharpCode);
}
