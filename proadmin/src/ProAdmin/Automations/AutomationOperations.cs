using ProAdmin.GitLab;
using ProAdmin.Jobs;
using ProAdmin.Pilot;
using ProAdmin.Settings;

namespace ProAdmin.Automations;

/// <summary>Где сейчас есть автоматизация: на каких серверах Pilot и есть ли репозиторий.</summary>
public sealed class AutomationPresence
{
    public required string Name { get; init; }
    public AutomationRepo? Repo { get; init; }
    public List<(EnvironmentSettings Env, AutomationSnapshot? Automation, string? Error)> Servers { get; } = new();

    public IEnumerable<EnvironmentSettings> ServersWithAutomation => Servers.Where(s => s.Automation is not null).Select(s => s.Env);
}

/// <summary>
/// Переименование и удаление автоматизации сразу во всех местах: серверы Pilot (все контуры) и репозиторий GitLab.
/// Это не изменение содержимого, поэтому выполняется напрямую, без MR/pipeline; каждое действие пишется в журнал операций.
/// </summary>
public sealed class AutomationOperations
{
    private readonly PilotGateway _pilot;
    private readonly JobStore _jobs;

    public AutomationOperations(PilotGateway pilot, JobStore jobs)
    {
        _pilot = pilot;
        _jobs = jobs;
    }

    public async Task<AutomationPresence> FindAsync(AdminSettings settings, Func<string, PilotLogin> login, GitLabClient git, string name, CancellationToken ct)
    {
        var repoTask = settings.GitLabGroupPath is { } group ? AutomationRepositories.FindAsync(git, group, name, ct) : Task.FromResult<AutomationRepo?>(null);
        var serverTasks = settings.Environments.Where(e => e.HasServer).Select(async env =>
        {
            try { return (env, await _pilot.GetAutomationAsync(env.PilotUrl, login(env.Key), name, ct), (string?)null); }
            catch (PilotGatewayException ex) { return (env, (AutomationSnapshot?)null, ex.Message); }
        }).ToList();

        var presence = new AutomationPresence { Name = name, Repo = await repoTask };
        foreach (var s in await Task.WhenAll(serverTasks))
            presence.Servers.Add(s);
        return presence;
    }

    /// <summary>Содержимое автоматизации в ветке репозитория (null - файла нет).</summary>
    public static async Task<AutomationSnapshot?> ReadBranchAsync(GitLabClient git, AutomationRepo repo, string branch, CancellationToken ct)
    {
        var json = await git.FindFileAsync(repo.ProjectPath, AutomationFiles.JsonPath(repo.Name), branch, ct);
        if (json is null)
            return null;
        var cs = await git.FindFileAsync(repo.ProjectPath, AutomationFiles.CsPath(repo.Name), branch, ct);
        return new AutomationSnapshot(repo.Name, json.Text, cs?.Text ?? "", false, 0);
    }

    public PublishJob StartLog(string operation, string name, string pilotUser, string? gitLabUser, string description)
    {
        var job = new PublishJob
        {
            Operation = operation,
            Env = "all",
            EnvTitle = "все контуры",
            Branch = "main, dev",
            AutomationName = name,
            CommitMessage = description,
            PilotUser = pilotUser,
            GitLabUser = gitLabUser,
        };
        job.Add("info", description);
        _jobs.Add(job);
        return job;
    }

    public void Finish(PublishJob job, Exception? error)
    {
        job.Update(j =>
        {
            j.Status = error is null ? JobStatus.Succeeded : JobStatus.Failed;
            j.Error = error?.Message;
            j.FinishedAt = DateTimeOffset.UtcNow;
        });
        job.Add(error is null ? "ok" : "error", error is null ? "Готово." : error.Message);
        _jobs.Persist();
    }

    /// <summary>
    /// Переименование: файлы в ветках dev и main / новый проект GitLab / автоматизация на всех серверах.
    /// </summary>
    public async Task RenameAsync(AutomationPresence presence, string newName, Func<string, PilotLogin> login, GitLabClient git,
        PublishJob job, CancellationToken ct)
    {
        var oldName = presence.Name;

        if (presence.Repo is { } repo)
        {
            foreach (var branch in new[] { AutomationRepositories.DevBranch, AutomationRepositories.MainBranch })
            {
                if (await git.FindBranchAsync(repo.ProjectPath, branch, ct) is null)
                {
                    job.Add("warn", $"В репозитории нет ветки {branch} - пропущена.");
                    continue;
                }

                var actions = new List<GitLabCommitAction>();
                if (await git.FindFileAsync(repo.ProjectPath, AutomationFiles.JsonPath(oldName), branch, ct) is not null)
                    actions.Add(GitLabCommitAction.Move(AutomationFiles.JsonPath(oldName), AutomationFiles.JsonPath(newName)));
                if (await git.FindFileAsync(repo.ProjectPath, AutomationFiles.CsPath(oldName), branch, ct) is not null)
                    actions.Add(GitLabCommitAction.Move(AutomationFiles.CsPath(oldName), AutomationFiles.CsPath(newName)));
                if (await git.FindFileAsync(repo.ProjectPath, AutomationFiles.ReadmePath, branch, ct) is { } readme && readme.Text.Contains(oldName))
                    actions.Add(GitLabCommitAction.Update(AutomationFiles.ReadmePath, readme.Text.Replace(oldName, newName)));

                if (actions.Count == 0)
                    continue;
                var message = $"{newName}: переименование из {oldName}";
                var commit = branch == AutomationRepositories.MainBranch
                    ? await AutomationRepositories.CommitToMainAsync(git, repo.ProjectPath, message, actions, ct)
                    : await git.CreateCommitAsync(repo.ProjectPath, branch, null, message, actions, ct);
                job.Add("ok", $"Ветка {branch}: файлы переименованы ({commit.ShortId}).", commit.WebUrl);
            }

            var renamed = await git.RenameProjectAsync(repo.ProjectPath, newName, ct);
            job.Add("ok", $"Репозиторий переименован: «{repo.Name}» / «{renamed.Name}» (путь {renamed.PathWithNamespace} не менялся).", renamed.WebUrl);
        }

        await RenameOnServersAsync(presence, oldName, newName, login, job, ct);
    }

    public async Task RenameOnServersAsync(AutomationPresence presence, string oldName, string newName, Func<string, PilotLogin> login,
        PublishJob job, CancellationToken ct)
    {
        foreach (var env in presence.ServersWithAutomation)
        {
            await _pilot.UpdateAutomationsAsync(env.PilotUrl, login(env.Key), list =>
            {
                if (list.Any(a => a.Name == newName))
                    throw new PilotGatewayException(PilotErrorKind.Other, $"На сервере {env.Title} уже есть автоматизация «{newName}».");
                var script = list.FirstOrDefault(a => a.Name == oldName)
                             ?? throw new PilotGatewayException(PilotErrorKind.Other, $"На сервере {env.Title} автоматизация «{oldName}» не найдена.");
                script.Name = newName;
            }, ct);
            job.Add("ok", $"Сервер {env.Title}: автоматизация переименована в «{newName}».");
        }
    }

    public enum RepoAction { Keep, Archive, Delete }

    public async Task DeleteAsync(AutomationPresence presence, IReadOnlyCollection<string> envKeys, RepoAction repoAction,
        Func<string, PilotLogin> login, GitLabClient git, PublishJob job, CancellationToken ct)
    {
        foreach (var env in presence.ServersWithAutomation.Where(e => envKeys.Contains(e.Key)))
        {
            await _pilot.UpdateAutomationsAsync(env.PilotUrl, login(env.Key), list =>
            {
                var index = list.FindIndex(a => a.Name == presence.Name);
                if (index >= 0) list.RemoveAt(index);
            }, ct);
            job.Add("ok", $"Сервер {env.Title}: автоматизация удалена.");
        }

        if (presence.Repo is not { } repo)
            return;

        switch (repoAction)
        {
            case RepoAction.Archive:
                await git.ArchiveProjectAsync(repo.ProjectPath, ct);
                job.Add("ok", $"Репозиторий {repo.ProjectPath} заархивирован (только чтение, можно восстановить в GitLab).", repo.WebUrl);
                break;
            case RepoAction.Delete:
                await git.DeleteProjectAsync(repo.ProjectPath, ct);
                job.Add("ok", $"Репозиторий {repo.ProjectPath} удалён.");
                break;
            default:
                job.Add("info", $"Репозиторий {repo.ProjectPath} оставлен без изменений.", repo.WebUrl);
                break;
        }
    }
}
