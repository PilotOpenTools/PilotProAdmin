using Microsoft.Data.Sqlite;
using PilotMigration.Config;
using PilotMigration.Database;

namespace PilotMigration.Tests;

/// <summary>
/// Импорт присланной файловой базы: карта таблиц сверяется со снимком настоящей схемы Pilot в PostgreSQL
/// (<see cref="PgSchemaSnapshot"/> - колонки схемы dbp26.13.0.62219 теста), а преобразования значений -
/// с тем, как данные лежат в .dbp. Файлы выбираются по отдельности, поэтому имена здесь - произвольные.
/// </summary>
public class DbpImportTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "dbp-test-" + Guid.NewGuid().ToString("N"));
    private readonly Guid _databaseId = Guid.NewGuid();

    /// <summary>Имена, как у присланной выгрузки: папка и названия могут быть любыми.</summary>
    private const string MainName = "выгрузка ГП/base.dbp";
    private const string ChatsName = "выгрузка ГП/чаты.dbp";
    private const string JournalName = "журнал/base.journal.dbp";

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }

    private DbpFileSet OpenAll() => DbpFileSet.Open(_folder, new FileSourceOptions
    {
        MainFile = MainName,
        ChatsFile = ChatsName,
        JournalFile = JournalName,
    });

    [Fact]
    public void MapMatchesPilotPgSchema()
    {
        CreateFiles();
        using var files = OpenAll();
        Assert.Empty(DbpToPgMap.Differences(PgSchemaSnapshot.Load(), files));
    }

    [Fact]
    public void ReadsDatabaseIdFromEveryFile()
    {
        CreateFiles();
        using var files = OpenAll();
        Assert.Equal(_databaseId, files.DatabaseId(DbpFile.Main));
        Assert.Equal(_databaseId, files.DatabaseId(DbpFile.Messages));
        Assert.Equal(_databaseId, files.DatabaseId(DbpFile.Journal));
    }

    [Fact]
    public void ChatsAndJournalAreOptional()
    {
        CreateFiles();
        using var files = DbpFileSet.Open(_folder, new FileSourceOptions { MainFile = MainName });
        Assert.Empty(DbpToPgMap.Differences(PgSchemaSnapshot.Load(), files));
        Assert.False(files.Has(DbpFile.Messages));
        Assert.False(files.Has(DbpFile.Journal));
    }

    /// <summary>Роль файла определяется по набору таблиц: имя ничего не гарантирует.</summary>
    [Fact]
    public void KindComesFromTablesNotName()
    {
        CreateFiles();
        using var files = OpenAll();
        Assert.Equal(DbpFileKind.Main, files.File(DbpFile.Main).Kind);
        Assert.Equal(DbpFileKind.Chats, files.File(DbpFile.Messages).Kind);
        Assert.Equal(DbpFileKind.Journal, files.File(DbpFile.Journal).Kind);
        Assert.Equal(DbpFileKind.Chats, ImportFiles.List(_folder).Single(f => f.Path == ChatsName).Kind);
    }

    [Fact]
    public void ListsEveryDbpFileWithItsKind()
    {
        CreateFiles();
        var listed = ImportFiles.List(_folder);
        Assert.Equal(new[] { MainName, ChatsName, JournalName }.OrderBy(n => n, StringComparer.Ordinal),
            listed.Select(f => f.Path).OrderBy(n => n, StringComparer.Ordinal));
        Assert.All(listed, f => Assert.Equal(_databaseId, f.DatabaseId));
        Assert.All(listed, f => Assert.Null(f.Problem));
    }

    [Fact]
    public void MissingFileIsReported()
    {
        CreateFiles();
        File.Delete(Path.Combine(_folder, ChatsName));
        var error = Assert.Throws<MigrationException>(() => OpenAll());
        Assert.Contains("чаты.dbp", error.Message);
    }

    [Fact]
    public void PathOutsideImportDirectoryIsRefused()
    {
        CreateFiles();
        var error = Assert.Throws<MigrationException>(() =>
            DbpFileSet.Open(_folder, new FileSourceOptions { MainFile = "../за-пределами.dbp" }));
        Assert.Contains("вне каталога импорта", error.Message);
    }

    [Fact]
    public void ColumnMissingInTargetIsReported()
    {
        CreateFiles();
        using var files = OpenAll();
        var schema = PgSchemaSnapshot.Load(without: ("objects", "last_change"));
        var diff = DbpToPgMap.Differences(schema, files);
        Assert.Contains(diff, d => d.Contains("objects") && d.Contains("last_change"));
    }

    [Fact]
    public void UnknownTableInFileIsReported()
    {
        CreateFiles();
        Execute(MainName, "create table [unexpected_in_26_14] ([id] GUID)");
        using var files = OpenAll();
        var diff = DbpToPgMap.Differences(PgSchemaSnapshot.Load(), files);
        Assert.Contains(diff, d => d.Contains("unexpected_in_26_14"));
    }

    /// <summary>Таблица files (80 строк в базе ГП) в PostgreSQL отсутствует и отбрасывается сознательно.</summary>
    [Fact]
    public void DroppedTablesDoNotBlockImport()
    {
        CreateFiles();
        Execute(MainName, "create table [files] ([id] GUID PRIMARY KEY, [data] BLOB)");
        Execute(MainName, "alter table [faslist] add column [mask] STRING");
        using var files = OpenAll();
        Assert.Empty(DbpToPgMap.Differences(PgSchemaSnapshot.Load(), files));
    }

    [Fact]
    public void ReadsRowsAndFaslist()
    {
        CreateFiles();
        var archive = Guid.NewGuid();
        var body = Guid.NewGuid();
        Execute(MainName, "insert into [faslist] (fa_id, name, creation_date, is_writable) values ($id, 'Файловый архив 1', 638113616097948006, 'true')",
            ("$id", archive.ToByteArray()));
        Execute(MainName, "insert into [famediator] (file_id, object_id, fa_id) values ($f, $o, $a)",
            ("$f", body.ToByteArray()), ("$o", Guid.NewGuid().ToByteArray()), ("$a", archive.ToByteArray()));

        using var files = DbpFileSet.Open(_folder, new FileSourceOptions { MainFile = MainName });
        var faslist = Assert.Single(files.Faslist());
        Assert.Equal(archive, faslist.Id);
        Assert.Equal("Файловый архив 1", faslist.Name);
        Assert.True(faslist.IsWritable);
        Assert.Equal(1, files.Count(DbpFile.Main, "famediator"));
        Assert.Equal((body, archive), Assert.Single(files.FileMap()));
    }

    [Fact]
    public void GuidKeepsDotNetByteLayout()
    {
        // Так лежит fa_id архива базы ГП: 16 байт в раскладке Guid.ToByteArray().
        var bytes = Convert.FromHexString("F7432F0E864C1B499153FF88CD8766BF");
        Assert.Equal(Guid.Parse("0e2f43f7-4c86-491b-9153-ff88cd8766bf"), DbpValue.ToGuid(bytes));
    }

    [Theory]
    [InlineData("2015-11-07 14:38:29.120443Z", "2015-11-07T14:38:29.1204430Z")]
    [InlineData("0001-01-01 00:00:00Z", "0001-01-01T00:00:00.0000000Z")]
    public void TimestampTextIsReadAsUtc(string stored, string expected)
    {
        var value = Assert.IsType<DateTime>(DbpValue.Convert(DbpKind.Timestamp, stored, "history_items", "created"));
        Assert.Equal(DateTimeKind.Utc, value.Kind);
        Assert.Equal(DateTime.Parse(expected, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal).Ticks, value.Ticks);
    }

    [Fact]
    public void FaslistCreationDateIsTicks()
    {
        var value = Assert.IsType<DateTime>(DbpValue.Convert(DbpKind.TimestampTicks, 638113616097948006L, "faslist", "creation_date"));
        Assert.Equal(new DateTime(638113616097948006, DateTimeKind.Utc), value);
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    [InlineData(1L, true)]
    [InlineData(0L, false)]
    public void BoolComesAsTextOrNumber(object stored, bool expected) =>
        Assert.Equal(expected, DbpValue.Convert(DbpKind.Bool, stored, "faslist", "is_writable"));

    [Fact]
    public void NullStaysNull() =>
        Assert.Null(DbpValue.Convert(DbpKind.Bytes, null, "objects", "data"));

    [Fact]
    public void UnconvertibleValueNamesTableAndColumn()
    {
        var error = Assert.Throws<MigrationException>(() => DbpValue.Convert(DbpKind.Uuid, "не guid", "objects", "id"));
        Assert.Contains("objects.id", error.Message);
    }

    // Создание присланной базы: те же имена таблиц и колонок, что ждёт карта импорта,
    // и те же классы хранения SQLite, что в настоящей базе.
    private void CreateFiles()
    {
        foreach (var (role, name) in new[] { (DbpFile.Main, MainName), (DbpFile.Messages, ChatsName), (DbpFile.Journal, JournalName) })
        {
            using var connection = Open(name);
            Run(connection, "create table [databaseid] ([id] INTEGER PRIMARY KEY, [data] GUID, session_id GUID)");
            using var insert = connection.CreateCommand();
            insert.CommandText = "insert into [databaseid] (id, data, session_id) values (0, $d, $s)";
            insert.Parameters.AddWithValue("$d", _databaseId.ToByteArray());
            insert.Parameters.AddWithValue("$s", Guid.NewGuid().ToByteArray());
            insert.ExecuteNonQuery();

            foreach (var table in DbpToPgMap.Tables.Where(t => t.File == role))
            {
                var columns = table.Columns.Select(c => $"[{c.Sqlite}] {StorageType(c.Kind)}");
                Run(connection, $"create table [{table.Sqlite}] ({string.Join(", ", columns)})");
            }
        }
    }

    private static string StorageType(DbpKind kind) => kind switch
    {
        DbpKind.Bytes => "BLOB",
        DbpKind.Uuid => "GUID",
        DbpKind.Int2 or DbpKind.Int4 or DbpKind.Int8 or DbpKind.TimestampTicks => "INTEGER",
        DbpKind.Text => "TEXT",
        DbpKind.Bool => "BOOLEAN",
        DbpKind.Timestamp => "DATETIME",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private SqliteConnection Open(string name)
    {
        var path = Path.Combine(_folder, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        return connection;
    }

    private void Execute(string file, string sql, params (string Name, object Value)[] parameters)
    {
        using var connection = Open(file);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value);
        command.ExecuteNonQuery();
    }

    private static void Run(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
