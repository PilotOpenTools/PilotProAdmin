using Npgsql;

namespace PilotMigration.Database;

public sealed record PgConstraint(string Table, string Name, string Definition);

/// <summary>
/// Внешние ключи схемы Pilot снимаются на время загрузки таблиц и возвращаются в той же транзакции.
/// <para>
/// Без этого таблицы вообще не загрузить: <c>messages.chat_id</c> ссылается на <c>chats</c>, а
/// <c>chats.last_message_id</c> - на <c>messages</c>. Проверки внешних ключей выполняются в конце каждого
/// <c>COPY</c>, поэтому какую из двух таблиц ни загружай первой, она сошлётся на ещё пустую вторую
/// (у базы ГП последнее сообщение указано у всех 2484 чатов).
/// </para>
/// <para>
/// Возврат ключа проверяет уже загруженные данные, то есть заодно это проверка ссылочной целостности переноса.
/// Вся работа идёт в одной транзакции: при любой ошибке откат вернёт и данные, и ключи.
/// </para>
/// </summary>
public static class ForeignKeys
{
    public static async Task<List<PgConstraint>> ReadAsync(NpgsqlConnection target, NpgsqlTransaction? tx,
        PilotPgSchema schema, CancellationToken ct)
    {
        var result = new List<PgConstraint>();
        const string sql = "select c.relname, con.conname, pg_get_constraintdef(con.oid) " +
                           "from pg_constraint con " +
                           "join pg_class c on c.oid = con.conrelid " +
                           "join pg_namespace n on n.oid = c.relnamespace " +
                           "where con.contype = 'f' and n.nspname = @s " +
                           "order by c.relname, con.conname";
        await using var cmd = new NpgsqlCommand(sql, target, tx);
        cmd.Parameters.AddWithValue("s", schema.Schema);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result.Add(new PgConstraint(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        return result;
    }

    public static async Task DropAsync(NpgsqlConnection target, NpgsqlTransaction tx, PilotPgSchema schema,
        IEnumerable<PgConstraint> constraints, CancellationToken ct)
    {
        foreach (var fk in constraints)
            await TableCopier.ExecuteAsync(target, tx,
                $"ALTER TABLE {schema.Q(fk.Table)} DROP CONSTRAINT {PgNames.Quote(fk.Name)}", ct);
    }

    /// <summary>
    /// Удаляет строки с битыми ссылками - те, что не прошли бы внешний ключ.
    /// <para>
    /// Нужно при импорте файловой базы: SQLite объявленные внешние ключи не проверяет
    /// (<c>PRAGMA foreign_keys</c> сервер не включает), поэтому в присланной базе попадаются,
    /// например, участники чатов без пользователя. В PostgreSQL схема Pilot эти связи требует,
    /// и такие строки пришлось бы либо удалить, либо не загружать базу вовсе.
    /// </para>
    /// <para>
    /// Удаление повторяется, пока есть что удалять: вместе со строкой сиротами становятся её дети
    /// (сообщение без чата / адресаты этого сообщения).
    /// </para>
    /// </summary>
    public static async Task<Dictionary<string, long>> CleanOrphansAsync(NpgsqlConnection target, NpgsqlTransaction tx,
        PilotPgSchema schema, IReadOnlyList<PgConstraint> constraints, IEnumerable<PgForeignKey> keys, CancellationToken ct)
    {
        var known = constraints.Select(c => c.Table).ToHashSet(StringComparer.Ordinal);
        var removed = new Dictionary<string, long>(StringComparer.Ordinal);
        var list = keys.Where(k => known.Contains(k.Table) && schema.Has(k.Table) && schema.Has(k.RefTable)).ToList();

        long pass;
        do
        {
            ct.ThrowIfCancellationRequested();
            pass = 0;
            foreach (var key in list)
            {
                var child = schema.Q(key.Table);
                var column = PgNames.Quote(key.Column);
                var deleted = await TableCopier.ExecuteAsync(target, tx,
                    $"DELETE FROM {child} c WHERE c.{column} IS NOT NULL AND NOT EXISTS (" +
                    $"SELECT 1 FROM {schema.Q(key.RefTable)} p WHERE p.{PgNames.Quote(key.RefColumn)} = c.{column})", ct);
                if (deleted == 0)
                    continue;
                pass += deleted;
                var what = $"{key.Table}.{key.Column} / {key.RefTable}";
                removed[what] = removed.GetValueOrDefault(what) + deleted;
            }
        }
        while (pass > 0);

        return removed;
    }

    /// <summary>Возвращает ключи обратно; PostgreSQL при этом проверяет загруженные строки.</summary>
    public static async Task RestoreAsync(NpgsqlConnection target, NpgsqlTransaction tx, PilotPgSchema schema,
        IEnumerable<PgConstraint> constraints, CancellationToken ct)
    {
        foreach (var fk in constraints)
        {
            try
            {
                await TableCopier.ExecuteAsync(target, tx,
                    $"ALTER TABLE {schema.Q(fk.Table)} ADD CONSTRAINT {PgNames.Quote(fk.Name)} {fk.Definition}", ct);
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.ForeignKeyViolation)
            {
                throw new MigrationException(
                    $"Перенесённые данные нарушают связь {fk.Table} / {fk.Definition}: {ex.Detail ?? ex.MessageText}. " +
                    "Приёмник не изменён (транзакция отменена).");
            }
        }
    }
}
