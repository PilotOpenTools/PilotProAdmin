using System.Data.Common;
using Microsoft.Data.Sqlite;

namespace PilotMigration.Database;

/// <summary>
/// Один файл выгрузки - база SQLite той же структуры, что файл базы Pilot при файловом хранении:
/// таблицы и колонки из <see cref="DbpToPgMap"/>, значения в том виде, в каком их читает импорт.
/// Пишется без журнала SQLite: недописанный файл всё равно лежит в папке «.partial» и выбрасывается.
/// </summary>
public sealed class DbpWriter : IDisposable
{
    /// <summary>Строк в одной транзакции SQLite: большие таблицы не держат весь журнал в памяти.</summary>
    private const int BatchRows = 50_000;

    private readonly SqliteConnection _connection;

    private DbpWriter(string path, DbpFile role, SqliteConnection connection)
    {
        Path = path;
        Role = role;
        _connection = connection;
    }

    public string Path { get; }

    public DbpFile Role { get; }

    /// <summary>
    /// Создаёт файл с таблицами роли и таблицей databaseid (по ней импорт проверяет, что файлы сняты с одной базы).
    /// <paramref name="databaseId"/> - строка databaseid источника: колонка / значение.
    /// </summary>
    public static DbpWriter Create(string path, DbpFile role, IReadOnlyDictionary<string, object?> databaseId)
    {
        if (File.Exists(path))
            throw new MigrationException($"Файл {path} уже существует.");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString());
        connection.Open();
        var writer = new DbpWriter(path, role, connection);
        try
        {
            writer.Execute("PRAGMA journal_mode = OFF");
            writer.Execute("PRAGMA synchronous = OFF");
            writer.CreateDatabaseId(databaseId);
            foreach (var table in DbpToPgMap.Tables.Where(t => t.File == role))
            {
                var columns = table.Columns.Select(c => $"{DbpSqliteFile.Quote(c.Sqlite)} {DbpToPgMap.SqliteType(c.Kind)}");
                writer.Execute($"create table {DbpSqliteFile.Quote(table.Sqlite)} ({string.Join(", ", columns)})");
            }
            return writer;
        }
        catch
        {
            writer.Dispose();
            throw;
        }
    }

    private void CreateDatabaseId(IReadOnlyDictionary<string, object?> row)
    {
        var columns = row.Keys.ToList();
        if (!columns.Contains("id", StringComparer.Ordinal) || !columns.Contains("data", StringComparer.Ordinal))
            throw new MigrationException("В таблице databaseid источника нет колонок id и data.");
        Execute($"create table [databaseid] ({string.Join(", ", columns.Select(c => c switch
        {
            "id" => "[id] INTEGER PRIMARY KEY",
            "data" or "session_id" => $"{DbpSqliteFile.Quote(c)} GUID",
            _ => DbpSqliteFile.Quote(c),
        }))})");
        using var insert = _connection.CreateCommand();
        insert.CommandText = $"insert into [databaseid] ({string.Join(", ", columns.Select(DbpSqliteFile.Quote))}) " +
                             $"values ({string.Join(", ", columns.Select((_, i) => "$p" + i))})";
        for (var i = 0; i < columns.Count; i++)
            insert.Parameters.AddWithValue("$p" + i, DbpValue.ToSqliteAny(row[columns[i]]));
        insert.ExecuteNonQuery();
    }

    /// <summary>
    /// Пишет строки таблицы. Колонки <paramref name="reader"/> - в порядке <see cref="DbpTable.Columns"/>
    /// (так их выбирает <see cref="SelectSql"/>).
    /// </summary>
    public async Task<long> WriteTableAsync(DbpTable table, DbDataReader reader, Action<long>? progress, CancellationToken ct)
    {
        if (table.File != Role)
            throw new ArgumentException($"Таблица {table.Pg} относится к другому файлу.", nameof(table));

        var tx = _connection.BeginTransaction();
        using var insert = _connection.CreateCommand();
        insert.Transaction = tx;
        insert.CommandText = $"insert into {DbpSqliteFile.Quote(table.Sqlite)} " +
                             $"({string.Join(", ", table.Columns.Select(c => DbpSqliteFile.Quote(c.Sqlite)))}) " +
                             $"values ({string.Join(", ", table.Columns.Select((_, i) => "$p" + i))})";
        // Тип параметра выводится из значения: byte[] / BLOB, long / INTEGER, string / TEXT, как у Pilot.
        var parameters = table.Columns.Select((_, i) => insert.Parameters.Add(new SqliteParameter { ParameterName = "$p" + i })).ToArray();

        long rows = 0;
        try
        {
            while (await reader.ReadAsync(ct))
            {
                for (var i = 0; i < table.Columns.Count; i++)
                {
                    var column = table.Columns[i];
                    var raw = reader.IsDBNull(i) ? null : reader.GetValue(i);
                    parameters[i].Value = DbpValue.ToSqlite(column.Kind, raw, table.Pg, column.Pg) ?? DBNull.Value;
                }
                insert.ExecuteNonQuery();
                if (++rows % BatchRows == 0)
                {
                    tx.Commit();
                    tx.Dispose();
                    tx = _connection.BeginTransaction();
                    insert.Transaction = tx;
                    progress?.Invoke(rows);
                }
            }
            tx.Commit();
            return rows;
        }
        finally
        {
            tx.Dispose();
        }
    }

    /// <summary>
    /// faslist - как архивы в settings.xml источника (имена, если запись уже есть, остаются прежними);
    /// файлы из архивов, которых там нет, относятся к записываемому архиву. Возвращает число переназначенных файлов.
    /// </summary>
    public int ReconcileArchives(IReadOnlyList<(Guid Id, string DefaultName, bool IsWritable)> archives)
    {
        if (Role != DbpFile.Main)
            throw new InvalidOperationException("Архивы есть только в основном файле.");
        if (archives.Count == 0)
            return 0;
        var ids = string.Join(", ", archives.Select((_, i) => "$a" + i));
        void AddIds(SqliteCommand command)
        {
            for (var i = 0; i < archives.Count; i++)
                command.Parameters.AddWithValue("$a" + i, archives[i].Id.ToByteArray());
        }

        var remapped = 0;
        if (archives.FirstOrDefault(a => a.IsWritable) is { Id: var writable } && writable != Guid.Empty)
        {
            using var update = _connection.CreateCommand();
            update.CommandText = $"update [famediator] set fa_id = $w where fa_id is null or fa_id not in ({ids})";
            update.Parameters.AddWithValue("$w", writable.ToByteArray());
            AddIds(update);
            remapped = update.ExecuteNonQuery();
        }

        using (var delete = _connection.CreateCommand())
        {
            delete.CommandText = $"delete from [faslist] where fa_id is null or fa_id not in ({ids})";
            AddIds(delete);
            delete.ExecuteNonQuery();
        }
        foreach (var archive in archives)
        {
            using var update = _connection.CreateCommand();
            update.CommandText = "update [faslist] set is_writable = $w where fa_id = $id";
            update.Parameters.AddWithValue("$w", archive.IsWritable ? 1L : 0L);
            update.Parameters.AddWithValue("$id", archive.Id.ToByteArray());
            if (update.ExecuteNonQuery() > 0)
                continue;
            using var insert = _connection.CreateCommand();
            insert.CommandText = "insert into [faslist] (fa_id, name, creation_date, is_writable) values ($id, $n, $d, $w)";
            insert.Parameters.AddWithValue("$id", archive.Id.ToByteArray());
            insert.Parameters.AddWithValue("$n", archive.DefaultName);
            insert.Parameters.AddWithValue("$d", DateTime.UtcNow.Ticks);
            insert.Parameters.AddWithValue("$w", archive.IsWritable ? 1L : 0L);
            insert.ExecuteNonQuery();
        }
        return remapped;
    }

    /// <summary>Запрос строк таблицы источника - колонки в порядке карты.</summary>
    public static string SelectSql(PilotPgSchema schema, DbpTable table) =>
        $"select {table.PgColumnList} from {schema.Q(table.Pg)}";

    private void Execute(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    public void Dispose() => _connection.Dispose();
}
