using System.Data;
using System.Text.Json;
using Npgsql;
using PilotMigration.Archives;
using PilotMigration.Config;
using PilotMigration.Database;
using PilotMigration.Pilot;

namespace PilotMigration.Engine;

/// <summary>
/// Выгрузка базы контура в файлы: base.dbp, chats.dbp, journal.dbp (та же структура, что у файловой базы Pilot,
/// по карте <see cref="DbpToPgMap"/>), тела файлов в FileArchive и manifest.json. Её читает импорт файловой базы -
/// так базу переносят на другой компьютер. Источник не останавливается: все таблицы читаются в одной транзакции
/// REPEATABLE READ, то есть на один момент времени.
/// </summary>
public static class FileDbExport
{
    private const long ProgressRows = 250_000;

    public static async Task ExportAsync(MigrationContext ctx, string requestedBy, RunLog log, CancellationToken ct)
    {
        var options = ctx.Request.FileTarget;
        var schema = ctx.SourceSchema!;
        var final = Path.Combine(ctx.ExportDirectory!, options.Name);
        var partial = final + ExportFiles.PartialSuffix;
        if (Directory.Exists(final))
            throw new MigrationException($"Выгрузка «{options.Name}» уже есть в каталоге выгрузок.");
        if (Directory.Exists(partial))
        {
            log.Warn($"Удаляю незавершённую выгрузку прошлого запуска: {partial}.");
            Directory.Delete(partial, recursive: true);
        }
        Directory.CreateDirectory(partial);

        var manifest = new ExportManifest
        {
            CreatedAt = DateTimeOffset.Now,
            CreatedBy = requestedBy,
            Source = ctx.SourceEnv.Key,
            SourceTitle = ctx.SourceEnv.Title,
            Database = ctx.SourceDb!.Name,
            DatabaseId = ctx.SourceDb.Id,
            ServerVersion = ctx.SourceDb.Version.ToString(),
            Schema = schema.Schema,
        };
        try
        {
            List<(Guid FileId, Guid SourceArchive)> fileMap;
            await using (var source = await PgConnections.OpenAsync(ctx.SourcePg!, ctx.SourceEnv.Title, ct))
            {
                // Снимок: все таблицы на один момент времени, источник при этом работает как обычно.
                await using var tx = await source.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
                await TableCopier.ExecuteAsync(source, tx, "SET TRANSACTION READ ONLY", ct);

                var databaseId = await ReadDatabaseIdAsync(source, tx, schema, ct);
                foreach (var role in Roles(options))
                {
                    var name = ExportFiles.FileName(role);
                    log.Phase($"Таблицы: {name} ({DbpFileSet.Title(role)})");
                    using var writer = DbpWriter.Create(Path.Combine(partial, name), role, databaseId);
                    foreach (var table in DbpToPgMap.Tables.Where(t => t.File == role))
                    {
                        ct.ThrowIfCancellationRequested();
                        await using var cmd = new NpgsqlCommand(DbpWriter.SelectSql(schema, table), source, tx);
                        await using var reader = await cmd.ExecuteReaderAsync(ct);
                        var rows = await writer.WriteTableAsync(table, reader,
                            n => { if (n % ProgressRows == 0) log.Info($"{table.Pg}: {n} строк…"); }, ct);
                        manifest.Rows[table.Pg] = rows;
                        log.Info($"{table.Pg}: {rows} строк");
                        log.Stat("Строк: " + table.Pg, rows);
                    }
                    if (role == DbpFile.Main)
                    {
                        var remapped = writer.ReconcileArchives(ctx.SourceSettings!.Archives
                            .Select(a => (a.Id, a.IsS3 ? "S3 архив" : "Файловый архив", a.IsWritable)).ToList());
                        if (remapped > 0)
                            log.Info($"{remapped} файлов из архивов, которых нет в settings.xml источника, отнесены к его записываемому архиву.");
                    }
                    manifest.Files[role.ToString()] = name;
                }
                fileMap = Remap(await FullCopy.ReadFileMapAsync(source, tx, schema, ct), ctx.SourceSettings!);
                await tx.RollbackAsync(CancellationToken.None);
            }
            log.Stat("Строк всего", manifest.Rows.Values.Sum());
            manifest.FileBodies = fileMap.Select(f => f.FileId).Distinct().LongCount();
            log.Stat("Файлов в базе", manifest.FileBodies);

            if (options.FileArchive)
            {
                log.Phase("Файлы");
                var folder = Path.Combine(partial, ExportFiles.ArchiveFolder);
                Directory.CreateDirectory(folder);
                using var target = new ResolvedArchive
                {
                    Id = Guid.Empty,
                    IsS3 = false,
                    IsWritable = true,
                    Location = folder,
                    StorageKey = "fs:" + folder,
                    Store = new FolderArchiveStore(folder),
                };
                var files = new FileTransfer(new ArchiveTopology(ctx.Archives!.Source, new[] { target }), log);
                await files.CopyMissingAsync(fileMap, "Файлы", ct);
                manifest.FileArchive = ExportFiles.ArchiveFolder;
                manifest.FileBodiesCopied = files.Copied;
                manifest.FileBodiesMissing = files.MissingInSource;
                manifest.FileBytes = files.CopiedBytes;
                log.Stat("Файлов скопировано", files.Copied);
                log.Stat("МБ скопировано", files.CopiedBytes / 1024 / 1024);
                if (files.MissingInSource > 0)
                    log.Stat("Файлов нет в архиве источника", files.MissingInSource);
            }
            else
            {
                log.Info("Файловый архив не отмечен - тела файлов не выгружаются, в базе остаётся только их индекс.");
            }

            await File.WriteAllTextAsync(Path.Combine(partial, ExportFiles.ManifestName),
                JsonSerializer.Serialize(manifest, ExportFiles.Json), ct);
            Directory.Move(partial, final);
            log.Ok($"Выгрузка готова: {final} ({Size(final)}).");
        }
        catch
        {
            try
            {
                if (Directory.Exists(partial))
                    Directory.Delete(partial, recursive: true);
            }
            catch (IOException ex)
            {
                log.Warn($"Не удалось удалить незавершённую выгрузку {partial}: {ex.Message}");
            }
            throw;
        }
    }

    private static IEnumerable<DbpFile> Roles(FileTargetOptions options)
    {
        yield return DbpFile.Main;
        if (options.Chats)
            yield return DbpFile.Messages;
        if (options.Journal)
            yield return DbpFile.Journal;
    }

    /// <summary>Строка databaseid источника: по ней импорт проверяет, что все файлы сняты с одной базы.</summary>
    private static async Task<Dictionary<string, object?>> ReadDatabaseIdAsync(NpgsqlConnection source, NpgsqlTransaction tx,
        PilotPgSchema schema, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand($"select * from {schema.Q("databaseid")} order by id limit 1", source, tx);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            throw new MigrationException($"Таблица databaseid схемы {schema.Schema} пуста.");
        var row = new Dictionary<string, object?>(StringComparer.Ordinal);
        for (var i = 0; i < reader.FieldCount; i++)
            row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
        return row;
    }

    /// <summary>Тела из архивов, которых нет в settings.xml, ищутся в записываемом архиве - как при полной копии.</summary>
    private static List<(Guid FileId, Guid SourceArchive)> Remap(List<(Guid FileId, Guid SourceArchive)> map, ServerDatabaseSettings settings)
    {
        var known = settings.Archives.Select(a => a.Id).ToHashSet();
        var writable = settings.Archives.FirstOrDefault(a => a.IsWritable)?.Id;
        return writable is null
            ? map
            : map.Select(f => known.Contains(f.SourceArchive) ? f : (f.FileId, writable.Value)).ToList();
    }

    private static string Size(string folder)
    {
        var bytes = new DirectoryInfo(folder).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
        return bytes >= 1L << 30 ? $"{bytes / 1024.0 / 1024 / 1024:N1} ГБ" : $"{bytes / 1024.0 / 1024:N0} МБ";
    }

    /// <summary>Что покажет проверка перед выгрузкой.</summary>
    public static async Task<IEnumerable<string>> SummarizeAsync(MigrationContext ctx, CancellationToken ct)
    {
        var options = ctx.Request.FileTarget;
        var schema = ctx.SourceSchema!;
        var s = ctx.SourceEnv.Title;
        await using var source = await PgConnections.OpenAsync(ctx.SourcePg!, s, ct);
        var objects = await TableCopier.CountAsync(source, schema.Q("objects"), ct);
        var people = await TableCopier.CountAsync(source, schema.Q("people"), ct);
        var files = await TableCopier.CountAsync(source, schema.Q("famediator"), ct);

        var result = new List<string>
        {
            $"База «{ctx.SourceDb!.Name}» ({s}) выгружается в папку «{options.Name}» каталога выгрузок: " +
            $"{objects} объектов, {people} пользователей, {files} файлов. База источника не останавливается и не меняется.",
            $"{ExportFiles.FileName(DbpFile.Main)} - объекты, типы, пользователи, права, история, индекс файлов.",
        };
        if (options.Chats)
        {
            var chats = await TableCopier.CountAsync(source, schema.Q("chats"), ct);
            var messages = await TableCopier.CountAsync(source, schema.Q("messages"), ct);
            result.Add($"{ExportFiles.FileName(DbpFile.Messages)} - чаты: {chats}, сообщений {messages}.");
        }
        else
        {
            result.Add("Чаты не выгружаются.");
        }
        if (options.Journal)
        {
            var actions = await TableCopier.CountAsync(source, schema.Q("user_actions"), ct);
            result.Add($"{ExportFiles.FileName(DbpFile.Journal)} - журнал действий: {actions} записей (самая долгая часть).");
        }
        else
        {
            result.Add("Журнал действий пользователей не выгружается.");
        }
        result.Add(options.FileArchive
            ? $"{ExportFiles.ArchiveFolder}/ - тела файлов из архивов «{s}» в раскладке Pilot."
            : "Тела файлов не выгружаются: без них после импорта файлы не откроются.");
        result.Add("На другом компьютере: скопируйте папку выгрузки в каталог импорта (IMPORT_ROOT) и выберите в «Миграции» источник «Файловая база (.dbp)» - " +
                   $"папка {ExportFiles.ArchiveFolder} рядом с {ExportFiles.FileName(DbpFile.Main)} подхватится сама.");
        return result;
    }
}
