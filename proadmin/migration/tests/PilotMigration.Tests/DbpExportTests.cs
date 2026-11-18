using System.Data;
using Microsoft.Data.Sqlite;
using PilotMigration.Config;
using PilotMigration.Database;

namespace PilotMigration.Tests;

/// <summary>
/// Выгрузка в файлы - обратный путь импорта: файлы, записанные <see cref="DbpWriter"/> из значений PostgreSQL,
/// должны пройти все проверки импорта и вернуть при чтении те же значения.
/// </summary>
public class DbpExportTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "dbp-export-" + Guid.NewGuid().ToString("N"));
    private readonly Guid _databaseId = Guid.NewGuid();

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void MapCoversWholePilotSchema() =>
        Assert.Empty(DbpToPgMap.ExportDifferences(PgSchemaSnapshot.Load()));

    /// <summary>Колонка, которой нет в карте, иначе молча не попала бы в выгрузку.</summary>
    [Fact]
    public void UnmappedColumnOrTableBlocksExport()
    {
        var schema = PgSchemaSnapshot.Load();
        var tables = schema.Tables.ToDictionary(t => t.Key, t => t.Value);
        tables["objects"] = tables["objects"] with { Columns = tables["objects"].Columns.Append("new_in_26_14").ToList() };
        tables["new_table"] = new PgTable("new_table", new[] { "id" }, Array.Empty<string>());
        var changed = new PilotPgSchema { Schema = schema.Schema, Tables = tables, ForeignKeys = schema.ForeignKeys };

        var diff = DbpToPgMap.ExportDifferences(changed);
        Assert.Contains(diff, d => d.Contains("objects.new_in_26_14"));
        Assert.Contains(diff, d => d.Contains("new_table"));
    }

    [Fact]
    public async Task ExportedFilesPassImportChecksAndRoundTripValues()
    {
        var rows = new Dictionary<string, object?[]>();
        foreach (var role in new[] { DbpFile.Main, DbpFile.Messages, DbpFile.Journal })
        {
            using var writer = DbpWriter.Create(Path.Combine(_folder, "export", ExportFiles.FileName(role)), role, DatabaseIdRow());
            foreach (var table in DbpToPgMap.Tables.Where(t => t.File == role))
            {
                var row = table.Columns.Select(c => SampleValue(c.Kind)).ToArray();
                // Пустые значения тоже должны доехать пустыми: вторая строка - все null.
                var data = Table(table, row, table.Columns.Select(_ => (object?)null).ToArray());
                using var reader = data.CreateDataReader();
                Assert.Equal(2, await writer.WriteTableAsync(table, reader, null, CancellationToken.None));
                rows[table.Pg] = row;
            }
        }

        using var files = DbpFileSet.Open(_folder, new FileSourceOptions
        {
            MainFile = "export/base.dbp",
            ChatsFile = "export/chats.dbp",
            JournalFile = "export/journal.dbp",
        });
        Assert.Empty(DbpToPgMap.Differences(PgSchemaSnapshot.Load(), files));
        Assert.Equal(DbpFileKind.Main, files.File(DbpFile.Main).Kind);
        Assert.Equal(DbpFileKind.Chats, files.File(DbpFile.Messages).Kind);
        Assert.Equal(DbpFileKind.Journal, files.File(DbpFile.Journal).Kind);
        foreach (var role in files.Files)
            Assert.Equal(_databaseId, files.DatabaseId(role));

        foreach (var table in DbpToPgMap.Tables)
        {
            using var command = files.Select(table.File, table.Sqlite, table.Columns.Select(c => c.Sqlite));
            using var reader = command.ExecuteReader();
            var read = new List<object?[]>();
            while (reader.Read())
            {
                read.Add(table.Columns.Select((c, i) =>
                    DbpValue.Convert(c.Kind, reader.IsDBNull(i) ? null : reader.GetValue(i), table.Sqlite, c.Sqlite)).ToArray());
            }
            Assert.Equal(2, read.Count);
            Assert.Equal(rows[table.Pg], read[0]);
            Assert.All(read[1], Assert.Null);
        }
    }

    /// <summary>faslist выгрузки - как архивы settings.xml; файлы из удалённых архивов уходят в записываемый.</summary>
    [Fact]
    public async Task ArchivesFollowSourceSettings()
    {
        var s3 = Guid.NewGuid();
        var removedFolder = Guid.NewGuid();
        var body = Guid.NewGuid();
        var path = Path.Combine(_folder, "base.dbp");
        using (var writer = DbpWriter.Create(path, DbpFile.Main, DatabaseIdRow()))
        {
            var faslist = DbpToPgMap.Tables.Single(t => t.Pg == "faslist");
            using (var reader = Table(faslist, new object?[] { removedFolder, "Файловый архив", DateTime.UtcNow, false }).CreateDataReader())
                await writer.WriteTableAsync(faslist, reader, null, CancellationToken.None);
            var famediator = DbpToPgMap.Tables.Single(t => t.Pg == "famediator");
            using (var reader = Table(famediator, new object?[] { body, Guid.NewGuid(), removedFolder }).CreateDataReader())
                await writer.WriteTableAsync(famediator, reader, null, CancellationToken.None);

            Assert.Equal(1, writer.ReconcileArchives(new[] { (s3, "S3 архив", true) }));
        }

        using var files = DbpFileSet.Open(_folder, new FileSourceOptions { MainFile = "base.dbp" });
        var archive = Assert.Single(files.Faslist());
        Assert.Equal(s3, archive.Id);
        Assert.True(archive.IsWritable);
        Assert.Equal((body, s3), Assert.Single(files.FileMap()));
    }

    [Theory]
    [InlineData("prod-20261101-1200", null)]
    [InlineData("бэкап прода", null)]
    [InlineData("a/b", "недопустимые")]
    [InlineData("..", "не годится")]
    [InlineData("x.partial", "не годится")]
    public void ExportNameIsAFolderName(string name, string? error)
    {
        var options = new FileTargetOptions { Name = name };
        options.Normalize("prod");
        if (error is null)
            Assert.Null(options.NameError());
        else
            Assert.Contains(error, options.NameError());
    }

    [Fact]
    public void EmptyNameGetsSourceAndDate()
    {
        var options = new FileTargetOptions();
        options.Normalize("prod");
        Assert.StartsWith("prod-", options.Name);
        Assert.Null(options.NameError());
    }

    private Dictionary<string, object?> DatabaseIdRow() => new()
    {
        ["id"] = 1,
        ["data"] = _databaseId,
        ["version"] = 3L,
        ["server_version"] = "26.13.0.62219",
        ["session_id"] = Guid.NewGuid(),
    };

    /// <summary>Значения тех типов, которые отдаёт Npgsql для колонок схемы Pilot.</summary>
    private static object SampleValue(DbpKind kind) => kind switch
    {
        DbpKind.Bytes => new byte[] { 1, 2, 3, 0, 255 },
        DbpKind.Uuid => Guid.NewGuid(),
        DbpKind.Int2 => (short)7,
        DbpKind.Int4 => 123456,
        DbpKind.Int8 => 9_000_000_000L,
        DbpKind.Text => "Текст «с кавычками»",
        DbpKind.Bool => true,
        // В PostgreSQL точность - микросекунды.
        DbpKind.Timestamp or DbpKind.TimestampTicks => new DateTime(2026, 11, 1, 12, 34, 56, DateTimeKind.Utc).AddTicks(1_234_560),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static DataTable Table(DbpTable table, params object?[][] rows)
    {
        var data = new DataTable();
        foreach (var column in table.Columns)
            data.Columns.Add(column.Pg, typeof(object));
        foreach (var row in rows)
            data.Rows.Add(row.Select(v => v ?? DBNull.Value).ToArray());
        return data;
    }
}
