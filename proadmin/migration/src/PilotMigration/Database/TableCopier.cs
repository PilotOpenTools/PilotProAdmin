using Npgsql;

namespace PilotMigration.Database;

/// <summary>Потоковое копирование строк между базами через COPY … (FORMAT BINARY) без промежуточных файлов.</summary>
public static class TableCopier
{
    /// <summary>Копирует результат запроса источника в таблицу приёмника (колонки - в одном порядке).</summary>
    public static async Task CopyAsync(
        NpgsqlConnection source, string sourceSelect,
        NpgsqlConnection target, string targetTable, string columnList,
        CancellationToken ct)
    {
        await using var input = await source.BeginRawBinaryCopyAsync($"COPY ({sourceSelect}) TO STDOUT (FORMAT BINARY)", ct);
        await using var output = await target.BeginRawBinaryCopyAsync($"COPY {targetTable} ({columnList}) FROM STDIN (FORMAT BINARY)", ct);
        await input.CopyToAsync(output, 1 << 20, ct);
    }

    /// <summary>Литералы массивов uuid пачками (COPY не принимает параметры - значения подставляются в текст запроса).</summary>
    public static IEnumerable<string> UuidBatches(IEnumerable<Guid> ids, int batchSize = 5000)
    {
        foreach (var chunk in ids.Chunk(batchSize))
            yield return "'{" + string.Join(",", chunk) + "}'::uuid[]";
    }

    public static async Task<long> CountAsync(NpgsqlConnection connection, string table, CancellationToken ct, NpgsqlTransaction? tx = null)
    {
        await using var cmd = new NpgsqlCommand($"select count(*) from {table}", connection, tx);
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public static async Task<int> ExecuteAsync(NpgsqlConnection connection, NpgsqlTransaction? tx, string sql, CancellationToken ct, params (string Name, object Value)[] parameters)
    {
        await using var cmd = new NpgsqlCommand(sql, connection, tx);
        foreach (var (name, value) in parameters)
            cmd.Parameters.AddWithValue(name, value);
        return await cmd.ExecuteNonQueryAsync(ct);
    }
}
