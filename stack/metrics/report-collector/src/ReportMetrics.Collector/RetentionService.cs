using Npgsql;
using NpgsqlTypes;

namespace ReportMetrics.Collector;

/// <summary>Таблица статистики, из которой удаляются строки старше срока хранения.</summary>
public interface IRetentionTarget
{
    string Name { get; }

    /// <summary>Удаляет не больше <paramref name="batch"/> строк со временем раньше <paramref name="cutoff"/>; возвращает число удалённых.</summary>
    Task<int> DeleteOlderThanAsync(DateTimeOffset cutoff, int batch, CancellationToken ct);
}

/// <summary>Удаляет персональные данные старше Collector:RetentionDays - при старте и затем раз в час.</summary>
public sealed class RetentionService : BackgroundService
{
    public const int DefaultDays = 30;
    public const int BatchSize = 5000;
    public static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    private readonly IReadOnlyList<IRetentionTarget> _targets;
    private readonly int _days;
    private readonly ILogger<RetentionService> _logger;

    public RetentionService(IEnumerable<IRetentionTarget> targets, int days, ILogger<RetentionService> logger)
    {
        _targets = targets.ToList();
        _days = days;
        _logger = logger;
    }

    /// <summary>0 - не удалять; отрицательное значение - ошибка настройки.</summary>
    public static int ParseDays(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return DefaultDays;
        if (!int.TryParse(value, out var days) || days < 0)
            throw new InvalidOperationException($"Collector:RetentionDays должно быть целым числом не меньше 0, получено \"{value}\"");
        return days;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_days == 0)
        {
            _logger.LogWarning("Collector:RetentionDays=0: срок хранения не ограничен, логины, ФИО и имена машин в статистике не удаляются.");
            return;
        }

        using var timer = new PeriodicTimer(Interval);
        do
        {
            await PurgeAsync(_targets, DateTimeOffset.UtcNow - TimeSpan.FromDays(_days), BatchSize, _logger, stoppingToken);
        }
        while (await WaitNextAsync(timer, stoppingToken));
    }

    private static async Task<bool> WaitNextAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try { return await timer.WaitForNextTickAsync(ct); }
        catch (OperationCanceledException) { return false; }
    }

    /// <summary>
    /// Чистит все таблицы пачками (каждая пачка - отдельная короткая транзакция). Ошибка одной таблицы
    /// пишется в журнал и не мешает остальным и следующему проходу. Возвращает число удалённых строк.
    /// </summary>
    public static async Task<int> PurgeAsync(IEnumerable<IRetentionTarget> targets, DateTimeOffset cutoff, int batch,
        ILogger logger, CancellationToken ct)
    {
        var total = 0;
        foreach (var target in targets)
        {
            var deleted = 0;
            try
            {
                int n;
                do
                {
                    n = await target.DeleteOlderThanAsync(cutoff, batch, ct);
                    deleted += n;
                }
                while (n >= batch);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning("Очистка {Table} не удалась: {Message}", target.Name, ex.Message);
            }

            if (deleted > 0)
                logger.LogInformation("Очистка {Table}: удалено строк старше {Cutoff:u}: {Count}", target.Name, cutoff, deleted);
            total += deleted;
        }
        return total;
    }

    /// <summary>Пачка по первичному ключу: подзапрос идёт по индексу времени и не держит долгих блокировок.</summary>
    internal static async Task<int> DeleteBatchAsync(NpgsqlDataSource db, string table, string key,
        DateTimeOffset cutoff, int batch, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand(
            $"DELETE FROM {table} WHERE {key} IN (SELECT {key} FROM {table} WHERE ts < $1 ORDER BY ts LIMIT $2 FOR UPDATE SKIP LOCKED)");
        cmd.Parameters.Add(new NpgsqlParameter { Value = cutoff, NpgsqlDbType = NpgsqlDbType.TimestampTz });
        cmd.Parameters.Add(new NpgsqlParameter { Value = batch, NpgsqlDbType = NpgsqlDbType.Integer });
        return await cmd.ExecuteNonQueryAsync(ct);
    }
}
