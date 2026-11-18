using System.Data;
using Npgsql;
using PilotMigration.Archives;
using PilotMigration.Database;

namespace PilotMigration.Engine;

/// <summary>
/// Полная копия: все таблицы базы приёмника заменяются данными источника в одной транзакции
/// (при ошибке приёмник остаётся прежним). Свой идентификатор базы (таблица databaseid) и свои файловые архивы
/// (faslist по settings.xml приёмника) у приёмника сохраняются. Тела файлов докопируются в архивы приёмника,
/// кроме общих с источником; лишние - удаляются.
/// </summary>
public static class FullCopy
{
    /// <summary>Таблицы, которые не копируются: идентичность базы приёмника.</summary>
    private static readonly HashSet<string> KeepInTarget = new(StringComparer.Ordinal) { "databaseid" };

    public static async Task PreSyncFilesAsync(MigrationContext ctx, FileTransfer files, RunLog log, CancellationToken ct)
    {
        // Пока приёмник работает - докопировать файлы, чтобы сократить простой.
        await using var source = await PgConnections.OpenAsync(ctx.SourcePg!, ctx.SourceEnv.Title, ct);
        var list = await ReadFileMapAsync(source, null, ctx.SourceSchema!, ct);
        log.Stat("Файлов в базе источника", list.Count);
        await files.CopyMissingAsync(list, "Файлы (до остановки приёмника)", ct);
    }

    /// <summary>Выполняется при остановленной базе приёмника. Возвращает Id тел файлов, на которые ссылается приёмник после копии.</summary>
    public static async Task<Dictionary<Guid, HashSet<Guid>>> CopyDatabaseAsync(MigrationContext ctx, FileTransfer files, RunLog log, CancellationToken ct)
    {
        var schema = ctx.TargetSchema!;
        await using var source = await PgConnections.OpenAsync(ctx.SourcePg!, ctx.SourceEnv.Title, ct);
        await using var target = await PgConnections.OpenAsync(ctx.TargetPg!, ctx.TargetEnv.Title, ct);

        // Снимок источника: все таблицы читаются на один момент времени, источник при этом работает.
        await using var sourceTx = await source.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        await TableCopier.ExecuteAsync(source, sourceTx, "SET TRANSACTION READ ONLY", ct);
        await using var tx = await target.BeginTransactionAsync(ct);
        await TableCopier.ExecuteAsync(target, tx, "SET LOCAL lock_timeout = '60s'", ct);

        var archiveNames = await ReadArchiveNamesAsync(target, tx, schema, ct);
        var tables = schema.LoadOrder(schema.Tables.Keys.Where(t => !KeepInTarget.Contains(t)));

        log.Phase("Копирование таблиц");
        // chats и messages ссылаются друг на друга, поэтому порядок загрузки не спасает: см. ForeignKeys.
        var foreignKeys = await ForeignKeys.ReadAsync(target, tx, schema, ct);
        await ForeignKeys.DropAsync(target, tx, schema, foreignKeys, ct);
        await TableCopier.ExecuteAsync(target, tx, "TRUNCATE " + string.Join(", ", tables.Select(t => schema.Q(t.Name))), ct);
        foreach (var table in tables)
        {
            ct.ThrowIfCancellationRequested();
            await TableCopier.CopyAsync(source, $"SELECT {table.ColumnList} FROM {schema.Q(table.Name)}", target, schema.Q(table.Name), table.ColumnList, ct);
            var rows = await TableCopier.CountAsync(target, schema.Q(table.Name), ct, tx);
            log.Info($"{table.Name}: {rows} строк");
            log.Stat("Строк: " + table.Name, rows);
        }
        await ResetIdentitiesAsync(target, tx, schema, tables, ct);
        await ForeignKeys.RestoreAsync(target, tx, schema, foreignKeys, ct);
        log.Info($"Внешние ключи возвращены и проверены: {foreignKeys.Count}.");

        // Файлы: какие тела нужны приёмнику и из какого архива источника они берутся.
        var fileMap = await ReadFileMapAsync(source, sourceTx, schema, ct);
        await ReconcileArchivesAsync(ctx, target, tx, archiveNames, log, ct);

        log.Phase("Файлы");
        await files.CopyMissingAsync(fileMap, "Файлы", ct);

        log.Phase("Фиксация");
        await tx.CommitAsync(ct);
        await sourceTx.RollbackAsync(CancellationToken.None);
        log.Ok("База приёмника заменена копией источника.");

        var referenced = new Dictionary<Guid, HashSet<Guid>>();
        await using (var cmd = new NpgsqlCommand($"select file_id, fa_id from {schema.Q("famediator")}", target))
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                var fa = reader.GetGuid(1);
                if (!referenced.TryGetValue(fa, out var set))
                    referenced[fa] = set = new HashSet<Guid>();
                set.Add(reader.GetGuid(0));
            }
        }
        return referenced;
    }

    /// <summary>Удаляет из собственных архивов приёмника тела, на которые база больше не ссылается.</summary>
    public static async Task PruneAsync(MigrationContext ctx, FileTransfer files, Dictionary<Guid, HashSet<Guid>> referenced, RunLog log, CancellationToken ct)
    {
        foreach (var archive in ctx.Archives!.Target)
        {
            if (ctx.Archives.IsSharedWithSource(archive) || archive.Store is null)
                continue;
            var deleted = await files.PruneAsync(archive, referenced.GetValueOrDefault(archive.Id) ?? new HashSet<Guid>(), null, ct);
            if (deleted > 0)
                log.Info($"Архив {archive.Title}: удалено {deleted} лишних файлов.");
            log.AddStat("Удалено лишних файлов", deleted);
        }
    }

    internal static async Task<List<(Guid FileId, Guid SourceArchive)>> ReadFileMapAsync(NpgsqlConnection source, NpgsqlTransaction? tx, Database.PilotPgSchema schema, CancellationToken ct)
    {
        var list = new List<(Guid, Guid)>();
        await using var cmd = new NpgsqlCommand($"select file_id, fa_id from {schema.Q("famediator")}", source, tx);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            list.Add((reader.GetGuid(0), reader.GetGuid(1)));
        return list;
    }

    internal static async Task<Dictionary<Guid, string>> ReadArchiveNamesAsync(NpgsqlConnection target, NpgsqlTransaction tx, Database.PilotPgSchema schema, CancellationToken ct)
    {
        var names = new Dictionary<Guid, string>();
        await using var cmd = new NpgsqlCommand($"select fa_id, name from {schema.Q("faslist")}", target, tx);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            names[reader.GetGuid(0)] = reader.IsDBNull(1) ? "" : reader.GetString(1);
        return names;
    }

    /// <summary>
    /// faslist - как в settings.xml приёмника: его архивы с его признаком «записываемый».
    /// Файлы из архивов источника, которых у приёмника нет, переназначаются на записываемый архив приёмника.
    /// </summary>
    internal static async Task ReconcileArchivesAsync(MigrationContext ctx, NpgsqlConnection target, NpgsqlTransaction tx,
        Dictionary<Guid, string> previousNames, RunLog log, CancellationToken ct)
    {
        var schema = ctx.TargetSchema!;
        var targetArchives = ctx.TargetSettings!.Archives;
        var ids = targetArchives.Select(a => a.Id).ToArray();
        var writable = targetArchives.FirstOrDefault(a => a.IsWritable)
                       ?? throw new MigrationException("У приёмника нет записываемого файлового архива.");

        var remapped = await TableCopier.ExecuteAsync(target, tx,
            $"UPDATE {schema.Q("famediator")} SET fa_id = @w WHERE NOT (fa_id = ANY(@ids))", ct, ("w", writable.Id), ("ids", ids));
        if (remapped > 0)
            log.Info($"{remapped} файлов из архивов, которых нет у приёмника, отнесены к его записываемому архиву {writable.Id}.");

        await TableCopier.ExecuteAsync(target, tx, $"DELETE FROM {schema.Q("faslist")} WHERE NOT (fa_id = ANY(@ids))", ct, ("ids", ids));
        foreach (var a in targetArchives)
        {
            var updated = await TableCopier.ExecuteAsync(target, tx,
                $"UPDATE {schema.Q("faslist")} SET is_writable = @w WHERE fa_id = @id", ct, ("w", a.IsWritable), ("id", a.Id));
            if (updated == 0)
            {
                var name = previousNames.GetValueOrDefault(a.Id);
                await TableCopier.ExecuteAsync(target, tx,
                    $"INSERT INTO {schema.Q("faslist")} (fa_id, name, creation_date, is_writable) VALUES (@id, @n, now(), @w)", ct,
                    ("id", a.Id), ("n", string.IsNullOrEmpty(name) ? (a.IsS3 ? "S3 архив" : "Файловый архив") : name), ("w", a.IsWritable));
            }
        }
    }

    internal static async Task ResetIdentitiesAsync(NpgsqlConnection target, NpgsqlTransaction tx, Database.PilotPgSchema schema, IEnumerable<PgTable> tables, CancellationToken ct)
    {
        foreach (var table in tables)
        {
            foreach (var column in table.IdentityColumns)
            {
                var q = schema.Q(table.Name);
                await TableCopier.ExecuteAsync(target, tx,
                    $"SELECT setval(pg_get_serial_sequence(@t, @c), COALESCE((SELECT max({PgNames.Quote(column)}) FROM {q}), 0) + 1, false)", ct,
                    ("t", q), ("c", column));
            }
        }
    }
}
