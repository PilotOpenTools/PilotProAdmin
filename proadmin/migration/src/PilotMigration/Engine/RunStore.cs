using System.Collections.Concurrent;
using System.Text.Json;
using PilotMigration.Config;

namespace PilotMigration.Engine;

/// <summary>История запусков: data/runs/&lt;id&gt;.json, в памяти - последние N.</summary>
public sealed class RunStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _directory;
    private readonly ConfigStore _config;
    private readonly ConcurrentDictionary<Guid, MigrationRun> _runs = new();
    private readonly ILogger<RunStore> _logger;

    public RunStore(ServiceOptions options, ConfigStore config, ILogger<RunStore> logger)
    {
        _directory = Path.Combine(options.DataDirectory, "runs");
        _config = config;
        _logger = logger;
        Directory.CreateDirectory(_directory);
        foreach (var file in Directory.EnumerateFiles(_directory, "*.json"))
        {
            try
            {
                var run = JsonSerializer.Deserialize<MigrationRun>(File.ReadAllText(file));
                if (run is null)
                    continue;
                if (!run.IsFinished)
                {
                    // Сервис перезапускался посреди переноса.
                    run.Status = RunStatus.Failed;
                    run.Error ??= "Сервис миграции был перезапущен во время переноса. Проверьте состояние базы приёмника.";
                    run.FinishedAt ??= DateTimeOffset.Now;
                }
                _runs[run.Id] = run;
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                _logger.LogWarning(ex, "Не прочитан {File}", file);
            }
        }
    }

    public void Add(MigrationRun run)
    {
        _runs[run.Id] = run;
        Save(run);
        Trim();
    }

    public MigrationRun? Get(Guid id) => _runs.TryGetValue(id, out var run) ? run : null;

    public IReadOnlyList<MigrationRun> List(int limit) =>
        _runs.Values.OrderByDescending(r => r.CreatedAt).Take(limit).Select(r => r.Snapshot()).ToList();

    public void Save(MigrationRun run)
    {
        MigrationRun snapshot = run.Snapshot();
        var path = Path.Combine(_directory, run.Id + ".json");
        try
        {
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(snapshot, JsonOptions));
            File.Move(tmp, path, overwrite: true);
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Не сохранён запуск {Id}", run.Id);
        }
    }

    private void Trim()
    {
        var keep = _config.Current.KeepRuns;
        foreach (var old in _runs.Values.Where(r => r.IsFinished).OrderByDescending(r => r.CreatedAt).Skip(keep).ToList())
        {
            _runs.TryRemove(old.Id, out _);
            try { File.Delete(Path.Combine(_directory, old.Id + ".json")); } catch (IOException) { }
        }
    }
}

/// <summary>Журнал запуска: пишет в запуск и сохраняет его.</summary>
public sealed class RunLog
{
    private readonly MigrationRun _run;
    private readonly RunStore _store;
    private DateTimeOffset _lastSave = DateTimeOffset.MinValue;

    public RunLog(MigrationRun run, RunStore store)
    {
        _run = run;
        _store = store;
    }

    public void Info(string text) => Add("info", text);
    public void Ok(string text) => Add("ok", text);
    public void Warn(string text) => Add("warn", text);
    public void Error(string text) => Add("error", text);

    public void Phase(string phase)
    {
        lock (_run) _run.Phase = phase;
        Add("phase", phase);
    }

    public void Stat(string key, long value)
    {
        lock (_run) _run.Stats[key] = value;
    }

    public void AddStat(string key, long delta)
    {
        lock (_run) _run.Stats[key] = (_run.Stats.TryGetValue(key, out var v) ? v : 0) + delta;
    }

    private void Add(string level, string text)
    {
        lock (_run) _run.Log.Add(new RunLogEntry(DateTimeOffset.Now, level, text));
        // Не чаще раза в секунду - журнал при копировании файлов бывает частым.
        if (level is "error" or "phase" || DateTimeOffset.Now - _lastSave > TimeSpan.FromSeconds(1))
        {
            _lastSave = DateTimeOffset.Now;
            _store.Save(_run);
        }
    }

    public void Flush() => _store.Save(_run);
}
