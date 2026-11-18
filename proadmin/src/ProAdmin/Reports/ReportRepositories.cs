using System.Text.RegularExpressions;
using ProAdmin.Automations;
using ProAdmin.GitLab;

namespace ProAdmin.Reports;

/// <summary>
/// Репозиторий отчёта: проект в группе отчётов (подгруппы - папки), имя проекта = имя отчёта в Pilot.
/// Ветки: <c>dev</c> - отчёт на тестовом сервере, <c>main</c> - на продовом. Файлы: <c>Имя.repx</c>, <c>README.md</c>.
/// </summary>
public static partial class ReportRepositories
{
    public static string Readme(string name, string description) =>
        $"# {name}\n\n{(description.Length > 0 ? description + "\n\n" : "")}" +
        "## Ветки\n\n" +
        $"- `{AutomationRepositories.DevBranch}` - отчёт на тестовом сервере Pilot\n" +
        $"- `{AutomationRepositories.MainBranch}` - отчёт на продовом сервере Pilot\n\n" +
        "## Как меняется\n\n" +
        "- Сохранение отчёта в Pilot - модуль Reports предлагает отправить `.repx` в `dev` или, для исправления прода, в `hotfix/…` от `main` (прямо в `main` модуль не отправляет).\n" +
        "- Перенос на прод - веб-админка, вкладка «Отчёты»: Merge Request `dev` / `main`, после pipeline отчёт записывается на продовый сервер.\n\n" +
        "## Файлы\n\n" +
        $"- `{ReportFiles.DefaultPath(name)}` - отчёт DevExpress\n";

    /// <summary>
    /// Создаёт проект: main / отчёт с прода (или с теста, если на проде его нет), dev / отчёт с теста.
    /// </summary>
    public static async Task<GitLabProject> CreateAsync(GitLabClient git, string name, long namespaceId, string description,
        byte[]? mainContent, byte[]? devContent, string? ciConfigPath, CancellationToken ct,
        Action<string, string>? log = null)
    {
        var project = await AutomationRepositories.CreateProjectAsync(git, name, namespaceId, description, ciConfigPath, ct);
        var path = project.PathWithNamespace;
        var file = ReportFiles.DefaultPath(name);

        var mainActions = new List<GitLabCommitAction> { GitLabCommitAction.Create(AutomationFiles.ReadmePath, Readme(name, description)) };
        var mainBytes = mainContent ?? devContent;
        if (mainBytes is not null)
            mainActions.Add(GitLabCommitAction.CreateBinary(file, mainBytes));
        mainActions.Add(GitLabCommitAction.Create(Changelog.FileName, Changelog.Initial(name, null,
            mainContent is null ? null : "Импорт: версия, работавшая на проде на момент создания репозитория.")));
        await git.CreateCommitAsync(path, AutomationRepositories.MainBranch, null,
            mainContent is not null ? $"{name}: импорт с продового сервера"
            : devContent is not null ? $"{name}: импорт с тестового сервера (на проде отчёта нет)"
            : $"{name}: создание репозитория отчёта", mainActions, ct);

        await git.CreateBranchAsync(path, AutomationRepositories.DevBranch, AutomationRepositories.MainBranch, ct);

        if (devContent is not null && !ReportFiles.SameContent(devContent, mainBytes))
        {
            var action = mainBytes is null ? GitLabCommitAction.CreateBinary(file, devContent) : GitLabCommitAction.UpdateBinary(file, devContent);
            await git.CreateCommitAsync(path, AutomationRepositories.DevBranch, null, $"{name}: импорт с тестового сервера", new[] { action }, ct);
        }
        await AutomationRepositories.ProtectMainAsync(git, path, log, ct);
        return project;
    }

    /// <summary>
    /// Папка GitLab, подходящая к папке отчёта в Pilot: по совпадению имени или номера в начале
    /// («01-Сводные отчёты» / «01-folder»). "" - корень группы.
    /// </summary>
    public static string GuessFolder(string? pilotFolder, IEnumerable<string> gitFolders)
    {
        if (string.IsNullOrWhiteSpace(pilotFolder))
            return "";
        var folders = gitFolders.Where(f => f.Length > 0).ToList();
        var top = pilotFolder.Split('/')[0];

        var byName = folders.FirstOrDefault(f => ProjectNames.Key(f.Split('/').Last()) == ProjectNames.Key(top) ||
                                                 ProjectNames.Key(f) == ProjectNames.Key(pilotFolder));
        if (byName is not null)
            return byName;

        var number = LeadingNumber().Match(top);
        if (number.Success)
        {
            var byNumber = folders.FirstOrDefault(f => LeadingNumber().Match(f.Split('/').Last()) is { Success: true } m && m.Value == number.Value);
            if (byNumber is not null)
                return byNumber;
        }
        return "";
    }

    [GeneratedRegex(@"^\s*[0-9A-Za-zА-Яа-я]{1,3}(?=\s*[-_.\s])")]
    private static partial Regex LeadingNumber();
}
