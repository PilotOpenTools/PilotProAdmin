using ProAdmin.Automations;
using ProAdmin.GitLab;
using ProAdmin.Jobs;
using ProAdmin.Pilot;
using ProAdmin.Reports;
using ProAdmin.Settings;
using Microsoft.AspNetCore.Mvc;

namespace ProAdmin.Pages.Reports;

/// <summary>
/// Репозиторий для отчёта, которого нет в GitLab: пользователь выбирает папку (подгруппу),
/// main получает отчёт с прода, dev - с теста.
/// </summary>
public class CreateModel : AppPageModel
{
    private readonly PilotReportGateway _pilot;
    private readonly JobStore _jobs;

    public CreateModel(PilotReportGateway pilot, JobStore jobs)
    {
        _pilot = pilot;
        _jobs = jobs;
    }

    [BindProperty(SupportsGet = true)] public string Name { get; set; } = "";
    [BindProperty(SupportsGet = true)] public string? Folder { get; set; }
    [BindProperty] public string? Description { get; set; }

    public List<(string Folder, long GroupId)> Folders { get; private set; } = new();
    public List<string> Errors { get; } = new();
    public List<(EnvironmentSettings Env, PilotReport? Report)> Servers { get; } = new();

    /// <summary>Имя проекта GitLab: имя отчёта без символов, недопустимых в GitLab.</summary>
    public string ProjectName => ProjectNames.Suggest(Name, "Отчёт");
    public bool NameChanged => ProjectName != Name.Trim();
    public string? PilotFolder => Servers.Select(s => s.Report?.Folder).FirstOrDefault(f => !string.IsNullOrEmpty(f));

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        if (Prepare() is { } redirect)
            return redirect;
        if (await DenyReportsChangeAsync(ct) is { } denied)
            return denied;
        await LoadAsync(ct);
        Folder ??= ReportRepositories.GuessFolder(PilotFolder, Folders.Select(f => f.Folder));
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        if (Prepare() is { } redirect)
            return redirect;
        if (await DenyReportsChangeAsync(ct) is { } denied)
            return denied;
        await LoadAsync(ct);
        if (Errors.Count > 0)
            return Page();

        var target = Folders.FirstOrDefault(f => f.Folder == (Folder ?? ""));
        if (target == default)
        {
            Errors.Add("Выберите папку из списка.");
            return Page();
        }

        var git = CreateGitLabClient();
        var description = Description?.Trim() ?? "";
        var job = new PublishJob
        {
            Operation = "создание репозитория отчёта",
            Env = "reports",
            EnvTitle = "отчёты",
            Branch = "main, dev",
            AutomationName = ProjectName,
            CommitMessage = $"Репозиторий «{ProjectName}» в {ReportsGroupPath}/{Folder}".TrimEnd('/'),
            PilotUser = CurrentSession.PilotLogin,
            GitLabUser = CurrentSession.GitLabUser?.Username,
        };
        job.Add("info", job.CommitMessage);
        _jobs.Add(job);

        try
        {
            var existing = (await AutomationRepositories.ListAsync(git, ReportsGroupPath!, ct))
                .FirstOrDefault(r => ProjectNames.Key(r.Name) == ProjectNames.Key(Name));
            if (existing is not null)
            {
                Errors.Add($"Репозиторий отчёта уже есть: {existing.ProjectPath}.");
                Finish(job, Errors[0]);
                return Page();
            }

            var dev = ReportCatalog.DevEnv(AppSettings);
            var main = ReportCatalog.MainEnv(AppSettings);
            var devBytes = await ContentAsync(dev, ct);
            var mainBytes = await ContentAsync(main, ct);

            var project = await ReportRepositories.CreateAsync(git, ProjectName, target.GroupId, description, mainBytes, devBytes, AppSettings.ReportsCiConfigPath, ct, (level, text) => job.Add(level, text));
            job.Add("ok", $"Создан репозиторий {project.PathWithNamespace}: main / {Origin(mainBytes, main, devBytes, dev)}, dev / {Origin(devBytes, dev, mainBytes, main)}.", project.WebUrl);
            Finish(job, null);
            TempData["Flash"] = $"Создан репозиторий «{ProjectName}» с ветками main и dev.";
            return RedirectToPage("/Reports/Index");
        }
        catch (Exception ex) when (ex is GitLabApiException or PilotGatewayException)
        {
            Errors.Add(ex.Message);
            Finish(job, ex.Message);
            return Page();
        }
    }

    private static string Origin(byte[]? own, EnvironmentSettings? env, byte[]? other, EnvironmentSettings? otherEnv) =>
        own is not null ? $"сервер {env?.Title}" : other is not null ? $"сервер {otherEnv?.Title}" : "пусто";

    private void Finish(PublishJob job, string? error)
    {
        job.Update(j =>
        {
            j.Status = error is null ? JobStatus.Succeeded : JobStatus.Failed;
            j.Error = error;
            j.FinishedAt = DateTimeOffset.UtcNow;
        });
        job.Add(error is null ? "ok" : "error", error ?? "Готово.");
        _jobs.Persist();
    }

    private async Task<byte[]?> ContentAsync(EnvironmentSettings? env, CancellationToken ct)
    {
        var report = Servers.FirstOrDefault(s => s.Env == env).Report;
        return env is null || report is null
            ? null
            : await _pilot.DownloadAsync(env.PilotUrl, ReportLogin(env.Key), AppSettings.ReportsLicenseType, report.Id, ct);
    }

    private IActionResult? Prepare()
    {
        ViewData["Section"] = "reports";
        if (!GitLabConnected)
            return RedirectToGitLab();
        if (ReportsGroupPath is null)
            Errors.Add("В настройках не указана группа GitLab с отчётами.");
        if (string.IsNullOrWhiteSpace(Name))
            Errors.Add("Не указан отчёт.");
        return null;
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        if (Errors.Count > 0)
            return;
        try
        {
            Folders = await AutomationRepositories.ListFoldersAsync(CreateGitLabClient(), ReportsGroupPath!, ct);
        }
        catch (GitLabApiException ex)
        {
            Errors.Add($"GitLab: {ex.Message}");
            return;
        }

        var key = ProjectNames.Key(Name);
        foreach (var env in ReportCatalog.ReportEnvs(AppSettings))
        {
            try
            {
                var list = await _pilot.ListAsync(env.PilotUrl, ReportLogin(env.Key), AppSettings.ReportsLicenseType, withContent: false, ct);
                Servers.Add((env, list.Reports.FirstOrDefault(r => ProjectNames.Key(r.Name) == key)));
            }
            catch (PilotGatewayException ex)
            {
                Errors.Add($"{env.Title}: {ex.Message}");
            }
        }
    }
}
