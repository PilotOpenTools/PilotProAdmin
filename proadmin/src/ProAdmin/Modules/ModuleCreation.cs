using System.Text.RegularExpressions;
using ProAdmin.Automations;
using ProAdmin.GitLab;
using ProAdmin.Settings;

namespace ProAdmin.Modules;

/// <summary>Занятые номера клиентских модулей стенда и следующий свободный.</summary>
public sealed record ModuleCreationHints(string? SuggestedNumber, IReadOnlySet<string> UsedNumbers);

/// <summary>
/// Новый модуль в группе модулей GitLab: проект, каркас в main, ветка dev с шаблоном CI, защита веток.
/// Дальше модуль живёт по обычной схеме: задачи / dev (тест) / release (стейдж) / main (прод).
/// </summary>
public static partial class ModuleCreation
{
    /// <summary>Защита как у подключённых модулей: dev и main только через Merge Request, release/* - коммиты админки.</summary>
    private const int DevPushLevel = 0, DevMergeLevel = 30, ReleasePushLevel = 40, ReleaseMergeLevel = 40;

    public static Task<ModuleCreationHints> HintsAsync(ModuleKind kind, IEnumerable<string> knownAssemblies)
    {
        var used = knownAssemblies.Select(a => LeadingNumber().Match(Path.GetFileName(a ?? ""))).Where(m => m.Success)
            .Select(m => m.Value).ToHashSet();
        string? suggested = null;
        if (kind == ModuleKind.Client)
        {
            var next = used.Select(int.Parse).Where(n => n < 999).DefaultIfEmpty(100).Max() + 1;
            suggested = next.ToString("000");
        }

        return Task.FromResult(new ModuleCreationHints(suggested, used));
    }

    /// <summary>Создать репозиторий модуля. Журнал - в <paramref name="log"/> (уровень, текст, ссылка).</summary>
    public static async Task<GitLabProject> CreateAsync(GitLabClient git, AdminSettings settings, ModuleScaffoldRequest request, long namespaceId,
        bool deployToTest, Action<string, string, string?> log, CancellationToken ct)
    {
        var group = request.Kind.GroupPath(settings) ?? throw new InvalidOperationException("Не задана группа модулей GitLab.");
        var existing = (await AutomationRepositories.ListAsync(git, group, ct))
            .FirstOrDefault(r => ProjectNames.Key(r.Name) == ProjectNames.Key(request.Code) || string.Equals(r.Slug, ProjectNames.Slug(request.Code), StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
            throw new InvalidOperationException($"В группе уже есть репозиторий {existing.ProjectPath}.");

        var project = await AutomationRepositories.CreateProjectAsync(git, request.Code, namespaceId,
            request.Description.Trim().Length > 0 ? request.Description.Trim() : request.Title.Trim(), null, ct);
        var path = project.PathWithNamespace;
        log("ok", $"Создан проект {path}.", project.WebUrl);

        // Pilot.ClientSdk может ещё не быть в nuget.org: если в GitLab есть открытый реестр pilot-ci/nuget - он второй источник.
        if (request.Kind == ModuleKind.Client && request.UseClientSdk && request.PackageFeedUrl is null)
        {
            var feed = await PackageFeedUrlAsync(git, ct);
            request = request with { PackageFeedUrl = feed };
            log(feed is null ? "info" : "ok", feed is null
                ? $"Проекта {ModuleScaffold.PackageFeedProject} в GitLab нет - Pilot.ClientSdk только из nuget.org."
                : $"nuget.config: nuget.org и реестр {ModuleScaffold.PackageFeedProject}.", null);
        }
        var files = ModuleScaffold.Files(request);
        await git.CreateCommitAsync(path, ModuleBranches.Main, null, $"{request.Code}: каркас модуля из веб-админки",
            files.Select(f => GitLabCommitAction.Create(f.Path, f.Content)).ToList(), ct);
        log("ok", $"В main - каркас: {string.Join(", ", files.Select(f => f.Path))}.", null);

        await git.CreateBranchAsync(path, ModuleBranches.Dev, ModuleBranches.Main, ct);
        // Шаблон CI только в dev: в main он придёт с первым релизом, иначе pipeline main сразу выложил бы каркас на прод.
        var message = $"{ModuleWorkflowBase.CiCommitPrefix} .gitlab-ci.yml (сборка zip для Pilot)" + (deployToTest ? "" : " [skip ci]");
        await git.CreateCommitAsync(path, ModuleBranches.Dev, null, message,
            new[] { GitLabCommitAction.Create(".gitlab-ci.yml", ModuleCiTemplate.Content(settings, request.Kind)) }, ct);
        log("ok", deployToTest
            ? "Ветка dev от main и шаблон CI в ней: pipeline dev соберёт модуль и выложит его на тестовый сервер."
            : "Ветка dev от main и шаблон CI в ней (pipeline не запускался: [skip ci]).", null);

        await AutomationRepositories.ProtectMainAsync(git, path, (level, text) => log(level, text, null), ct);
        await ProtectAsync(git, path, "release/*", ReleasePushLevel, ReleaseMergeLevel, "release/* защищены: коммиты - только Maintainer (так делает админка при релизе).", log, ct);
        await ProtectAsync(git, path, ModuleBranches.Dev, DevPushLevel, DevMergeLevel, "Ветка dev защищена: изменения только через Merge Request.", log, ct);
        return project;
    }

    /// <summary>Переменные групп модулей с ключами выкладки: контур, имя, Protected (stage и prod - только защищённым веткам).</summary>
    public static readonly (string Env, string Variable, bool Protected)[] DeployKeyVariables =
    {
        ("test", "DEPLOY_KEY_TEST", false), ("stage", "DEPLOY_KEY_STAGE", true), ("prod", "DEPLOY_KEY_PROD", true),
    };

    /// <summary>
    /// Ключи выкладки в группе модулей: недостающие DEPLOY_KEY_TEST / _STAGE / _PROD создаются из ключей админки
    /// (MODULES_DEPLOY_KEY_*) - Masked, у stage и prod ещё Protected. Установщик задаёт их только группам, которые были
    /// при установке; без этого шага deploy_* модуля в группе, созданной позже, падает «Не задана переменная DEPLOY_KEY_TEST».
    /// Существующие переменные не меняются. Ошибки - предупреждения в журнал, модуль создаётся всё равно.
    /// </summary>
    public static async Task EnsureDeployKeysAsync(GitLabClient git, string group, Infrastructure.CiDeployOptions keys,
        Action<string, string, string?> log, CancellationToken ct)
    {
        var added = new List<string>();
        var noKey = new List<string>();
        foreach (var (env, name, isProtected) in DeployKeyVariables)
        {
            try
            {
                if (await git.GroupVariableExistsAsync(group, name, ct))
                    continue;
                var key = keys.KeyFor(env);
                if (string.IsNullOrWhiteSpace(key))
                {
                    noKey.Add(name);
                    continue;
                }
                await git.CreateGroupVariableAsync(group, name, key, masked: true, isProtected, ct);
                added.Add(name);
            }
            catch (GitLabApiException ex)
            {
                log("warn", $"Переменная {name} группы {group} не задана: {ex.Message}. Задайте её вручную (Settings / CI/CD / Variables группы, " +
                            $"значение MODULES_DEPLOY_KEY_{env.ToUpperInvariant()} из .env админки), иначе deploy_{env} модуля упадёт.", null);
            }
        }
        if (added.Count > 0)
            log("ok", $"В группе {group} заданы ключи выкладки из CI: {string.Join(", ", added)}.", null);
        if (noKey.Count > 0)
            log("warn", $"У админки нет ключей для {string.Join(", ", noKey)} (MODULES_DEPLOY_KEY_* в .env) - выкладка из CI на эти контуры выключена.", null);
    }

    /// <summary>
    /// Адрес реестра NuGet проекта <see cref="ModuleScaffold.PackageFeedProject"/> - каким его видят разработчики (от WebUrl проекта);
    /// null - проекта нет или GitLab недоступен.
    /// </summary>
    internal static async Task<string?> PackageFeedUrlAsync(GitLabClient git, CancellationToken ct)
    {
        try
        {
            var project = await git.FindProjectAsync(ModuleScaffold.PackageFeedProject, ct);
            return project is null ? null : PackageFeedUrl(project);
        }
        catch (GitLabApiException)
        {
            return null;
        }
    }

    internal static string? PackageFeedUrl(GitLabProject project)
    {
        var web = project.WebUrl.TrimEnd('/');
        var suffix = "/" + project.PathWithNamespace;
        if (!web.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            return null;
        return $"{web[..^suffix.Length]}/api/v4/projects/{project.Id}/packages/nuget/index.json";
    }

    private static async Task ProtectAsync(GitLabClient git, string project, string branch, int push, int merge, string done,
        Action<string, string, string?> log, CancellationToken ct)
    {
        try
        {
            if (await git.ProtectBranchAsync(project, branch, push, merge, ct))
                log("ok", done, null);
        }
        catch (GitLabApiException ex)
        {
            log("warn", $"Не удалось защитить {branch}: {ex.Message}. Настройте Protected branches вручную.", null);
        }
    }

    [GeneratedRegex(@"^\d{3}(?=\D)")]
    private static partial Regex LeadingNumber();

}
