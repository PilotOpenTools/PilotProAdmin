using System.Xml.Linq;

namespace PilotMigration.Pilot;

/// <summary>
/// Описание базы из settings.xml сервера Pilot (в кластере - из config.settings): файловые архивы, строка подключения к PostgreSQL
/// и параметры поведения сервера. Behavior - null, если настройки собраны из админского API: в нём их нет.
/// </summary>
public sealed record ServerDatabaseSettings(
    string Name,
    Guid Id,
    string? PgConnectionString,
    IReadOnlyList<ServerArchiveSettings> Archives,
    IReadOnlyDictionary<string, bool>? Behavior = null);

public sealed record ServerArchiveSettings(
    Guid Id,
    bool IsS3,
    bool IsWritable,
    string Folder,
    string? S3Url,
    string? S3Bucket,
    string? S3AccessKeyId);

public static class ServerSettingsXml
{
    public static ServerDatabaseSettings Read(string path, string database)
    {
        if (!File.Exists(path))
            throw new MigrationException($"Файл настроек сервера Pilot не найден: {path}. Смонтируйте папку settings сервера в контейнер миграции.");

        XDocument doc;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            doc = XDocument.Load(stream);
        }
        catch (Exception ex) when (ex is IOException or System.Xml.XmlException)
        {
            throw new MigrationException($"Не удалось прочитать {path}: {ex.Message}", ex);
        }

        return Parse(doc, database, path);
    }

    internal static ServerDatabaseSettings Parse(XDocument doc, string database, string source)
    {
        var db = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "DatabaseParameters"
                                                      && string.Equals((string?)e.Attribute("Name"), database, StringComparison.OrdinalIgnoreCase))
                 ?? throw new MigrationException($"В {source} нет базы «{database}».");

        var archives = new List<ServerArchiveSettings>();
        foreach (var fa in db.Descendants().Where(e => e.Name.LocalName is "FileArchiveParameters" or "FileArchiveParametersS3"))
        {
            if (!Guid.TryParse((string?)fa.Attribute("Id"), out var id))
                continue;
            var isS3 = fa.Name.LocalName == "FileArchiveParametersS3";
            archives.Add(new ServerArchiveSettings(
                id,
                isS3,
                string.Equals((string?)fa.Attribute("IsWritable"), "true", StringComparison.OrdinalIgnoreCase),
                (string?)fa.Attribute("Folder") ?? "",
                isS3 ? (string?)fa.Attribute("Url") : null,
                isS3 ? (string?)fa.Attribute("Bucket") : null,
                isS3 ? (string?)fa.Attribute("AccessKeyId") : null));
        }

        var pg = db.Descendants().FirstOrDefault(e => e.Name.LocalName == "PgConnectionString");
        Guid.TryParse((string?)db.Attribute("Id"), out var dbId);
        return new ServerDatabaseSettings((string?)db.Attribute("Name") ?? database, dbId, (string?)pg?.Attribute("ConnectionString"), archives,
            ServerSettingsCopy.ReadBehavior(doc));
    }
}
