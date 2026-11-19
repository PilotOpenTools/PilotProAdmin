using System.Collections.Concurrent;
using System.Text.Json;
using ProAdmin.Infrastructure;
using Microsoft.Extensions.Options;

namespace ProAdmin.Jobs;

/// <summary>История операций: в памяти + data/jobs.json (последние <see cref="Keep"/>).</summary>
public sealed class JobStore
{
    private const int Keep = 300;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    private readonly ConcurrentDictionary<Guid, PublishJob> _jobs = new();
    private readonly string _path;
    private readonly ILogger<JobStore> _logger;
    private readonly object _fileLock = new();

    public JobStore(IOptions<AppOptions> options, ILogger<JobStore> logger)
    {
        _logger = logger;
        _path = Path.Combine(Path.GetFullPath(options.Value.DataDirectory), "jobs.json");
        Load();
    }

    public void Add(PublishJob job)
    {
        _jobs[job.Id] = job;
        Persist();
    }

    public PublishJob? Get(Guid id) => _jobs.TryGetValue(id, out var job) ? job : null;

    /// <summary>Операцию видит администратор прода и тот, кто её запустил (по входу в Pilot или по пользователю GitLab).</summary>
    public static bool CanSee(PublishJob job, Sessions.UserSession session, bool isProdAdmin) =>
        isProdAdmin ||
        (session.LoginCredential is { } credential &&
         string.Equals(job.PilotUser, credential.Login, StringComparison.OrdinalIgnoreCase)) ||
        (job.GitLabUser is { Length: > 0 } gl && string.Equals(gl, session.GitLabUser?.Username, StringComparison.OrdinalIgnoreCase));

    public IReadOnlyList<PublishJob> Recent(int count) =>
        _jobs.Values.Select(j => j.Snapshot()).OrderByDescending(j => j.CreatedAt).Take(count).ToList();

    public PublishJob? FindActive(string env, string name) =>
        _jobs.Values.FirstOrDefault(j => !j.IsFinished &&
                                         string.Equals(j.Env, env, StringComparison.OrdinalIgnoreCase) &&
                                         j.AutomationName == name);

    public void Persist()
    {
        try
        {
            var snapshot = Recent(Keep);
            lock (_fileLock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                var tmp = _path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(snapshot, Json));
                File.Move(tmp, _path, overwrite: true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Не удалось сохранить историю операций");
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path))
                return;

            var jobs = JsonSerializer.Deserialize<List<PublishJob>>(File.ReadAllText(_path)) ?? new();
            foreach (var job in jobs)
            {
                if (!job.IsFinished)
                {
                    job.Status = JobStatus.Failed;
                    job.Error = "Операция прервана перезапуском сервиса. Проверьте состояние MR и сервера вручную.";
                    job.FinishedAt ??= DateTimeOffset.UtcNow;
                }
                _jobs[job.Id] = job;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Не удалось прочитать историю операций {Path}", _path);
        }
    }
}
