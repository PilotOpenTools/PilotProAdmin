using System.Text.Json;
using System.Text.Json.Serialization;
using PilotMigration.Config;
using PilotMigration.Engine;

namespace PilotMigration.Scheduling;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ScheduleKind
{
    /// <summary>Каждые N минут.</summary>
    Interval,

    /// <summary>В заданное время в выбранные дни недели.</summary>
    Weekly,
}

/// <summary>Перенос по расписанию.</summary>
public sealed class ScheduleEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public MigrationRequest Request { get; set; } = new();
    public ScheduleKind Kind { get; set; } = ScheduleKind.Weekly;
    public int IntervalMinutes { get; set; } = 1440;

    /// <summary>Время запуска «ЧЧ:ММ» (местное время сервиса).</summary>
    public string TimeOfDay { get; set; } = "03:00";

    /// <summary>Дни недели: 1 - понедельник … 7 - воскресенье. Пусто - каждый день.</summary>
    public List<int> Days { get; set; } = new();

    public string CreatedBy { get; set; } = "";
    public DateTimeOffset? UpdatedAt { get; set; }
    public DateTimeOffset? LastRunAt { get; set; }
    public Guid? LastRunId { get; set; }
    public string? LastResult { get; set; }
    public DateTimeOffset? NextRunAt { get; set; }

    public string? Validate()
    {
        if (string.IsNullOrWhiteSpace(Name))
            return "Укажите название расписания.";
        if (Kind == ScheduleKind.Interval && (IntervalMinutes < 15 || IntervalMinutes > 60 * 24 * 31))
            return "Интервал - от 15 минут до 31 дня.";
        if (Kind == ScheduleKind.Weekly && !TimeSpan.TryParse(TimeOfDay, out _))
            return "Время запуска - в формате ЧЧ:ММ.";
        if (Days.Any(d => d < 1 || d > 7))
            return "Дни недели - числа от 1 (пн) до 7 (вс).";
        if (Request.IsFileImport)
            return "Импорт файловой базы по расписанию не ставится: это разовый перенос присланных файлов.";
        if (Request.IsFileExport)
            return "Выгрузка в файлы по расписанию не ставится: она разовая, и каждая занимает место целиком.";
        return MigrationContext.DirectionError(Request.Source, Request.Target);
    }

    /// <summary>Следующий запуск после момента <paramref name="after"/>.</summary>
    public DateTimeOffset? ComputeNext(DateTimeOffset after)
    {
        if (!Enabled)
            return null;
        if (Kind == ScheduleKind.Interval)
        {
            var from = LastRunAt ?? after;
            var next = from.AddMinutes(IntervalMinutes);
            return next > after ? next : after.AddMinutes(1);
        }

        if (!TimeSpan.TryParse(TimeOfDay, out var time))
            return null;
        var local = after.ToLocalTime();
        for (var i = 0; i <= 7; i++)
        {
            var day = local.Date.AddDays(i);
            var candidate = new DateTimeOffset(day + time, TimeZoneInfo.Local.GetUtcOffset(day + time));
            var dow = (int)day.DayOfWeek == 0 ? 7 : (int)day.DayOfWeek;
            if (candidate > after && (Days.Count == 0 || Days.Contains(dow)))
                return candidate;
        }
        return null;
    }

    public string Describe()
    {
        if (Kind == ScheduleKind.Interval)
        {
            return IntervalMinutes % 1440 == 0 ? $"каждые {IntervalMinutes / 1440} сут."
                : IntervalMinutes % 60 == 0 ? $"каждые {IntervalMinutes / 60} ч"
                : $"каждые {IntervalMinutes} мин";
        }
        var names = new[] { "", "пн", "вт", "ср", "чт", "пт", "сб", "вс" };
        var days = Days.Count is 0 or 7 ? "ежедневно" : string.Join(", ", Days.OrderBy(d => d).Select(d => names[d]));
        return $"{days} в {TimeOfDay}";
    }
}

public sealed class ScheduleStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _path;
    private readonly object _lock = new();
    private List<ScheduleEntry> _items;

    public ScheduleStore(ServiceOptions options)
    {
        _path = Path.Combine(options.DataDirectory, "schedules.json");
        _items = File.Exists(_path)
            ? JsonSerializer.Deserialize<List<ScheduleEntry>>(File.ReadAllText(_path)) ?? new()
            : new();
        var now = DateTimeOffset.Now;
        foreach (var s in _items)
            s.NextRunAt = s.ComputeNext(now);
    }

    public IReadOnlyList<ScheduleEntry> List()
    {
        lock (_lock) return Clone(_items);
    }

    public ScheduleEntry? Get(Guid id)
    {
        lock (_lock) return Clone(_items).FirstOrDefault(s => s.Id == id);
    }

    public ScheduleEntry Upsert(ScheduleEntry entry)
    {
        lock (_lock)
        {
            var existing = _items.FirstOrDefault(s => s.Id == entry.Id);
            if (existing is not null)
            {
                entry.LastRunAt = existing.LastRunAt;
                entry.LastRunId = existing.LastRunId;
                entry.LastResult = existing.LastResult;
                entry.CreatedBy = existing.CreatedBy;
                _items.Remove(existing);
            }
            entry.Request.Normalize();
            entry.UpdatedAt = DateTimeOffset.Now;
            entry.NextRunAt = entry.ComputeNext(DateTimeOffset.Now);
            _items.Add(entry);
            Save();
            return entry;
        }
    }

    public bool Delete(Guid id)
    {
        lock (_lock)
        {
            var removed = _items.RemoveAll(s => s.Id == id) > 0;
            if (removed)
                Save();
            return removed;
        }
    }

    public void Update(Guid id, Action<ScheduleEntry> change)
    {
        lock (_lock)
        {
            var entry = _items.FirstOrDefault(s => s.Id == id);
            if (entry is null)
                return;
            change(entry);
            Save();
        }
    }

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_items, JsonOptions));
        File.Move(tmp, _path, overwrite: true);
    }

    private static List<ScheduleEntry> Clone(List<ScheduleEntry> items) =>
        JsonSerializer.Deserialize<List<ScheduleEntry>>(JsonSerializer.Serialize(items))!;
}

/// <summary>Раз в 20 секунд запускает наступившие расписания.</summary>
public sealed class SchedulerService : BackgroundService
{
    private readonly ScheduleStore _schedules;
    private readonly MigrationEngine _engine;
    private readonly ILogger<SchedulerService> _logger;

    public SchedulerService(ScheduleStore schedules, MigrationEngine engine, ILogger<SchedulerService> logger)
    {
        _schedules = schedules;
        _engine = engine;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                Tick(DateTimeOffset.Now);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Расписание миграций");
            }
            await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken);
        }
    }

    private void Tick(DateTimeOffset now)
    {
        foreach (var s in _schedules.List().Where(s => s.Enabled && s.NextRunAt is { } next && next <= now))
        {
            var run = _engine.TryStart(s.Request, $"расписание «{s.Name}»", $"расписание: {s.Name}", s.Id);
            _schedules.Update(s.Id, e =>
            {
                e.LastRunAt = now;
                if (run is not null)
                {
                    e.LastRunId = run.Id;
                    e.LastResult = "запущено";
                }
                else
                {
                    e.LastResult = "пропущено: в это время шёл другой перенос";
                }
                e.NextRunAt = e.ComputeNext(now);
            });
            _logger.LogInformation("Расписание {Name}: {Result}", s.Name, run is null ? "пропущено (занято)" : "запущено " + run.Id);
        }
    }
}
