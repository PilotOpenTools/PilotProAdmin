namespace PilotMigration.Database;

public sealed record DbpColumn(string Pg, string Sqlite, DbpKind Kind)
{
    public static DbpColumn Of(string name, DbpKind kind) => new(name, name, kind);
}

/// <summary>Таблица приёмника и то, откуда её строки берутся в файловой базе.</summary>
public sealed record DbpTable(string Pg, DbpFile File, string Sqlite, IReadOnlyList<DbpColumn> Columns)
{
    public string PgColumnList => string.Join(", ", Columns.Select(c => PgNames.Quote(c.Pg)));
}

/// <summary>
/// Соответствие таблиц файловой базы Pilot (три файла SQLite) и схемы <c>dbp&lt;версия&gt;</c> в PostgreSQL.
/// Карта - источник истины: <see cref="Differences"/> сверяет её с настоящей схемой приёмника и с файлами,
/// и любое расхождение останавливает импорт. Так расходящиеся версии сервера видны до переноса,
/// а не на половине залитых таблиц (в файловой базе нет номера версии, сверять её больше нечем).
/// </summary>
public static class DbpToPgMap
{
    /// <summary>Таблицы приёмника, которые импорт не трогает: идентичность базы приёмника.</summary>
    public static readonly HashSet<string> KeepInTarget = new(StringComparer.Ordinal) { "databaseid" };

    /// <summary>Таблицы и колонки файловой базы, которых нет в PostgreSQL и которые сознательно отбрасываются.</summary>
    public static readonly HashSet<string> DroppedInPg = new(StringComparer.Ordinal) { "files", "faslist.mask" };

    public static readonly IReadOnlyList<DbpTable> Tables = Build();

    /// <summary>Таблицы, которые можно загрузить из выбранных файлов: чаты и журнал переносят не всегда.</summary>
    public static IEnumerable<DbpTable> For(DbpFileSet files) => Tables.Where(t => files.Has(t.File));

    private static IReadOnlyList<DbpTable> Build()
    {
        var main = new List<DbpTable>
        {
            Table("objects", DbpFile.Main,
                DbpColumn.Of("id", DbpKind.Uuid),
                DbpColumn.Of("data", DbpKind.Bytes),
                DbpColumn.Of("last_change", DbpKind.Int4)),
            Table("people", DbpFile.Main,
                DbpColumn.Of("id", DbpKind.Int4),
                DbpColumn.Of("login", DbpKind.Text),
                DbpColumn.Of("password", DbpKind.Text),
                DbpColumn.Of("data", DbpKind.Bytes),
                DbpColumn.Of("last_change", DbpKind.Int4)),
            Table("organisationunits", DbpFile.Main,
                DbpColumn.Of("id", DbpKind.Int4),
                DbpColumn.Of("data", DbpKind.Bytes),
                DbpColumn.Of("last_change", DbpKind.Int4)),
            Table("metadata", DbpFile.Main,
                DbpColumn.Of("id", DbpKind.Int2),
                DbpColumn.Of("data", DbpKind.Bytes)),
            Table("changesets", DbpFile.Main,
                DbpColumn.Of("id", DbpKind.Int8),
                DbpColumn.Of("data", DbpKind.Bytes),
                DbpColumn.Of("identity", DbpKind.Uuid)),
            Table("changeset_data", DbpFile.Main,
                DbpColumn.Of("id", DbpKind.Uuid),
                DbpColumn.Of("data", DbpKind.Bytes)),
            Table("history_items", DbpFile.Main,
                DbpColumn.Of("id", DbpKind.Uuid),
                DbpColumn.Of("object_id", DbpKind.Uuid),
                DbpColumn.Of("reason", DbpKind.Text),
                DbpColumn.Of("creator_id", DbpKind.Int4),
                DbpColumn.Of("created", DbpKind.Timestamp),
                DbpColumn.Of("object", DbpKind.Bytes),
                DbpColumn.Of("due_to_person", DbpKind.Int4)),
            Table("interitable_access", DbpFile.Main,
                DbpColumn.Of("object_id", DbpKind.Uuid),
                DbpColumn.Of("access", DbpKind.Bytes)),
            Table("rules", DbpFile.Main,
                DbpColumn.Of("id", DbpKind.Uuid),
                DbpColumn.Of("description", DbpKind.Bytes)),
            Table("rules_changeset_data", DbpFile.Main,
                DbpColumn.Of("rule_id", DbpKind.Uuid),
                DbpColumn.Of("changeset_data_id", DbpKind.Uuid)),
            Table("subscriptions", DbpFile.Main,
                DbpColumn.Of("obj_id", DbpKind.Uuid),
                DbpColumn.Of("person_id", DbpKind.Int4)),
            // id здесь - не ключ строки, а пользователь, которому адресовано уведомление (так в обоих хранилищах).
            Table("notifications", DbpFile.Main,
                DbpColumn.Of("id", DbpKind.Int4),
                DbpColumn.Of("data", DbpKind.Bytes)),
            Table("counters", DbpFile.Main,
                DbpColumn.Of("name", DbpKind.Text),
                DbpColumn.Of("data", DbpKind.Bytes)),
            Table("settings", DbpFile.Main,
                DbpColumn.Of("key", DbpKind.Text),
                DbpColumn.Of("data", DbpKind.Bytes)),
            Table("famediator", DbpFile.Main,
                DbpColumn.Of("file_id", DbpKind.Uuid),
                DbpColumn.Of("object_id", DbpKind.Uuid),
                DbpColumn.Of("fa_id", DbpKind.Uuid)),
            // creation_date в файловой базе - тики .NET, в остальных таблицах даты записаны строкой.
            Table("faslist", DbpFile.Main,
                DbpColumn.Of("fa_id", DbpKind.Uuid),
                DbpColumn.Of("name", DbpKind.Text),
                DbpColumn.Of("creation_date", DbpKind.TimestampTicks),
                DbpColumn.Of("is_writable", DbpKind.Bool)),
            Table("external_tasks", DbpFile.Main,
                DbpColumn.Of("id", DbpKind.Uuid),
                DbpColumn.Of("type", DbpKind.Text),
                DbpColumn.Of("initiator_id", DbpKind.Int4),
                DbpColumn.Of("date_created", DbpKind.Timestamp),
                DbpColumn.Of("date_started", DbpKind.Timestamp),
                DbpColumn.Of("date_finished", DbpKind.Timestamp),
                DbpColumn.Of("handler", DbpKind.Text),
                DbpColumn.Of("task_data", DbpKind.Bytes),
                DbpColumn.Of("is_canceled", DbpKind.Bool),
                DbpColumn.Of("source_id", DbpKind.Uuid),
                DbpColumn.Of("group_id", DbpKind.Uuid),
                DbpColumn.Of("object_id", DbpKind.Uuid),
                DbpColumn.Of("project_id", DbpKind.Uuid),
                DbpColumn.Of("progress", DbpKind.Text),
                DbpColumn.Of("additional", DbpKind.Text)),
        };

        var messages = new List<DbpTable>
        {
            Table("chats", DbpFile.Messages,
                DbpColumn.Of("id", DbpKind.Uuid),
                DbpColumn.Of("name", DbpKind.Text),
                DbpColumn.Of("description", DbpKind.Text),
                DbpColumn.Of("creator_id", DbpKind.Int4),
                DbpColumn.Of("type", DbpKind.Int2),
                DbpColumn.Of("last_message_id", DbpKind.Uuid),
                DbpColumn.Of("creation_date_utc", DbpKind.Timestamp)),
            Table("chat_members", DbpFile.Messages,
                DbpColumn.Of("chat_id", DbpKind.Uuid),
                DbpColumn.Of("person_id", DbpKind.Int4),
                DbpColumn.Of("is_admin", DbpKind.Bool),
                DbpColumn.Of("is_deleted", DbpKind.Bool),
                DbpColumn.Of("is_notifiable", DbpKind.Bool),
                DbpColumn.Of("date_updated", DbpKind.Timestamp),
                DbpColumn.Of("is_viewer_only", DbpKind.Bool)),
            Table("messages", DbpFile.Messages,
                DbpColumn.Of("id", DbpKind.Uuid),
                DbpColumn.Of("data", DbpKind.Bytes),
                DbpColumn.Of("creator_id", DbpKind.Int4),
                DbpColumn.Of("date_time_server_utc", DbpKind.Timestamp),
                DbpColumn.Of("date_time_local_utc", DbpKind.Timestamp),
                DbpColumn.Of("chat_id", DbpKind.Uuid),
                DbpColumn.Of("related_message_id", DbpKind.Uuid),
                DbpColumn.Of("type", DbpKind.Int2)),
            Table("message_person", DbpFile.Messages,
                DbpColumn.Of("chat_id", DbpKind.Uuid),
                DbpColumn.Of("message_id", DbpKind.Uuid),
                DbpColumn.Of("person_id", DbpKind.Int4),
                new DbpColumn("issent", "isSent", DbpKind.Bool),
                DbpColumn.Of("mark_as_unread", DbpKind.Bool),
                DbpColumn.Of("date", DbpKind.Timestamp)),
            // В PostgreSQL таблица переименована: relations / message_relations.
            new DbpTable("message_relations", DbpFile.Messages, "relations", new[]
            {
                DbpColumn.Of("object_id", DbpKind.Uuid),
                DbpColumn.Of("chat_id", DbpKind.Uuid),
                DbpColumn.Of("type", DbpKind.Int2),
                DbpColumn.Of("message_id", DbpKind.Uuid),
                DbpColumn.Of("is_deleted", DbpKind.Bool),
            }),
            Table("third_party_info", DbpFile.Messages,
                DbpColumn.Of("third_party_info", DbpKind.Text),
                new DbpColumn("messageid", "messageId", DbpKind.Uuid)),
        };

        var journal = new List<DbpTable>
        {
            Table("user_actions", DbpFile.Journal,
                DbpColumn.Of("id", DbpKind.Int8),
                DbpColumn.Of("person_id", DbpKind.Int4),
                DbpColumn.Of("ip", DbpKind.Text),
                DbpColumn.Of("server_date_time", DbpKind.Timestamp),
                DbpColumn.Of("changeset_id", DbpKind.Uuid),
                DbpColumn.Of("event_kind", DbpKind.Int4),
                DbpColumn.Of("object_id", DbpKind.Uuid),
                DbpColumn.Of("object_type", DbpKind.Int4),
                DbpColumn.Of("object_title", DbpKind.Text),
                DbpColumn.Of("additional_info", DbpKind.Bytes),
                DbpColumn.Of("error_string", DbpKind.Text),
                DbpColumn.Of("plugin_additional", DbpKind.Bytes)),
        };

        return main.Concat(messages).Concat(journal).ToList();
    }

    private static DbpTable Table(string name, DbpFile file, params DbpColumn[] columns) =>
        new(name, file, name, columns);

    /// <summary>Тип колонки в файловой базе - тот же класс хранения SQLite, что у настоящей базы Pilot.</summary>
    public static string SqliteType(DbpKind kind) => kind switch
    {
        DbpKind.Bytes => "BLOB",
        DbpKind.Uuid => "GUID",
        DbpKind.Int2 or DbpKind.Int4 or DbpKind.Int8 or DbpKind.TimestampTicks => "INTEGER",
        DbpKind.Text => "TEXT",
        DbpKind.Bool => "BOOLEAN",
        DbpKind.Timestamp => "DATETIME",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>
    /// Расхождения карты со схемой источника выгрузки. Таблица или колонка, которой нет в карте,
    /// не попала бы в файлы молча, поэтому любое расхождение останавливает выгрузку.
    /// </summary>
    public static List<string> ExportDifferences(PilotPgSchema schema)
    {
        var result = new List<string>();
        var mapped = Tables.ToDictionary(t => t.Pg, StringComparer.Ordinal);
        foreach (var name in schema.Tables.Keys.OrderBy(n => n, StringComparer.Ordinal))
        {
            if (!mapped.ContainsKey(name) && !KeepInTarget.Contains(name))
                result.Add($"таблицы {name} схемы {schema.Schema} нет в карте выгрузки (версия сервера новее, чем поддерживает выгрузка)");
        }
        foreach (var table in Tables)
        {
            if (!schema.Tables.TryGetValue(table.Pg, out var pg))
            {
                result.Add($"таблицы {table.Pg} нет в схеме {schema.Schema}");
                continue;
            }
            var expected = table.Columns.Select(c => c.Pg).ToHashSet(StringComparer.Ordinal);
            foreach (var extra in pg.Columns.Where(c => !expected.Contains(c)))
                result.Add($"колонки {table.Pg}.{extra} нет в карте выгрузки");
            foreach (var missing in expected.Where(c => !pg.Columns.Contains(c)))
                result.Add($"в таблице {table.Pg} нет колонки {missing}");
        }
        return result;
    }

    /// <summary>
    /// Расхождения карты импорта со схемой приёмника и с присланными файлами. Пустой список - импорт возможен.
    /// </summary>
    public static List<string> Differences(PilotPgSchema schema, DbpFileSet files)
    {
        var result = new List<string>();
        var tables = For(files).ToList();

        // Приёмник: все таблицы схемы должны быть либо в карте, либо среди сохраняемых.
        var mapped = tables.Select(t => t.Pg).ToHashSet(StringComparer.Ordinal);
        var skippedFiles = Tables.Except(tables).Select(t => t.Pg).ToHashSet(StringComparer.Ordinal);
        foreach (var name in schema.Tables.Keys.OrderBy(n => n, StringComparer.Ordinal))
        {
            if (mapped.Contains(name) || KeepInTarget.Contains(name) || skippedFiles.Contains(name))
                continue;
            result.Add($"таблица {name} есть в схеме {schema.Schema} приёмника, но её нет в карте импорта " +
                       "(версия сервера приёмника новее, чем поддерживает импорт)");
        }

        foreach (var table in tables)
        {
            if (!schema.Tables.TryGetValue(table.Pg, out var pg))
            {
                result.Add($"таблицы {table.Pg} нет в схеме {schema.Schema} приёмника");
                continue;
            }

            var expected = table.Columns.Select(c => c.Pg).ToHashSet(StringComparer.Ordinal);
            foreach (var extra in pg.Columns.Where(c => !expected.Contains(c)))
                result.Add($"в таблице {table.Pg} приёмника есть колонка {extra}, которую импорт не заполняет");
            foreach (var missing in expected.Where(c => !pg.Columns.Contains(c)))
                result.Add($"в таблице {table.Pg} приёмника нет колонки {missing}");

            if (!files.Tables(table.File).Contains(table.Sqlite, StringComparer.Ordinal))
            {
                result.Add($"в файле {files.Name(table.File)} нет таблицы {table.Sqlite}");
                continue;
            }

            var sqlite = files.Columns(table.File, table.Sqlite);
            foreach (var column in table.Columns.Where(c => !sqlite.Contains(c.Sqlite, StringComparer.Ordinal)))
                result.Add($"в таблице {table.Sqlite} файла {files.Name(table.File)} нет колонки {column.Sqlite}");
            var used = table.Columns.Select(c => c.Sqlite).ToHashSet(StringComparer.Ordinal);
            foreach (var extra in sqlite.Where(c => !used.Contains(c) && !DroppedInPg.Contains($"{table.Sqlite}.{c}")))
                result.Add($"в таблице {table.Sqlite} файла {files.Name(table.File)} есть колонка {extra}, " +
                           "которой нет в PostgreSQL (версия сервера, снявшего базу, новее)");
        }

        // Файлы: таблицы, которые импорт нигде не использует.
        foreach (var file in files.Files)
        {
            var known = Tables.Where(t => t.File == file).Select(t => t.Sqlite)
                .Concat(KeepInTarget).Concat(DroppedInPg).ToHashSet(StringComparer.Ordinal);
            foreach (var name in files.Tables(file).Where(n => !known.Contains(n)))
                result.Add($"в файле {files.Name(file)} есть таблица {name}, которую импорт не знает " +
                           "(версия сервера, снявшего базу, новее)");
        }

        return result;
    }
}
