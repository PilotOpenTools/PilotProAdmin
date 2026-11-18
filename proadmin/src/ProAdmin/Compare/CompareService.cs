using ProAdmin.Automations;
using ProAdmin.GitLab;
using ProAdmin.Pilot;
using ProAdmin.Reports;
using ProAdmin.Settings;

namespace ProAdmin.Compare;

public enum CompareState
{
    Same,
    Differs,
    OnlyA,
    OnlyB,
}

/// <summary>Строка сравнения двух контуров (обычно тест и прод).</summary>
public sealed class CompareRow
{
    public required string Name { get; init; }
    public AutomationRepo? Repo { get; init; }
    public CompareState State { get; init; }

    /// <summary>Что именно отличается: JSON, C#, пауза, файл.</summary>
    public List<string> Differences { get; } = new();

    public string? VersionA { get; init; }
    public string? VersionB { get; init; }
    public string? DetailA { get; init; }
    public string? DetailB { get; init; }

    /// <summary>Папка для группировки: папка репозитория; для отчётов без репозитория - папка на сервере.</summary>
    public string? Folder { get; init; }
}

public sealed class CompareResult
{
    public List<CompareRow> Rows { get; } = new();
    public string? ErrorA { get; set; }
    public string? ErrorB { get; set; }
    public string? GitError { get; set; }

    public int Count(CompareState state) => Rows.Count(r => r.State == state);
}

/// <summary>
/// Сравнение того, что реально работает на двух серверах Pilot (по умолчанию тест / прод):
/// автоматизации (JSON, C#, пауза) и отчёты (.repx).
/// </summary>
public sealed class CompareService
{
    private readonly PilotGateway _pilot;
    private readonly ReportCatalog _reports;

    public CompareService(PilotGateway pilot, ReportCatalog reports)
    {
        _pilot = pilot;
        _reports = reports;
    }

    /// <summary>Контур-источник и контур-приёмник по умолчанию: ветка dev и ветка main.</summary>
    public static (EnvironmentSettings? A, EnvironmentSettings? B) DefaultPair(AdminSettings settings) =>
        (settings.Environments.FirstOrDefault(e => e.HasServer && e.Branch == AutomationRepositories.DevBranch)
         ?? settings.Environments.FirstOrDefault(e => e.HasServer),
         settings.Environments.FirstOrDefault(e => e.HasServer && e.Branch == AutomationRepositories.MainBranch)
         ?? settings.Environments.LastOrDefault(e => e.HasServer));

    public async Task<CompareResult> AutomationsAsync(AdminSettings settings, EnvironmentSettings a, EnvironmentSettings b,
        Func<string, PilotLogin> login, GitLabClient git, CancellationToken ct)
    {
        var result = new CompareResult();

        async Task<IReadOnlyList<AutomationSnapshot>?> Load(EnvironmentSettings env, Action<string> onError)
        {
            try { return await _pilot.GetAutomationsAsync(env.PilotUrl, login(env.Key), ct); }
            catch (PilotGatewayException ex) { onError(ex.Message); return null; }
        }

        var taskA = Load(a, m => result.ErrorA = m);
        var taskB = Load(b, m => result.ErrorB = m);
        var reposTask = LoadReposAsync(git, settings.GitLabGroupPath, result, ct);
        await Task.WhenAll(taskA, taskB, reposTask);
        if (taskA.Result is not { } listA || taskB.Result is not { } listB)
            return result;

        var repos = reposTask.Result;
        var byA = listA.GroupBy(x => x.Name).ToDictionary(g => g.Key, g => g.First());
        var byB = listB.GroupBy(x => x.Name).ToDictionary(g => g.Key, g => g.First());

        foreach (var name in byA.Keys.Union(byB.Keys))
        {
            byA.TryGetValue(name, out var sa);
            byB.TryGetValue(name, out var sb);
            var repo = repos.GetValueOrDefault(ProjectNames.Key(name));

            var row = new CompareRow
            {
                Name = name,
                Repo = repo,
                Folder = repo?.Folder,
                VersionA = sa?.Version?.ToString(),
                VersionB = sb?.Version?.ToString(),
                DetailA = sa is null ? null : sa.IsPaused ? "на паузе" : null,
                DetailB = sb is null ? null : sb.IsPaused ? "на паузе" : null,
                State = sa is null ? CompareState.OnlyB : sb is null ? CompareState.OnlyA : Differences(sa, sb).Count == 0 ? CompareState.Same : CompareState.Differs,
            };
            if (sa is not null && sb is not null)
                row.Differences.AddRange(Differences(sa, sb));
            result.Rows.Add(row);
        }

        Sort(result);
        return result;
    }

    public async Task<CompareResult> ReportsAsync(AdminSettings settings, EnvironmentSettings a, EnvironmentSettings b,
        Func<string, PilotLogin> login, GitLabClient git, CancellationToken ct)
    {
        var result = new CompareResult();
        var catalog = await _reports.LoadAsync(settings, login, git, ct);
        result.GitError = catalog.GitError;
        if (catalog.PilotErrors.TryGetValue(a.Key, out var ea)) result.ErrorA = ea.Message;
        if (catalog.PilotErrors.TryGetValue(b.Key, out var eb)) result.ErrorB = eb.Message;
        if (!catalog.Pilot.ContainsKey(a.Key) || !catalog.Pilot.ContainsKey(b.Key))
        {
            result.ErrorA ??= catalog.Pilot.ContainsKey(a.Key) ? null : $"Контур «{a.Title}» не участвует в отчётах (нужны сервер и ветка).";
            result.ErrorB ??= catalog.Pilot.ContainsKey(b.Key) ? null : $"Контур «{b.Title}» не участвует в отчётах (нужны сервер и ветка).";
            return result;
        }

        foreach (var r in catalog.Rows)
        {
            var pa = r.Envs.GetValueOrDefault(a.Key)?.Pilot;
            var pb = r.Envs.GetValueOrDefault(b.Key)?.Pilot;
            if (pa is null && pb is null)
                continue;

            var same = pa is not null && pb is not null &&
                       (pa.Sha is not null && pa.Sha == pb.Sha || pa.NormalizedSha is not null && pa.NormalizedSha == pb.NormalizedSha);
            var row = new CompareRow
            {
                Name = r.Name,
                Repo = r.Repo,
                Folder = r.Folder ?? (r.PilotFolder is { Length: > 0 } pf ? "Pilot: " + pf : null),
                DetailA = pa is null ? null : Size(pa.Size),
                DetailB = pb is null ? null : Size(pb.Size),
                State = pa is null ? CompareState.OnlyB : pb is null ? CompareState.OnlyA : same ? CompareState.Same : CompareState.Differs,
            };
            if (row.State == CompareState.Differs)
                row.Differences.Add("файл отчёта");
            if (pa is not null && pb is not null && pa.Folder != pb.Folder)
                row.Differences.Add($"папка: «{pa.Folder}» / «{pb.Folder}»");
            result.Rows.Add(row);
        }

        Sort(result);
        return result;
    }

    public static List<string> Differences(AutomationSnapshot a, AutomationSnapshot b)
    {
        var list = new List<string>();
        if (!AutomationFiles.SameContent(a.Triggers, b.Triggers)) list.Add("JSON");
        if (!AutomationFiles.SameContent(a.CSharpCode, b.CSharpCode)) list.Add("C#");
        if (a.IsPaused != b.IsPaused) list.Add("пауза");
        return list;
    }

    private static string Size(long bytes) => bytes >= 1024 * 1024 ? $"{bytes / 1024.0 / 1024:0.#} МБ" : $"{Math.Max(1, bytes / 1024)} КБ";

    private static async Task<Dictionary<string, AutomationRepo>> LoadReposAsync(GitLabClient git, string? group, CompareResult result, CancellationToken ct)
    {
        if (group is null)
            return new();
        try
        {
            return (await AutomationRepositories.ListAsync(git, group, ct))
                .GroupBy(r => ProjectNames.Key(r.Name)).ToDictionary(g => g.Key, g => g.First());
        }
        catch (GitLabApiException ex)
        {
            result.GitError = ex.Message;
            return new();
        }
    }

    private static void Sort(CompareResult result) =>
        result.Rows.Sort((x, y) =>
        {
            var c = string.Compare(x.Folder ?? "￿", y.Folder ?? "￿", StringComparison.OrdinalIgnoreCase);
            return c != 0 ? c : string.Compare(x.Name, y.Name, StringComparison.CurrentCultureIgnoreCase);
        });
}
