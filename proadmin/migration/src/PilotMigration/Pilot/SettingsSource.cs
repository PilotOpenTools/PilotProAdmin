using System.Xml;
using System.Xml.Linq;
using Ascon.Pilot.DataClasses;
using Npgsql;
using PilotMigration.Config;
using PilotMigration.Database;
using PilotMigration.Engine;

namespace PilotMigration.Pilot;

/// <summary>
/// Откуда сервис берёт «настройки сервера» контура (архивы, строка PostgreSQL, параметры поведения). Монолит - settings.xml
/// по пути из подключений. Кластер Pilot-Server (у базы задан Redis) - XML из таблицы config.settings базы конфигурации
/// на том же сервере PostgreSQL: settings.xml там нет, а путь к нему от прежней установки может остаться и быть устаревшим.
/// </summary>
public static class SettingsSource
{
    /// <summary>Признак кластера: админский API отдаёт строку Redis только кластерным базам (у монолита она пуста).</summary>
    public static bool IsCluster(AdminDatabaseInfo db) => !string.IsNullOrWhiteSpace(db.RedisConnectionString);

    /// <summary>
    /// Settings.xml по пути из подключений - не тот сервер (путь остался от прежней установки: базу перенесли, а файл нет).
    /// Иначе миграция тихо читала бы и писала в старую базу PostgreSQL и переписывала бы faslist по чужим архивам.
    /// Сверяются идентификатор базы, множество файловых архивов и хост:порт/база PostgreSQL (если строка есть и там и там).
    /// </summary>
    internal static string? StaleError(EnvConfig env, ServerDatabaseSettings file, AdminDatabaseInfo db)
    {
        var differences = new List<string>();
        if (file.Id != Guid.Empty && db.Id != Guid.Empty && file.Id != db.Id)
            differences.Add($"идентификатор базы {file.Id} в файле, {db.Id} на сервере");
        var apiArchives = (db.FileArchives ?? new()).Select(a => a.Id).ToHashSet();
        if (!file.Archives.Select(a => a.Id).ToHashSet().SetEquals(apiArchives))
            differences.Add($"файловых архивов {file.Archives.Count} в файле, {apiArchives.Count} на сервере (или у них другие Id)");
        if (PgConnections.Endpoint(file.PgConnectionString) is { } f && PgConnections.Endpoint(db.PostgresConnectionString) is { } a &&
            !PgConnections.SameDatabase(file.PgConnectionString, db.PostgresConnectionString))
            differences.Add($"PostgreSQL {PgConnections.Describe(f)} в файле, {PgConnections.Describe(a)} на сервере");
        return differences.Count == 0
            ? null
            : $"Контур «{env.Title}»: settings.xml не соответствует серверу: очистите путь или обновите файл ({env.SettingsXmlPath}). " +
              $"Расхождения: {string.Join("; ", differences)}.";
    }

    /// <summary>
    /// Настройки базы контура. Проблемы - в <paramref name="report"/> (блокирующие, если без них не обойтись: <paramref name="needArchives"/>).
    /// null - настройки не получены, тогда архивы и строка PostgreSQL берутся из админского API. Кластер читается из базы конфигурации
    /// только если нужны архивы или параметры поведения (<paramref name="needBehavior"/>): иначе незачем требовать пароль PostgreSQL.
    /// </summary>
    public static async Task<ServerDatabaseSettings?> ReadAsync(
        EnvConfig env, AdminDatabaseInfo db, ServiceOptions options, bool needArchives, bool needBehavior, Action<CheckLevel, string> report, CancellationToken ct)
    {
        var missing = needArchives ? CheckLevel.Blocker : CheckLevel.Warning;
        if (IsCluster(db))
        {
            if (!needArchives && !needBehavior)
                return null;
            if (!string.IsNullOrEmpty(env.SettingsXmlPath))
                report(CheckLevel.Warning, $"Контур «{env.Title}» работает кластером: путь к settings.xml не используется, настройки читаются из базы " +
                                           $"конфигурации {options.ConfigurationDatabase}. Очистите путь (Миграция / Подключения).");
            try
            {
                return await ReadClusterAsync(env, db, options, ct);
            }
            catch (MigrationException ex)
            {
                report(missing, ex.Message);
                return null;
            }
        }

        if (!string.IsNullOrEmpty(env.SettingsXmlPath))
        {
            ServerDatabaseSettings file;
            try
            {
                file = ServerSettingsXml.Read(env.SettingsXmlPath, db.Name);
            }
            catch (MigrationException ex)
            {
                report(missing, ex.Message);
                return null;
            }
            if (StaleError(env, file, db) is { } stale)
            {
                report(CheckLevel.Blocker, stale);
                return null;
            }
            return file;
        }

        // Без settings.xml - архивы из админского API (у S3 там нет адреса и бакета).
        var archives = db.FileArchives.Select(a => new ServerArchiveSettings(
            a.Id, a.FileArchiveType == FileArchiveTypeEnum.S3, a.IsWritable, a.Folder ?? "", null, null, null)).ToList();
        if (needArchives && archives.Any(a => a.IsS3))
            report(CheckLevel.Blocker, $"У базы «{db.Name}» ({env.Title}) есть S3-архив, а путь к settings.xml сервера не задан - без него неизвестны адрес и бакет S3.");
        return new ServerDatabaseSettings(db.Name, db.Id, null, archives);
    }

    private static async Task<ServerDatabaseSettings> ReadClusterAsync(EnvConfig env, AdminDatabaseInfo db, ServiceOptions options, CancellationToken ct)
    {
        var connectionString = PgConnections.Build(env, db.PostgresConnectionString, options.PgPassword(env.Key), cluster: true, database: options.ConfigurationDatabase);
        string? xml;
        try
        {
            await using var connection = await PgConnections.OpenAsync(connectionString, env.Title, ct);
            await using var cmd = new NpgsqlCommand("select data from config.settings where id = 1", connection);
            xml = await cmd.ExecuteScalarAsync(ct) as string;
        }
        catch (NpgsqlException ex)
        {
            throw new MigrationException($"Конфигурация кластера «{env.Title}»: таблица config.settings базы {options.ConfigurationDatabase} не прочитана: {ex.Message}", ex);
        }
        return ParseCluster(xml, db.Name, options.ConfigurationDatabase);
    }

    internal static ServerDatabaseSettings ParseCluster(string? xml, string database, string configurationDatabase)
    {
        var source = $"config.settings базы {configurationDatabase}";
        if (string.IsNullOrWhiteSpace(xml))
            throw new MigrationException($"В {source} нет конфигурации кластера (строка с id = 1 пуста или отсутствует).");
        XDocument doc;
        try
        {
            doc = XDocument.Parse(xml);
        }
        catch (XmlException ex)
        {
            throw new MigrationException($"Не удалось разобрать конфигурацию кластера из {source}: {ex.Message}", ex);
        }
        return ServerSettingsXml.Parse(doc, database, source);
    }
}
