using Npgsql;
using PilotMigration.Config;

namespace PilotMigration.Database;

/// <summary>Таблица схемы Pilot в PostgreSQL.</summary>
public sealed record PgTable(string Name, IReadOnlyList<string> Columns, IReadOnlyList<string> IdentityColumns)
{
    public string ColumnList => string.Join(", ", Columns.Select(PgNames.Quote));
}

public sealed record PgForeignKey(string Table, string Column, string RefTable, string RefColumn);

/// <summary>Схема базы Pilot (<c>dbp&lt;версия сервера&gt;</c>): таблицы, колонки, внешние ключи.</summary>
public sealed class PilotPgSchema
{
    public required string Schema { get; init; }
    public required IReadOnlyDictionary<string, PgTable> Tables { get; init; }
    public required IReadOnlyList<PgForeignKey> ForeignKeys { get; init; }

    public string Q(string table) => $"{PgNames.Quote(Schema)}.{PgNames.Quote(table)}";

    public bool Has(string table) => Tables.ContainsKey(table);

    /// <summary>Таблицы в порядке загрузки: сначала те, на которые ссылаются.</summary>
    public IReadOnlyList<PgTable> LoadOrder(IEnumerable<string> names)
    {
        var set = names.Where(Tables.ContainsKey).ToHashSet();
        var result = new List<PgTable>();
        var done = new HashSet<string>();
        void Visit(string table, HashSet<string> path)
        {
            if (done.Contains(table) || !path.Add(table))
                return;
            foreach (var fk in ForeignKeys.Where(f => f.Table == table && f.RefTable != table && set.Contains(f.RefTable)))
                Visit(fk.RefTable, path);
            path.Remove(table);
            if (done.Add(table))
                result.Add(Tables[table]);
        }
        foreach (var name in set.OrderBy(n => n, StringComparer.Ordinal))
            Visit(name, new HashSet<string>());
        return result;
    }

    /// <summary>Внешние ключи, ссылающиеся на таблицу.</summary>
    public IEnumerable<PgForeignKey> ReferencesTo(string table) => ForeignKeys.Where(f => f.RefTable == table);

    public static async Task<PilotPgSchema> LoadAsync(NpgsqlConnection connection, CancellationToken ct)
    {
        var schemas = new List<string>();
        await using (var cmd = new NpgsqlCommand(
                         "select n.nspname from pg_namespace n where n.nspname like 'dbp%' " +
                         "and exists (select 1 from pg_tables t where t.schemaname = n.nspname and t.tablename = 'objects')", connection))
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
                schemas.Add(reader.GetString(0));
        }
        if (schemas.Count == 0)
            throw new MigrationException($"В базе PostgreSQL «{connection.Database}» нет схемы Pilot (dbp…). Это точно база Pilot?");

        // После обновления сервера может остаться старая схема - берётся самая новая версия.
        var schema = schemas.OrderByDescending(SchemaVersion).First();

        var columns = new Dictionary<string, List<(string Column, bool Identity)>>(StringComparer.Ordinal);
        await using (var cmd = new NpgsqlCommand(
                         "select c.relname, a.attname, a.attidentity <> '' " +
                         "from pg_attribute a join pg_class c on c.oid = a.attrelid join pg_namespace n on n.oid = c.relnamespace " +
                         "where n.nspname = @s and c.relkind = 'r' and a.attnum > 0 and not a.attisdropped order by c.relname, a.attnum", connection))
        {
            cmd.Parameters.AddWithValue("s", schema);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var table = reader.GetString(0);
                if (!columns.TryGetValue(table, out var list))
                    columns[table] = list = new();
                list.Add((reader.GetString(1), reader.GetBoolean(2)));
            }
        }

        var fks = new List<PgForeignKey>();
        await using (var cmd = new NpgsqlCommand(
                         "select c.relname, a.attname, rc.relname, ra.attname " +
                         "from pg_constraint k " +
                         "join pg_class c on c.oid = k.conrelid join pg_namespace n on n.oid = c.relnamespace " +
                         "join pg_class rc on rc.oid = k.confrelid " +
                         "join pg_attribute a on a.attrelid = k.conrelid and a.attnum = k.conkey[1] " +
                         "join pg_attribute ra on ra.attrelid = k.confrelid and ra.attnum = k.confkey[1] " +
                         "where k.contype = 'f' and n.nspname = @s", connection))
        {
            cmd.Parameters.AddWithValue("s", schema);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                fks.Add(new PgForeignKey(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
        }

        return new PilotPgSchema
        {
            Schema = schema,
            Tables = columns.ToDictionary(
                kv => kv.Key,
                kv => new PgTable(kv.Key, kv.Value.Select(c => c.Column).ToList(), kv.Value.Where(c => c.Identity).Select(c => c.Column).ToList()),
                StringComparer.Ordinal),
            ForeignKeys = fks,
        };
    }

    /// <summary>Различия в структуре таблиц (для проверки, что серверы одной версии).</summary>
    public IEnumerable<string> DifferencesFrom(PilotPgSchema other)
    {
        if (Schema != other.Schema)
            yield return $"схема «{Schema}» ≠ «{other.Schema}» (разные версии Pilot-Server)";
        foreach (var t in Tables.Values)
        {
            if (!other.Tables.TryGetValue(t.Name, out var o))
                yield return $"таблицы {t.Name} нет во второй базе";
            else if (!t.Columns.SequenceEqual(o.Columns))
                yield return $"колонки таблицы {t.Name} различаются";
        }
        foreach (var o in other.Tables.Keys.Where(k => !Tables.ContainsKey(k)))
            yield return $"таблицы {o} нет в первой базе";
    }

    private static Version SchemaVersion(string schema) =>
        Version.TryParse(schema[3..], out var v) ? v : new Version(0, 0);
}

public static class PgNames
{
    public static string Quote(string name) => "\"" + name.Replace("\"", "\"\"") + "\"";
}

/// <summary>Подключения к PostgreSQL базы Pilot одного контура.</summary>
public static class PgConnections
{
    /// <summary>
    /// Строка подключения сервиса к базе контура целиком из строки подключения сервера Pilot
    /// (settings.xml или админский API). Пароль - из строки, а если его там нет, из окружения сервиса
    /// (<paramref name="fallbackPassword"/>); в кластере без пароля подключиться нечем, это ошибка с именем переменной.
    /// <paramref name="database"/> подменяет имя базы (база конфигурации кластера на том же сервере).
    /// </summary>
    public static string Build(EnvConfig env, string? pilotConnectionString, string? fallbackPassword = null, bool cluster = false, string? database = null)
    {
        NpgsqlConnectionStringBuilder? pilot = null;
        if (!string.IsNullOrWhiteSpace(pilotConnectionString))
        {
            try { pilot = new NpgsqlConnectionStringBuilder(pilotConnectionString); }
            catch (Exception ex) when (ex is ArgumentException or KeyNotFoundException) { pilot = null; }
        }

        database ??= pilot?.Database;
        if (pilot is null || string.IsNullOrEmpty(database))
            throw new MigrationException($"Неизвестно имя базы PostgreSQL контура «{env.Title}»: сервер Pilot его не сообщил.");

        var host = pilot.Host ?? throw new MigrationException($"Не задан хост PostgreSQL контура «{env.Title}».");
        (host, var port) = SplitHostPort(host, pilot.Port > 0 ? pilot.Port : 5432);

        var user = pilot.Username;
        var password = string.IsNullOrEmpty(pilot.Password) ? fallbackPassword : pilot.Password;
        if (string.IsNullOrEmpty(user))
            throw new MigrationException($"Не задан пользователь PostgreSQL контура «{env.Title}»: сервер Pilot его не сообщил.");
        if (cluster && string.IsNullOrEmpty(password))
            throw new MigrationException($"Контур «{env.Title}» - кластер: пароль PostgreSQL в его настройках зашифрован, а сервер Pilot его не отдаёт. " +
                                         $"Задайте пароль роли {user} переменной {ServiceOptions.PgPasswordVariable(env.Key)} сервиса миграции " +
                                         $"(в compose - {env.Key.ToUpperInvariant()}_PILOT_PG_PASSWORD из .env) и пересоздайте контейнер pilot-migration.");

        return new NpgsqlConnectionStringBuilder
        {
            Host = host,
            Port = port,
            Username = user,
            Password = password,
            Database = database,
            Pooling = false,
            Timeout = 15,
            CommandTimeout = 0,
            KeepAlive = 30,
            ApplicationName = "pilot-migration",
        }.ConnectionString;
    }

    /// <summary>Pilot пишет host=postgres-test:5432 - порт внутри хоста.</summary>
    private static (string Host, int Port) SplitHostPort(string host, int port)
    {
        var colon = host.LastIndexOf(':');
        return colon > 0 && int.TryParse(host[(colon + 1)..], out var p) ? (host[..colon], p) : (host, port);
    }

    /// <summary>Хост, порт и база строки подключения; null - строку не разобрать или хоста или базы в ней нет.</summary>
    internal static (string Host, int Port, string Database)? Endpoint(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            return null;
        NpgsqlConnectionStringBuilder b;
        try { b = new NpgsqlConnectionStringBuilder(connectionString); }
        catch (Exception ex) when (ex is ArgumentException or KeyNotFoundException) { return null; }
        if (string.IsNullOrEmpty(b.Host) || string.IsNullOrEmpty(b.Database))
            return null;
        var (host, port) = SplitHostPort(b.Host, b.Port);
        return (host, port, b.Database);
    }

    public static string Describe((string Host, int Port, string Database) e) => $"{e.Host}:{e.Port}/{e.Database}";

    /// <summary>Одна и та же база PostgreSQL: хост (без учёта регистра), порт и имя базы. Если строку не разобрать - не совпадают.</summary>
    internal static bool SameDatabase(string? a, string? b) =>
        Endpoint(a) is { } x && Endpoint(b) is { } y &&
        string.Equals(x.Host, y.Host, StringComparison.OrdinalIgnoreCase) && x.Port == y.Port && string.Equals(x.Database, y.Database, StringComparison.Ordinal);

    public static async Task<NpgsqlConnection> OpenAsync(string connectionString, string envTitle, CancellationToken ct)
    {
        var connection = new NpgsqlConnection(connectionString);
        try
        {
            await connection.OpenAsync(ct);
            return connection;
        }
        catch (Exception ex) when (ex is NpgsqlException or System.Net.Sockets.SocketException or TimeoutException)
        {
            await connection.DisposeAsync();
            var b = new NpgsqlConnectionStringBuilder(connectionString);
            throw new MigrationException($"PostgreSQL «{envTitle}» ({b.Host}:{b.Port}/{b.Database}, пользователь {b.Username}): {ex.Message}", ex);
        }
    }
}
