using System.Data;
using Npgsql;
using NpgsqlTypes;
using PilotMigration.Database;
using PilotMigration.Objects;

namespace PilotMigration.Engine;

/// <summary>
/// Выборочный перенос при остановленной базе приёмника, в одной транзакции: пользователи и оргструктура,
/// объекты (дерево целиком или по Id с родителями и потомками, отчёты, модули) и их файлы.
/// Id объектов и пользователей сохраняются.
/// </summary>
public sealed class SelectiveCopy
{
    private readonly MigrationContext _ctx;
    private readonly RunLog _log;
    private readonly FileTransfer? _files;
    private PilotPgSchema Schema => _ctx.TargetSchema!;

    public SelectiveCopy(MigrationContext ctx, RunLog log, FileTransfer? files)
    {
        _ctx = ctx;
        _log = log;
        _files = files;
    }

    /// <summary>Тела файлов удалённых объектов - кандидаты на удаление из архива после фиксации.</summary>
    public List<(Guid FileId, Guid Archive)> RemovedFiles { get; } = new();

    /// <summary>Строит план по объектам без изменений (для проверки).</summary>
    public static async Task<ObjectPlan> BuildPlanAsync(MigrationContext ctx, NpgsqlConnection source, NpgsqlTransaction? sourceTx,
        NpgsqlConnection target, NpgsqlTransaction? targetTx, CancellationToken ct)
    {
        var schema = ctx.TargetSchema!;
        var s = await ObjectIndex.LoadAsync(source, sourceTx, schema.Q("objects"), ct);
        var t = await ObjectIndex.LoadAsync(target, targetTx, schema.Q("objects"), ct);
        var o = ctx.Request.Options;
        return ObjectPlan.Build(s, t, ctx.SourceCategory, ctx.TargetCategory, new ObjectSelectionRequest
        {
            TreeAll = o.Tree && o.TreeAll,
            TreeIds = o.Tree && !o.TreeAll ? o.TreeIds.Select(Guid.Parse).ToList() : Array.Empty<Guid>(),
            Categories = o.Categories,
        });
    }

    /// <summary>Типы переносимых объектов должны быть в приёмнике с теми же Id и именами.</summary>
    public static List<string> TypeMismatches(MigrationContext ctx, ObjectPlan plan)
    {
        var result = new List<string>();
        foreach (var typeId in plan.Copy.Select(id => plan.Source.Nodes[id].TypeId).Distinct())
        {
            var sName = MigrationContext.TypeName(ctx.SourceMetadata, typeId);
            var tName = MigrationContext.TypeName(ctx.TargetMetadata, typeId);
            if (sName is null)
                continue; // служебные объекты без типа в метаданных (например, удалённые навсегда)
            if (tName != sName)
                result.Add(tName is null ? $"тип «{sName}» (Id {typeId}) отсутствует в приёмнике" : $"Id {typeId}: «{sName}» в источнике, «{tName}» в приёмнике");
        }
        return result;
    }

    public static IEnumerable<string> Describe(ObjectPlan plan)
    {
        yield return $"Объекты: записать {plan.Copy.Count} (из них родителей, которых нет в приёмнике, - {plan.AddedAncestors.Count}), " +
                     $"удалить {plan.Delete.Count}, поправить список детей у {plan.Touch.Count}.";
        if (plan.NotFound.Count > 0)
            yield return $"Нет в источнике: {string.Join(", ", plan.NotFound.Take(10))}{(plan.NotFound.Count > 10 ? " …" : "")}.";
    }

    public async Task ExecuteAsync(CancellationToken ct)
    {
        var o = _ctx.Request.Options;
        await using var source = await PgConnections.OpenAsync(_ctx.SourcePg!, _ctx.SourceEnv.Title, ct);
        await using var target = await PgConnections.OpenAsync(_ctx.TargetPg!, _ctx.TargetEnv.Title, ct);
        await using var sourceTx = await source.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        await TableCopier.ExecuteAsync(source, sourceTx, "SET TRANSACTION READ ONLY", ct);
        await using var tx = await target.BeginTransactionAsync(ct);
        await TableCopier.ExecuteAsync(target, tx, "SET LOCAL lock_timeout = '60s'", ct);

        if (o.Users)
        {
            _log.Phase("Пользователи и оргструктура");
            await CopyUsersAsync(source, sourceTx, target, tx, ct);
        }

        List<(Guid FileId, Guid SourceArchive)> neededFiles = new();
        if (o.HasObjects)
        {
            _log.Phase("Объекты: анализ дерева");
            var plan = await BuildPlanAsync(_ctx, source, sourceTx, target, tx, ct);
            foreach (var line in Describe(plan))
                _log.Info(line);
            foreach (var w in plan.Warnings.Take(20))
                _log.Warn(w);
            var mismatches = TypeMismatches(_ctx, plan);
            if (mismatches.Count > 0)
                throw new MigrationException("Типы объектов в источнике и приёмнике различаются: " + string.Join("; ", mismatches.Take(10)) + ". Сначала перенесите конфигурацию.");

            _log.Phase("Объекты: запись");
            neededFiles = await CopyObjectsAsync(plan, source, sourceTx, target, tx, ct);
        }

        if (neededFiles.Count > 0 && _files is not null)
        {
            _log.Phase("Файлы");
            if (o.FileArchive)
                await _files.CopyMissingAsync(neededFiles, "Файлы объектов", ct);
        }

        _log.Phase("Фиксация");
        await tx.CommitAsync(ct);
        await sourceTx.RollbackAsync(CancellationToken.None);
        _log.Ok("Изменения в базе приёмника зафиксированы.");

        // Тела файлов удалённых объектов, на которые больше никто не ссылается.
        if (o.FileArchive && _files is not null && RemovedFiles.Count > 0)
        {
            var stillUsed = new HashSet<Guid>();
            await using (var cmd = new NpgsqlCommand($"select file_id from {Schema.Q("famediator")} where file_id = ANY(@ids)", target))
            {
                cmd.Parameters.AddWithValue("ids", RemovedFiles.Select(f => f.FileId).Distinct().ToArray());
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                    stillUsed.Add(reader.GetGuid(0));
            }
            foreach (var group in RemovedFiles.GroupBy(f => f.Archive))
            {
                var archive = _ctx.Archives!.Target.FirstOrDefault(a => a.Id == group.Key);
                if (archive is null)
                    continue;
                var deleted = await _files.PruneAsync(archive, stillUsed, group.Select(f => f.FileId), ct);
                if (deleted > 0)
                    _log.Info($"Архив {archive.Title}: удалено {deleted} файлов удалённых объектов.");
                _log.AddStat("Удалено файлов", deleted);
            }
        }
    }

    private async Task CopyUsersAsync(NpgsqlConnection source, NpgsqlTransaction sourceTx, NpgsqlConnection target, NpgsqlTransaction tx, CancellationToken ct)
    {
        var people = Schema.Tables["people"];
        var units = Schema.Tables["organisationunits"];

        var sourcePeople = new List<(int Id, string Login)>();
        await using (var cmd = new NpgsqlCommand($"select id, login from {Schema.Q("people")}", source, sourceTx))
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
                sourcePeople.Add((reader.GetInt32(0), reader.GetString(1)));
        }
        var ids = sourcePeople.Select(p => p.Id).ToArray();
        var logins = sourcePeople.Select(p => p.Login).ToArray();

        // Логины уникальны: сначала освобождаем логины источника у записей приёмника.
        var renamed = await TableCopier.ExecuteAsync(target, tx,
            $"UPDATE {Schema.Q("people")} SET login = login || '#' || id WHERE NOT (id = ANY(@ids)) AND login = ANY(@logins)", ct,
            ("ids", ids), ("logins", logins));
        if (renamed > 0)
            _log.Warn($"{renamed} пользователей приёмника с тем же логином, но другим Id, переименованы (логин#Id).");
        await TableCopier.ExecuteAsync(target, tx,
            $"UPDATE {Schema.Q("people")} SET login = '__migration_' || id WHERE id = ANY(@ids)", ct, ("ids", ids));

        await TableCopier.ExecuteAsync(target, tx, $"CREATE TEMP TABLE mig_people (LIKE {Schema.Q("people")}) ON COMMIT DROP", ct);
        await TableCopier.CopyAsync(source, $"SELECT {people.ColumnList} FROM {Schema.Q("people")}", target, "mig_people", people.ColumnList, ct);
        var updates = string.Join(", ", people.Columns.Where(c => c != "id").Select(c => $"{PgNames.Quote(c)} = EXCLUDED.{PgNames.Quote(c)}"));
        var upserted = await TableCopier.ExecuteAsync(target, tx,
            $"INSERT INTO {Schema.Q("people")} ({people.ColumnList}) SELECT {people.ColumnList} FROM mig_people ON CONFLICT (id) DO UPDATE SET {updates}", ct);

        // Пользователи, которых нет в источнике: удаляются, если на них ничего не ссылается.
        var refs = Schema.ReferencesTo("people").ToList();
        var notReferenced = string.Join(" AND ", refs.Select(r =>
            $"NOT EXISTS (SELECT 1 FROM {Schema.Q(r.Table)} x WHERE x.{PgNames.Quote(r.Column)} = p.id)"));
        var deleted = await TableCopier.ExecuteAsync(target, tx,
            $"DELETE FROM {Schema.Q("people")} p WHERE NOT (p.id = ANY(@ids))" + (notReferenced.Length > 0 ? " AND " + notReferenced : ""), ct, ("ids", ids));
        long kept;
        await using (var cmd = new NpgsqlCommand($"select count(*) from {Schema.Q("people")} where not (id = ANY(@ids))", target, tx))
        {
            cmd.Parameters.AddWithValue("ids", ids);
            kept = (long)(await cmd.ExecuteScalarAsync(ct))!;
        }

        await TableCopier.ExecuteAsync(target, tx, $"DELETE FROM {Schema.Q("organisationunits")}", ct);
        await TableCopier.CopyAsync(source, $"SELECT {units.ColumnList} FROM {Schema.Q("organisationunits")}", target, Schema.Q("organisationunits"), units.ColumnList, ct);
        var unitCount = await TableCopier.CountAsync(target, Schema.Q("organisationunits"), ct, tx);

        await FullCopy.ResetIdentitiesAsync(target, tx, Schema, new[] { people, units }, ct);

        _log.Info($"Пользователи: записано {upserted}, удалено {deleted}. Оргструктура: {unitCount} подразделений и должностей.");
        if (kept > 0)
            _log.Warn($"{kept} пользователей приёмника, которых нет в источнике, оставлены: на них ссылаются история, чаты или подписки.");
        _log.Stat("Пользователей записано", upserted);
        _log.Stat("Пользователей удалено", deleted);
    }

    private async Task<List<(Guid FileId, Guid SourceArchive)>> CopyObjectsAsync(ObjectPlan plan,
        NpgsqlConnection source, NpgsqlTransaction sourceTx, NpgsqlConnection target, NpgsqlTransaction tx, CancellationToken ct)
    {
        var objects = Schema.Tables["objects"];
        var byObject = Schema.ReferencesTo("objects").ToList();

        // 1. Удалить из приёмника удаляемые объекты и старые версии заменяемых (со строками, которые на них ссылаются).
        var replace = plan.Delete.Concat(plan.Copy.Where(plan.Target.Contains)).Distinct().ToList();
        foreach (var batch in replace.Chunk(5000))
        {
            var ids = batch.ToArray();
            await using (var cmd = new NpgsqlCommand($"DELETE FROM {Schema.Q("famediator")} WHERE object_id = ANY(@ids) RETURNING file_id, fa_id", target, tx))
            {
                cmd.Parameters.AddWithValue("ids", ids);
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                    RemovedFiles.Add((reader.GetGuid(0), reader.GetGuid(1)));
            }
            foreach (var fk in byObject)
                await TableCopier.ExecuteAsync(target, tx, $"DELETE FROM {Schema.Q(fk.Table)} WHERE {PgNames.Quote(fk.Column)} = ANY(@ids)", ct, ("ids", ids));
            await TableCopier.ExecuteAsync(target, tx, $"DELETE FROM {Schema.Q("objects")} WHERE id = ANY(@ids)", ct, ("ids", ids));
        }
        _log.Info($"Удалено из приёмника: {plan.Delete.Count} объектов, заменяется: {replace.Count - plan.Delete.Count}.");
        _log.Stat("Объектов удалено", plan.Delete.Count);

        // 2. Строки источника: объекты, права, история, подписки, привязка файлов к архивам.
        var copy = plan.Copy.ToList();
        foreach (var batch in TableCopier.UuidBatches(copy))
            await TableCopier.CopyAsync(source, $"SELECT {objects.ColumnList} FROM {Schema.Q("objects")} WHERE id = ANY({batch})",
                target, Schema.Q("objects"), objects.ColumnList, ct);
        _log.Stat("Объектов записано", copy.Count);

        var skipped = new Dictionary<string, int>();
        foreach (var fk in byObject)
        {
            var table = Schema.Tables[fk.Table];
            // Строки, ссылающиеся на пользователей, которых нет в приёмнике, пропускаются.
            var personRefs = Schema.ForeignKeys.Where(f => f.Table == fk.Table && f.RefTable == "people").ToList();
            var filter = string.Join(" AND ", personRefs.Select(r =>
                $"EXISTS (SELECT 1 FROM {Schema.Q("people")} p WHERE p.id = x.{PgNames.Quote(r.Column)})"));
            skipped[fk.Table] = await CopyFilteredAsync(source, target, tx, table, fk.Column, copy, filter, ct);
        }
        foreach (var (table, count) in skipped.Where(kv => kv.Value > 0))
            _log.Warn($"{table}: {count} строк не перенесено - они ссылаются на пользователей, которых нет в приёмнике (перенесите и пользователей).");

        var fileMap = new List<(Guid FileId, Guid SourceArchive)>();
        foreach (var batch in TableCopier.UuidBatches(copy))
        {
            await using var cmd = new NpgsqlCommand($"select file_id, fa_id from {Schema.Q("famediator")} where object_id = ANY({batch})", source, sourceTx);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                fileMap.Add((reader.GetGuid(0), reader.GetGuid(1)));
        }
        var famediator = Schema.Tables["famediator"];
        // Тело файла может числиться за другим объектом приёмника - такие строки не дублируются.
        await CopyFilteredAsync(source, target, tx, famediator, "object_id", copy,
            $"NOT EXISTS (SELECT 1 FROM {Schema.Q("famediator")} f WHERE f.file_id = x.file_id)", ct);
        var targetArchives = _ctx.TargetSettings?.Archives.Select(a => a.Id).ToArray() ?? Array.Empty<Guid>();
        var writable = _ctx.TargetSettings?.Archives.FirstOrDefault(a => a.IsWritable)?.Id;
        if (writable is not null && copy.Count > 0)
        {
            await TableCopier.ExecuteAsync(target, tx,
                $"UPDATE {Schema.Q("famediator")} SET fa_id = @w WHERE object_id = ANY(@ids) AND NOT (fa_id = ANY(@fa))", ct,
                ("w", writable.Value), ("ids", copy.ToArray()), ("fa", targetArchives));
        }
        _log.Stat("Файлов у перенесённых объектов", fileMap.Count);

        // 3. Списки детей у родителей и LastChange - в пределах счётчика изменений приёмника.
        long maxChange;
        await using (var cmd = new NpgsqlCommand($"select coalesce(max(id), 0) from {Schema.Q("changesets")}", target, tx))
            maxChange = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));

        var edited = 0;
        var toEdit = plan.Copy.Concat(plan.Touch).Distinct().ToList();
        foreach (var batch in toEdit.Chunk(1000))
        {
            var rows = new List<(Guid Id, byte[] Data, int LastChange)>();
            await using (var cmd = new NpgsqlCommand($"select id, data, last_change from {Schema.Q("objects")} where id = ANY(@ids)", target, tx))
            {
                cmd.Parameters.AddWithValue("ids", batch.ToArray());
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                    rows.Add((reader.GetGuid(0), (byte[])reader.GetValue(1), reader.GetInt32(2)));
            }

            foreach (var (id, data, lastChange) in rows)
            {
                var header = ObjectHeader.Parse(data);
                var newData = data;
                if (plan.ChildrenEdit(id, header.Children) is { } edit)
                    newData = ProtoWire.EditChildren(newData, edit.Keep, edit.Add) ?? newData;
                var newLastChange = lastChange;
                if (plan.Copy.Contains(id) && (lastChange > maxChange || header.LastChange > maxChange))
                {
                    newLastChange = (int)Math.Min(lastChange, maxChange);
                    newData = ProtoWire.SetLastChange(newData, Math.Min(header.LastChange, maxChange));
                }
                if (ReferenceEquals(newData, data) && newLastChange == lastChange)
                    continue;
                await using var update = new NpgsqlCommand($"UPDATE {Schema.Q("objects")} SET data = @d, last_change = @lc WHERE id = @id", target, tx);
                update.Parameters.Add(new NpgsqlParameter("d", NpgsqlDbType.Bytea) { Value = newData });
                update.Parameters.AddWithValue("lc", newLastChange);
                update.Parameters.AddWithValue("id", id);
                await update.ExecuteNonQueryAsync(ct);
                edited++;
            }
        }
        _log.Info($"Скорректированы связи родитель - дети у {edited} объектов.");
        return fileMap;
    }

    /// <summary>Копирует строки таблицы источника по списку Id объектов через временную таблицу; возвращает число отброшенных фильтром.</summary>
    private async Task<int> CopyFilteredAsync(NpgsqlConnection source, NpgsqlConnection target, NpgsqlTransaction tx,
        PgTable table, string keyColumn, IReadOnlyCollection<Guid> ids, string filter, CancellationToken ct)
    {
        var temp = "mig_" + table.Name;
        await TableCopier.ExecuteAsync(target, tx, $"DROP TABLE IF EXISTS {temp}", ct);
        await TableCopier.ExecuteAsync(target, tx, $"CREATE TEMP TABLE {temp} (LIKE {Schema.Q(table.Name)}) ON COMMIT DROP", ct);
        foreach (var batch in TableCopier.UuidBatches(ids))
            await TableCopier.CopyAsync(source, $"SELECT {table.ColumnList} FROM {Schema.Q(table.Name)} WHERE {PgNames.Quote(keyColumn)} = ANY({batch})",
                target, temp, table.ColumnList, ct);
        var total = await TableCopier.CountAsync(target, temp, ct, tx);
        var where = filter.Length > 0 ? " WHERE " + filter : "";
        var inserted = await TableCopier.ExecuteAsync(target, tx,
            $"INSERT INTO {Schema.Q(table.Name)} ({table.ColumnList}) SELECT {string.Join(", ", table.Columns.Select(c => "x." + PgNames.Quote(c)))} FROM {temp} x{where} ON CONFLICT DO NOTHING", ct);
        return (int)(total - inserted);
    }
}
