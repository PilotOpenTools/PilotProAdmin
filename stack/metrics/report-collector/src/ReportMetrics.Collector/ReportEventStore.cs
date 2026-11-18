using Npgsql;
using NpgsqlTypes;

namespace ReportMetrics.Collector;

/// <summary>
/// Хранилище событий в PostgreSQL. Та же база pilot_metrics, что и у метрик автоматизаций,
/// схема metrics, таблица report_events. Таблица создаётся при старте сервиса.
/// </summary>
public sealed class ReportEventStore : IRetentionTarget
{
    public const string Schema = """
        CREATE SCHEMA IF NOT EXISTS metrics;

        CREATE TABLE IF NOT EXISTS metrics.report_events (
            run_id          uuid         PRIMARY KEY,
            env             text         NOT NULL,
            ts              timestamptz  NOT NULL,
            received_at     timestamptz  NOT NULL DEFAULT now(),
            report          text         NOT NULL,
            report_id       uuid,
            status          text         NOT NULL,
            is_success      boolean      GENERATED ALWAYS AS (status = 'success') STORED,
            duration_ms     bigint,
            pages           integer,
            error_type      text,
            error_message   text,
            login           text,
            person          text,
            server          text,
            client_type     text,
            client_version  text,
            module_version  text,
            machine         text
        );

        CREATE INDEX IF NOT EXISTS report_events_ts_idx        ON metrics.report_events (ts);
        CREATE INDEX IF NOT EXISTS report_events_report_ts_idx ON metrics.report_events (report, ts);
        CREATE INDEX IF NOT EXISTS report_events_env_ts_idx    ON metrics.report_events (env, ts);
        """;

    private const string InsertSql = """
        INSERT INTO metrics.report_events
            (run_id, env, ts, report, report_id, status, duration_ms, pages, error_type, error_message,
             login, person, server, client_type, client_version, module_version, machine)
        VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, $14, $15, $16, $17)
        ON CONFLICT (run_id) DO NOTHING
        """;

    private readonly NpgsqlDataSource _db;
    private readonly ILogger<ReportEventStore> _logger;

    public ReportEventStore(NpgsqlDataSource db, ILogger<ReportEventStore> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>Создаёт схему. База может подниматься дольше сервиса - поэтому с повторами.</summary>
    public async Task EnsureSchemaAsync(CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await using var cmd = _db.CreateCommand(Schema);
                await cmd.ExecuteNonQueryAsync(ct);
                _logger.LogInformation("Схема metrics.report_events готова");
                return;
            }
            catch (Exception ex) when (attempt < 30 && !ct.IsCancellationRequested)
            {
                _logger.LogWarning("База недоступна ({Attempt}/30): {Message}", attempt, ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }
        }
    }

    public string Name => "metrics.report_events";

    public Task<int> DeleteOlderThanAsync(DateTimeOffset cutoff, int batch, CancellationToken ct) =>
        RetentionService.DeleteBatchAsync(_db, Name, "run_id", cutoff, batch, ct);

    /// <summary>Вставляет пачку. Возвращает число новых записей (повторы по run_id пропускаются).</summary>
    public async Task<int> InsertAsync(IReadOnlyList<ReportEventRow> rows, CancellationToken ct)
    {
        if (rows.Count == 0)
            return 0;

        await using var batch = _db.CreateBatch();
        foreach (var r in rows)
        {
            var cmd = new NpgsqlBatchCommand(InsertSql);
            cmd.Parameters.Add(new NpgsqlParameter { Value = r.RunId, NpgsqlDbType = NpgsqlDbType.Uuid });
            cmd.Parameters.Add(new NpgsqlParameter { Value = r.Env, NpgsqlDbType = NpgsqlDbType.Text });
            cmd.Parameters.Add(new NpgsqlParameter { Value = r.Ts, NpgsqlDbType = NpgsqlDbType.TimestampTz });
            cmd.Parameters.Add(new NpgsqlParameter { Value = r.Report, NpgsqlDbType = NpgsqlDbType.Text });
            cmd.Parameters.Add(Param(r.ReportId, NpgsqlDbType.Uuid));
            cmd.Parameters.Add(new NpgsqlParameter { Value = r.Status, NpgsqlDbType = NpgsqlDbType.Text });
            cmd.Parameters.Add(Param(r.DurationMs, NpgsqlDbType.Bigint));
            cmd.Parameters.Add(Param(r.Pages, NpgsqlDbType.Integer));
            cmd.Parameters.Add(Param(r.ErrorType, NpgsqlDbType.Text));
            cmd.Parameters.Add(Param(r.ErrorMessage, NpgsqlDbType.Text));
            cmd.Parameters.Add(Param(r.Login, NpgsqlDbType.Text));
            cmd.Parameters.Add(Param(r.Person, NpgsqlDbType.Text));
            cmd.Parameters.Add(Param(r.Server, NpgsqlDbType.Text));
            cmd.Parameters.Add(Param(r.ClientType, NpgsqlDbType.Text));
            cmd.Parameters.Add(Param(r.ClientVersion, NpgsqlDbType.Text));
            cmd.Parameters.Add(Param(r.ModuleVersion, NpgsqlDbType.Text));
            cmd.Parameters.Add(Param(r.Machine, NpgsqlDbType.Text));
            batch.BatchCommands.Add(cmd);
        }

        return await batch.ExecuteNonQueryAsync(ct);
    }

    public async Task<bool> PingAsync(CancellationToken ct)
    {
        try
        {
            await using var cmd = _db.CreateCommand("SELECT 1");
            await cmd.ExecuteScalarAsync(ct);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Проверка базы не прошла: {Message}", ex.Message);
            return false;
        }
    }

    /// <summary>Короткая сводка по отчётам - чтобы проверить сбор без Grafana.</summary>
    public async Task<IReadOnlyList<ReportSummary>> SummaryAsync(TimeSpan period, CancellationToken ct)
    {
        const string sql = """
            SELECT env, report,
                   count(*)                                   AS builds,
                   count(*) FILTER (WHERE is_success)         AS success,
                   count(*) FILTER (WHERE status = 'error')   AS errors,
                   max(ts)                                    AS last_ts
            FROM metrics.report_events
            WHERE ts >= now() - $1
            GROUP BY env, report
            ORDER BY builds DESC
            LIMIT 200
            """;

        await using var cmd = _db.CreateCommand(sql);
        cmd.Parameters.Add(new NpgsqlParameter { Value = period, NpgsqlDbType = NpgsqlDbType.Interval });

        var result = new List<ReportSummary>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result.Add(new ReportSummary(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetInt64(2),
                reader.GetInt64(3),
                reader.GetInt64(4),
                reader.GetFieldValue<DateTimeOffset>(5)));
        }
        return result;
    }

    private static NpgsqlParameter Param<T>(T value, NpgsqlDbType type) =>
        new() { Value = (object?)value ?? DBNull.Value, NpgsqlDbType = type };
}

public sealed record ReportSummary(string Env, string Report, long Builds, long Success, long Errors, DateTimeOffset LastTs);
