using Npgsql;
using NpgsqlTypes;

namespace ReportMetrics.Collector;

/// <summary>
/// События клиентских модулей в PostgreSQL: та же база pilot_metrics, схема metrics, таблица module_events.
/// Таблица создаётся при старте сервиса.
/// </summary>
public sealed class ModuleEventStore : IRetentionTarget
{
    public const string Schema = """
        CREATE SCHEMA IF NOT EXISTS metrics;

        CREATE TABLE IF NOT EXISTS metrics.module_events (
            event_id        uuid         PRIMARY KEY,
            env             text         NOT NULL,
            ts              timestamptz  NOT NULL,
            received_at     timestamptz  NOT NULL DEFAULT now(),
            kind            text         NOT NULL,
            module          text         NOT NULL,
            module_title    text         NOT NULL,
            module_version  text,
            action          text,
            status          text         NOT NULL,
            is_success      boolean      GENERATED ALWAYS AS (status = 'success') STORED,
            duration_ms     bigint,
            items           bigint,
            error_type      text,
            error_message   text,
            login           text,
            person          text,
            server          text,
            client_type     text,
            client_version  text,
            machine         text,
            lib_version     text
        );

        CREATE INDEX IF NOT EXISTS module_events_ts_idx         ON metrics.module_events (ts);
        CREATE INDEX IF NOT EXISTS module_events_module_ts_idx  ON metrics.module_events (module_title, ts);
        CREATE INDEX IF NOT EXISTS module_events_env_kind_ts_idx ON metrics.module_events (env, kind, ts);
        """;

    private const string InsertSql = """
        INSERT INTO metrics.module_events
            (event_id, env, ts, kind, module, module_title, module_version, action, status, duration_ms, items,
             error_type, error_message, login, person, server, client_type, client_version, machine, lib_version)
        VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, $14, $15, $16, $17, $18, $19, $20)
        ON CONFLICT (event_id) DO NOTHING
        """;

    private readonly NpgsqlDataSource _db;
    private readonly ILogger<ModuleEventStore> _logger;

    public ModuleEventStore(NpgsqlDataSource db, ILogger<ModuleEventStore> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>Создаёт таблицу. Вызывается после схемы отчётов - база к этому моменту уже доступна.</summary>
    public async Task EnsureSchemaAsync(CancellationToken ct)
    {
        await using var cmd = _db.CreateCommand(Schema);
        await cmd.ExecuteNonQueryAsync(ct);
        _logger.LogInformation("Схема metrics.module_events готова");
    }

    public string Name => "metrics.module_events";

    public Task<int> DeleteOlderThanAsync(DateTimeOffset cutoff, int batch, CancellationToken ct) =>
        RetentionService.DeleteBatchAsync(_db, Name, "event_id", cutoff, batch, ct);

    /// <summary>Вставляет пачку. Возвращает число новых записей (повторы по event_id пропускаются).</summary>
    public async Task<int> InsertAsync(IReadOnlyList<ModuleEventRow> rows, CancellationToken ct)
    {
        if (rows.Count == 0)
            return 0;

        await using var batch = _db.CreateBatch();
        foreach (var r in rows)
        {
            var cmd = new NpgsqlBatchCommand(InsertSql);
            cmd.Parameters.Add(new NpgsqlParameter { Value = r.EventId, NpgsqlDbType = NpgsqlDbType.Uuid });
            cmd.Parameters.Add(new NpgsqlParameter { Value = r.Env, NpgsqlDbType = NpgsqlDbType.Text });
            cmd.Parameters.Add(new NpgsqlParameter { Value = r.Ts, NpgsqlDbType = NpgsqlDbType.TimestampTz });
            cmd.Parameters.Add(new NpgsqlParameter { Value = r.Kind, NpgsqlDbType = NpgsqlDbType.Text });
            cmd.Parameters.Add(new NpgsqlParameter { Value = r.Module, NpgsqlDbType = NpgsqlDbType.Text });
            cmd.Parameters.Add(new NpgsqlParameter { Value = r.ModuleTitle, NpgsqlDbType = NpgsqlDbType.Text });
            cmd.Parameters.Add(Param(r.ModuleVersion, NpgsqlDbType.Text));
            cmd.Parameters.Add(Param(r.Action, NpgsqlDbType.Text));
            cmd.Parameters.Add(new NpgsqlParameter { Value = r.Status, NpgsqlDbType = NpgsqlDbType.Text });
            cmd.Parameters.Add(Param(r.DurationMs, NpgsqlDbType.Bigint));
            cmd.Parameters.Add(Param(r.Items, NpgsqlDbType.Bigint));
            cmd.Parameters.Add(Param(r.ErrorType, NpgsqlDbType.Text));
            cmd.Parameters.Add(Param(r.ErrorMessage, NpgsqlDbType.Text));
            cmd.Parameters.Add(Param(r.Login, NpgsqlDbType.Text));
            cmd.Parameters.Add(Param(r.Person, NpgsqlDbType.Text));
            cmd.Parameters.Add(Param(r.Server, NpgsqlDbType.Text));
            cmd.Parameters.Add(Param(r.ClientType, NpgsqlDbType.Text));
            cmd.Parameters.Add(Param(r.ClientVersion, NpgsqlDbType.Text));
            cmd.Parameters.Add(Param(r.Machine, NpgsqlDbType.Text));
            cmd.Parameters.Add(Param(r.LibVersion, NpgsqlDbType.Text));
            batch.BatchCommands.Add(cmd);
        }

        return await batch.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Короткая сводка по модулям - проверить сбор без Grafana.</summary>
    public async Task<IReadOnlyList<ModuleSummary>> SummaryAsync(TimeSpan period, CancellationToken ct)
    {
        const string sql = """
            SELECT env, module_title,
                   count(*) FILTER (WHERE kind = 'action')                      AS actions,
                   count(*) FILTER (WHERE kind = 'action' AND status = 'error') AS errors,
                   count(DISTINCT login)                                         AS users,
                   (array_agg(module_version ORDER BY ts DESC)
                        FILTER (WHERE module_version IS NOT NULL))[1]            AS last_version,
                   max(ts)                                                       AS last_ts
            FROM metrics.module_events
            WHERE ts >= now() - $1
            GROUP BY env, module_title
            ORDER BY actions DESC
            LIMIT 300
            """;

        await using var cmd = _db.CreateCommand(sql);
        cmd.Parameters.Add(new NpgsqlParameter { Value = period, NpgsqlDbType = NpgsqlDbType.Interval });

        var result = new List<ModuleSummary>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result.Add(new ModuleSummary(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetInt64(2),
                reader.GetInt64(3),
                reader.GetInt64(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.GetFieldValue<DateTimeOffset>(6)));
        }
        return result;
    }

    private static NpgsqlParameter Param<T>(T value, NpgsqlDbType type) =>
        new() { Value = (object?)value ?? DBNull.Value, NpgsqlDbType = type };
}

public sealed record ModuleSummary(string Env, string Module, long Actions, long Errors, long Users, string? LastVersion, DateTimeOffset LastTs);
