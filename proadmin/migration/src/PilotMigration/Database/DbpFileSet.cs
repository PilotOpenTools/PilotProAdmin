using Microsoft.Data.Sqlite;
using PilotMigration.Config;

namespace PilotMigration.Database;

/// <summary>Роль файла в присланной базе. Имена файлов у выгрузок бывают любые, роль задаётся при выборе.</summary>
public enum DbpFile
{
    /// <summary>Объекты, метаданные (типы), пользователи, права, история, индекс файлового архива.</summary>
    Main,

    /// <summary>Чаты и сообщения.</summary>
    Messages,

    /// <summary>Журнал действий пользователей.</summary>
    Journal,
}

public sealed record DbpArchive(Guid Id, string Name, bool IsWritable, string Mask);

/// <summary>
/// Один файл базы Pilot при файловом хранении. Открывается с <c>immutable=1</c> - библиотека SQLite
/// не пишет в файл ни байта (в том числе не создаёт <c>-wal</c> и <c>-shm</c>), поэтому присланные файлы
/// остаются неизменными, даже если сняты с базы в режиме WAL.
/// </summary>
public sealed class DbpSqliteFile : IDisposable
{
    private readonly SqliteConnection _connection;

    private DbpSqliteFile(string path, SqliteConnection connection, IReadOnlyList<string> tables)
    {
        Path = path;
        _connection = connection;
        Tables = tables;
        Kind = DetectKind(tables);
        DatabaseId = ReadDatabaseId();
    }

    public string Path { get; }

    public string Name => System.IO.Path.GetFileName(Path);

    public IReadOnlyList<string> Tables { get; }

    public DbpFileKind Kind { get; }

    /// <summary>Идентификатор базы (таблица databaseid); null - таблицы нет или она пуста.</summary>
    public Guid? DatabaseId { get; }

    public static DbpSqliteFile Open(string path)
    {
        if (!File.Exists(path))
            throw new MigrationException($"Файл не найден: {path}");

        // URI с immutable=1 включается только для источника, начинающегося с «file:».
        var source = new Uri(System.IO.Path.GetFullPath(path)).AbsoluteUri + "?immutable=1";
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = source, Pooling = false }.ToString());
        try
        {
            connection.Open();
            return new DbpSqliteFile(path, connection, ReadTables(connection));
        }
        catch (SqliteException ex)
        {
            connection.Dispose();
            throw new MigrationException($"Файл {System.IO.Path.GetFileName(path)} не открывается как база SQLite: {ex.Message}");
        }
    }

    private static List<string> ReadTables(SqliteConnection connection)
    {
        var result = new List<string>();
        using var command = connection.CreateCommand();
        command.CommandText = "select name from sqlite_master where type = 'table' and name not like 'sqlite_%' order by name";
        using var reader = command.ExecuteReader();
        while (reader.Read())
            result.Add(reader.GetString(0));
        return result;
    }

    /// <summary>Вид файла - по набору таблиц, а не по имени.</summary>
    private static DbpFileKind DetectKind(IReadOnlyList<string> tables)
    {
        bool Has(string name) => tables.Contains(name, StringComparer.Ordinal);
        if (Has("objects") && Has("metadata"))
            return DbpFileKind.Main;
        if (Has("chats") && Has("messages"))
            return DbpFileKind.Chats;
        if (Has("user_actions"))
            return DbpFileKind.Journal;
        return DbpFileKind.Unknown;
    }

    private Guid? ReadDatabaseId()
    {
        if (!Tables.Contains("databaseid", StringComparer.Ordinal))
            return null;
        using var command = _connection.CreateCommand();
        command.CommandText = "select data from databaseid order by id limit 1";
        using var reader = command.ExecuteReader();
        return reader.Read() && !reader.IsDBNull(0) ? DbpValue.ToGuid(reader.GetValue(0)) : null;
    }

    /// <summary>Колонки таблицы в порядке объявления.</summary>
    public IReadOnlyList<string> Columns(string table)
    {
        var result = new List<string>();
        using var command = _connection.CreateCommand();
        command.CommandText = "select name from pragma_table_info($t) order by cid";
        command.Parameters.AddWithValue("$t", table);
        using var reader = command.ExecuteReader();
        while (reader.Read())
            result.Add(reader.GetString(0));
        return result;
    }

    public long Count(string table)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = $"select count(*) from {Quote(table)}";
        return Convert.ToInt64(command.ExecuteScalar());
    }

    /// <summary>Запрос строк таблицы: только перечисленные колонки, в этом же порядке.</summary>
    public SqliteCommand Select(string table, IEnumerable<string> columns) =>
        Command($"select {string.Join(", ", columns.Select(Quote))} from {Quote(table)}");

    public SqliteCommand Command(string sql)
    {
        var command = _connection.CreateCommand();
        command.CommandText = sql;
        return command;
    }

    internal static string Quote(string name) => "[" + name.Replace("]", "]]") + "]";

    public void Dispose() => _connection.Dispose();
}

/// <summary>
/// Присланная файловая база: основной файл и, если их переносят, файлы чатов и журнала.
/// Каждый выбирается отдельно - лежать они могут в разных папках и называться как угодно.
/// </summary>
public sealed class DbpFileSet : IDisposable
{
    private readonly Dictionary<DbpFile, DbpSqliteFile> _files = new();

    public static string Title(DbpFile file) => file switch
    {
        DbpFile.Main => "основная база",
        DbpFile.Messages => "чаты",
        DbpFile.Journal => "журнал действий",
        _ => throw new ArgumentOutOfRangeException(nameof(file)),
    };

    /// <summary>Какой вид файла ждёт эта роль.</summary>
    public static DbpFileKind Expected(DbpFile file) => file switch
    {
        DbpFile.Main => DbpFileKind.Main,
        DbpFile.Messages => DbpFileKind.Chats,
        DbpFile.Journal => DbpFileKind.Journal,
        _ => DbpFileKind.Unknown,
    };

    public bool Has(DbpFile file) => _files.ContainsKey(file);

    public IEnumerable<DbpFile> Files => _files.Keys;

    public DbpSqliteFile File(DbpFile file) =>
        _files.TryGetValue(file, out var opened)
            ? opened
            : throw new MigrationException($"Файл не выбран: {Title(file)}.");

    public string Name(DbpFile file) => File(file).Name;

    /// <summary>Открывает выбранные файлы. Основной обязателен, чаты и журнал - по выбору.</summary>
    public static DbpFileSet Open(string importDirectory, FileSourceOptions source)
    {
        var set = new DbpFileSet();
        try
        {
            set.OpenFile(DbpFile.Main, importDirectory, source.MainFile);
            if (source.HasChats)
                set.OpenFile(DbpFile.Messages, importDirectory, source.ChatsFile);
            if (source.HasJournal)
                set.OpenFile(DbpFile.Journal, importDirectory, source.JournalFile);
            return set;
        }
        catch
        {
            set.Dispose();
            throw;
        }
    }

    private void OpenFile(DbpFile role, string importDirectory, string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new MigrationException($"Не выбран файл: {Title(role)}.");
        _files[role] = DbpSqliteFile.Open(ImportFiles.Resolve(importDirectory, path));
    }

    public Guid DatabaseId(DbpFile file)
    {
        var opened = File(file);
        return opened.DatabaseId
               ?? throw new MigrationException($"В файле {opened.Name} нет идентификатора базы (таблица databaseid пуста).");
    }

    public IReadOnlyList<string> Tables(DbpFile file) => File(file).Tables;

    public IReadOnlyList<string> Columns(DbpFile file, string table) => File(file).Columns(table);

    public long Count(DbpFile file, string table) => File(file).Count(table);

    public SqliteCommand Select(DbpFile file, string table, IEnumerable<string> columns) => File(file).Select(table, columns);

    /// <summary>Файловые архивы базы: их Id стоят в famediator.</summary>
    public List<DbpArchive> Faslist()
    {
        var main = File(DbpFile.Main);
        var result = new List<DbpArchive>();
        // mask есть в старых версиях файловой базы и нет в PostgreSQL - читаем, только если колонка на месте.
        var hasMask = main.Columns("faslist").Contains("mask", StringComparer.Ordinal);
        using var command = main.Command($"select fa_id, name, is_writable, {(hasMask ? "mask" : "''")} from faslist");
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var id = DbpValue.ToGuid(reader.GetValue(0));
            if (id is null)
                continue;
            result.Add(new DbpArchive(
                id.Value,
                reader.IsDBNull(1) ? "" : reader.GetString(1),
                DbpValue.ToBool(reader.GetValue(2)) ?? false,
                reader.IsDBNull(3) ? "" : reader.GetValue(3).ToString() ?? ""));
        }
        return result;
    }

    /// <summary>Id тел файлов и архив, в котором они лежат (таблица famediator).</summary>
    public List<(Guid FileId, Guid Archive)> FileMap()
    {
        var result = new List<(Guid, Guid)>();
        using var command = File(DbpFile.Main).Select("famediator", new[] { "file_id", "fa_id" });
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var fileId = DbpValue.ToGuid(reader.GetValue(0));
            var archive = DbpValue.ToGuid(reader.GetValue(1));
            if (fileId is not null && archive is not null)
                result.Add((fileId.Value, archive.Value));
        }
        return result;
    }

    public void Dispose()
    {
        foreach (var file in _files.Values)
            file.Dispose();
        _files.Clear();
    }
}
