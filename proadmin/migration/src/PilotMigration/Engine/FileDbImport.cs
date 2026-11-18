using Npgsql;
using NpgsqlTypes;
using PilotMigration.Database;

namespace PilotMigration.Engine;

/// <summary>
/// Импорт присланной файловой базы Pilot (три файла SQLite) в базу контура: приёмник останавливается,
/// все его таблицы заменяются строками из файлов в одной транзакции (ошибка / откат, приёмник прежний).
/// Как и при полной копии между контурами, у приёмника остаются свой идентификатор базы и свои файловые архивы,
/// а тела файлов докопируются из присланного архива.
/// </summary>
public static class FileDbImport
{
    /// <summary>Сколько строк между сообщениями в журнал о ходе загрузки большой таблицы.</summary>
    private const long ProgressRows = 250_000;

    /// <summary>Тела файлов, пока база приёмника ещё работает - чтобы сократить простой.</summary>
    public static async Task PreSyncFilesAsync(MigrationContext ctx, FileTransfer files, RunLog log, CancellationToken ct)
    {
        var map = ctx.Dbp!.FileMap();
        log.Stat("Файлов в присланной базе", map.Count);
        if (!ctx.CopiesFiles)
        {
            log.Info("Файловый архив не отмечен - тела файлов не копируются.");
            return;
        }
        await files.CopyMissingAsync(map, "Файлы (до остановки приёмника)", ct);
    }

    /// <summary>
    /// Выполняется при остановленной базе приёмника. Возвращает Id тел файлов, на которые ссылается
    /// приёмник после импорта - по архивам.
    /// </summary>
    public static async Task<Dictionary<Guid, HashSet<Guid>>> ImportAsync(MigrationContext ctx, FileTransfer files,
        RunLog log, CancellationToken ct)
    {
        var schema = ctx.TargetSchema!;
        var dbp = ctx.Dbp!;
        var options = ctx.Request.FileSource;

        await using var target = await PgConnections.OpenAsync(ctx.TargetPg!, ctx.TargetEnv.Title, ct);
        await using var tx = await target.BeginTransactionAsync(ct);
        await TableCopier.ExecuteAsync(target, tx, "SET LOCAL lock_timeout = '60s'", ct);

        var archiveNames = await FullCopy.ReadArchiveNamesAsync(target, tx, schema, ct);

        // Чистится вся схема, а не только импортируемые таблицы: иначе в приёмнике остались бы
        // чаты или журнал от прежней базы - со ссылками на пользователей и объекты, которых больше нет.
        var replaced = schema.Tables.Values
            .Where(t => !DbpToPgMap.KeepInTarget.Contains(t.Name))
            .OrderBy(t => t.Name, StringComparer.Ordinal)
            .ToList();

        log.Phase("Загрузка таблиц");
        var foreignKeys = await ForeignKeys.ReadAsync(target, tx, schema, ct);
        await ForeignKeys.DropAsync(target, tx, schema, foreignKeys, ct);
        await TableCopier.ExecuteAsync(target, tx, "TRUNCATE " + string.Join(", ", replaced.Select(t => schema.Q(t.Name))), ct);

        long total = 0;
        foreach (var table in DbpToPgMap.For(dbp))
        {
            ct.ThrowIfCancellationRequested();
            var rows = await CopyTableAsync(dbp, target, schema, table, log.Info, ct);
            total += rows;
            log.Info($"{table.Pg}: {rows} строк из {dbp.Name(table.File)}");
            log.Stat("Строк: " + table.Pg, rows);
        }
        log.Stat("Строк всего", total);

        await FullCopy.ResetIdentitiesAsync(target, tx, schema, replaced, ct);
        await FullCopy.ReconcileArchivesAsync(ctx, target, tx, archiveNames, log, ct);

        log.Phase("Строки с битыми ссылками");
        var orphans = await ForeignKeys.CleanOrphansAsync(target, tx, schema, foreignKeys, schema.ForeignKeys, ct);
        if (orphans.Count == 0)
        {
            log.Ok("Битых ссылок в присланной базе нет.");
        }
        else
        {
            var orphanRows = orphans.Values.Sum();
            log.Warn($"Удалено строк с битыми ссылками: {orphanRows}. В файловой базе внешние ключи не проверяются, " +
                     "а в PostgreSQL схема Pilot их требует - такие строки сервер всё равно не смог бы отдать клиенту.");
            foreach (var (what, count) in orphans.OrderByDescending(o => o.Value))
                log.Info($"  {what}: {count}");
            log.Stat("Удалено строк с битыми ссылками", orphanRows);
        }

        await ForeignKeys.RestoreAsync(target, tx, schema, foreignKeys, ct);
        log.Info($"Внешние ключи возвращены и проверены: {foreignKeys.Count}.");

        if (ctx.CopiesFiles)
        {
            log.Phase("Файлы");
            await files.CopyMissingAsync(dbp.FileMap(), "Файлы", ct);
        }

        log.Phase("Фиксация");
        await tx.CommitAsync(ct);
        log.Ok($"База «{ctx.TargetDatabaseName}» заменена присланной файловой базой.");

        var referenced = new Dictionary<Guid, HashSet<Guid>>();
        await using var cmd = new NpgsqlCommand($"select file_id, fa_id from {schema.Q("famediator")}", target);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var archive = reader.GetGuid(1);
            if (!referenced.TryGetValue(archive, out var set))
                referenced[archive] = set = new HashSet<Guid>();
            set.Add(reader.GetGuid(0));
        }
        return referenced;
    }

    /// <summary>Одна таблица: строки из SQLite пишутся в PostgreSQL потоком, без промежуточных файлов.</summary>
    internal static async Task<long> CopyTableAsync(DbpFileSet dbp, NpgsqlConnection target, PilotPgSchema schema,
        DbpTable table, Action<string> log, CancellationToken ct)
    {
        await using var command = dbp.Select(table.File, table.Sqlite, table.Columns.Select(c => c.Sqlite));
        await using var reader = await command.ExecuteReaderAsync(ct);
        await using var writer = await target.BeginBinaryImportAsync(
            $"COPY {schema.Q(table.Pg)} ({table.PgColumnList}) FROM STDIN (FORMAT BINARY)", ct);

        long rows = 0;
        while (await reader.ReadAsync(ct))
        {
            await writer.StartRowAsync(ct);
            for (var i = 0; i < table.Columns.Count; i++)
            {
                var column = table.Columns[i];
                var raw = reader.IsDBNull(i) ? null : reader.GetValue(i);
                var value = DbpValue.Convert(column.Kind, raw, table.Sqlite, column.Sqlite);
                await WriteAsync(writer, column.Kind, value, ct);
            }
            if (++rows % ProgressRows == 0)
                log($"{table.Pg}: загружено {rows} строк…");
        }

        await writer.CompleteAsync(ct);
        return rows;
    }

    private static async Task WriteAsync(NpgsqlBinaryImporter writer, DbpKind kind, object? value, CancellationToken ct)
    {
        if (value is null)
        {
            await writer.WriteNullAsync(ct);
            return;
        }
        switch (kind)
        {
            case DbpKind.Bytes:
                await writer.WriteAsync((byte[])value, NpgsqlDbType.Bytea, ct);
                break;
            case DbpKind.Uuid:
                await writer.WriteAsync((Guid)value, NpgsqlDbType.Uuid, ct);
                break;
            case DbpKind.Int2:
                await writer.WriteAsync((short)value, NpgsqlDbType.Smallint, ct);
                break;
            case DbpKind.Int4:
                await writer.WriteAsync((int)value, NpgsqlDbType.Integer, ct);
                break;
            case DbpKind.Int8:
                await writer.WriteAsync((long)value, NpgsqlDbType.Bigint, ct);
                break;
            case DbpKind.Text:
                await writer.WriteAsync((string)value, NpgsqlDbType.Text, ct);
                break;
            case DbpKind.Bool:
                await writer.WriteAsync((bool)value, NpgsqlDbType.Boolean, ct);
                break;
            case DbpKind.Timestamp:
            case DbpKind.TimestampTicks:
                await writer.WriteAsync((DateTime)value, NpgsqlDbType.TimestampTz, ct);
                break;
            default:
                throw new MigrationException($"Неизвестный тип колонки: {kind}.");
        }
    }

    /// <summary>Что покажет проверка перед импортом: сколько чего приедет.</summary>
    public static IEnumerable<string> Summarize(MigrationContext ctx)
    {
        var dbp = ctx.Dbp!;
        var options = ctx.Request.FileSource;
        var target = ctx.TargetEnv.Title;
        var db = ctx.TargetDb!.Name;

        var objects = dbp.Count(DbpFile.Main, "objects");
        var people = dbp.Count(DbpFile.Main, "people");
        var fileBodies = dbp.Count(DbpFile.Main, "famediator");
        yield return $"База «{db}» ({target}) будет остановлена, все её данные удалены и заменены присланной файловой базой: " +
                     $"{objects} объектов, {people} пользователей, {fileBodies} файлов.";
        if (dbp.Has(DbpFile.Messages))
            yield return $"Чаты: {dbp.Count(DbpFile.Messages, "chats")}, сообщений {dbp.Count(DbpFile.Messages, "messages")}.";
        else
            yield return "Чаты не переносятся - в приёмнике они будут пустыми.";
        if (dbp.Has(DbpFile.Journal))
            yield return $"Журнал действий: {dbp.Count(DbpFile.Journal, "user_actions")} записей (самая долгая часть загрузки).";
        else
            yield return "Журнал действий пользователей не переносится.";
        yield return "Сохраняются: идентификатор базы приёмника и его файловые архивы (settings.xml приёмника).";
        yield return $"После импорта база «{db}» запускается, клиенты «{target}» перечитают данные с сервера.";
    }
}
