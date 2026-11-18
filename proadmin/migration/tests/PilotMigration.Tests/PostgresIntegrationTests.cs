using System.Runtime.CompilerServices;
using Ascon.Pilot.DataClasses;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using PilotMigration.Archives;
using PilotMigration.Config;
using PilotMigration.Database;
using PilotMigration.Engine;
using PilotMigration.Pilot;

namespace PilotMigration.Tests;

/// <summary>
/// Тест на настоящем PostgreSQL 16. Адрес берётся из PILOT_IT_PG (строка подключения суперпользователя, например
/// <c>Host=127.0.0.1;Port=55432;Username=postgres;Password=…;Database=postgres</c>); без неё тест помечается пропущенным.
/// Тесту «разные кластеры» нужен ещё второй, независимый сервер - PILOT_IT_PG_OTHER в том же формате.
/// </summary>
public sealed class RequiresPostgresFactAttribute : FactAttribute
{
    public const string Variable = "PILOT_IT_PG";
    public const string OtherVariable = "PILOT_IT_PG_OTHER";

    public RequiresPostgresFactAttribute(bool needOtherCluster = false, [CallerFilePath] string? sourceFilePath = null, [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        var missing = new[] { Variable, OtherVariable }.Take(needOtherCluster ? 2 : 1)
            .Where(v => string.IsNullOrEmpty(Environment.GetEnvironmentVariable(v))).ToList();
        if (missing.Count > 0)
            Skip = "Нужны переменные окружения: " + string.Join(", ", missing) +
                   " (строка подключения суперпользователя к PostgreSQL 16, например Host=127.0.0.1;Port=55432;Username=postgres;Password=...;Database=postgres)";
    }
}

/// <summary>Две свежие базы со схемой Pilot на одном сервере; всё созданное удаляется в <see cref="DisposeAsync"/>.</summary>
internal sealed class PgPair : IAsyncDisposable
{
    public static readonly Guid SourceDbId = Guid.Parse("aaaaaaaa-1111-1111-1111-111111111111");
    public static readonly Guid TargetArchive = Guid.Parse("bbbbbbbb-2222-2222-2222-222222222222");
    public static readonly Guid SourceArchive = Guid.Parse("cccccccc-3333-3333-3333-333333333333");

    private readonly List<string> _databases = new();
    private readonly List<string> _roles = new();
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "pilot-migration-it-" + Guid.NewGuid().ToString("N"));

    public string Admin { get; } = Environment.GetEnvironmentVariable(RequiresPostgresFactAttribute.Variable)!;
    public string Source { get; private set; } = "";
    public string Target { get; private set; } = "";
    public PilotPgSchema Schema { get; } = PgSchemaSnapshot.Load();

    public static async Task<PgPair> CreateAsync(bool seed = true)
    {
        var pair = new PgPair();
        pair.Source = await pair.CreateDatabaseAsync("src");
        pair.Target = await pair.CreateDatabaseAsync("dst");
        foreach (var cs in new[] { pair.Source, pair.Target })
        {
            await using var c = await Open(cs);
            await Run(c, pair.Ddl());
        }
        if (seed)
        {
            await using (var c = await Open(pair.Source)) await Run(c, SourceData());
            await using (var c = await Open(pair.Target)) await Run(c, TargetData());
        }
        return pair;
    }

    public string For(string database) => new NpgsqlConnectionStringBuilder(Admin) { Database = database, Pooling = false }.ConnectionString;

    public async Task<string> CreateDatabaseAsync(string tag)
    {
        var name = $"mig_it_{Guid.NewGuid():N}_{tag}";
        await using var admin = await Open(Admin);
        await Run(admin, $"CREATE DATABASE \"{name}\"");
        _databases.Add(name);
        return For(name);
    }

    private const string ControlFunctions = "pg_control_system(), pg_control_checkpoint()";

    /// <summary>
    /// Роль без прав на pg_control_*: так пользователь сервиса выглядит без pg_monitor. В PostgreSQL 16 эти функции доступны всем,
    /// поэтому право отзывается у PUBLIC в созданных тестом базах (pg_proc у каждой базы свой) и уходит вместе с ними.
    /// </summary>
    public async Task<(string User, string Password)> CreateUnprivilegedRoleAsync()
    {
        var user = $"mig_it_{Guid.NewGuid():N}";
        var password = Guid.NewGuid().ToString("N");
        await using var admin = await Open(Admin);
        foreach (var cs in new[] { Source, Target })
        {
            await using var c = await Open(cs);
            await Run(c, $"REVOKE EXECUTE ON FUNCTION {ControlFunctions} FROM PUBLIC");
        }
        await Run(admin, $"CREATE ROLE \"{user}\" LOGIN PASSWORD '{password}'");
        _roles.Add(user);
        return (user, password);
    }

    public static async Task<NpgsqlConnection> Open(string connectionString)
    {
        var c = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString);
        await c.OpenAsync();
        return c;
    }

    public static async Task Run(NpgsqlConnection c, string sql, NpgsqlTransaction? tx = null)
    {
        await using var cmd = new NpgsqlCommand(sql, c, tx);
        await cmd.ExecuteNonQueryAsync();
    }

    public static async Task<T> Scalar<T>(NpgsqlConnection c, string sql, NpgsqlTransaction? tx = null)
    {
        await using var cmd = new NpgsqlCommand(sql, c, tx);
        return (T)(await cmd.ExecuteScalarAsync())!;
    }

    /// <summary>Отпечаток содержимого таблицы: равен у двух баз, если строки одинаковы (порядок не важен).</summary>
    public async Task<string> FingerprintAsync(string connectionString, string table)
    {
        await using var c = await Open(connectionString);
        return await Scalar<string>(c, $"select coalesce(md5(string_agg(t::text, '|' order by t::text)), '') from {Schema.Q(table)} t");
    }

    public async Task<long> CountAsync(string connectionString, string table)
    {
        await using var c = await Open(connectionString);
        return await Scalar<long>(c, $"select count(*) from {Schema.Q(table)}");
    }

    /// <summary>Журнал запуска, как в сервисе (на временной папке данных).</summary>
    public RunLog NewLog()
    {
        var options = new ServiceOptions { DataDirectory = _directory };
        var store = new RunStore(options, new ConfigStore(options), NullLogger<RunStore>.Instance);
        return new RunLog(new MigrationRun(), store);
    }

    /// <summary>Контекст полной копии: настоящие базы, архивов у источника нет (таблица famediator пуста).</summary>
    public async Task<MigrationContext> ContextAsync()
    {
        await using var s = await Open(Source);
        await using var t = await Open(Target);
        return new MigrationContext
        {
            Request = new MigrationRequest(),
            Config = new MigrationConfig(),
            SourceEnv = new EnvConfig { Key = "prod", Title = "Прод" },
            TargetEnv = new EnvConfig { Key = "test", Title = "Тест" },
            Options = new ServiceOptions(),
            SourcePg = Source,
            TargetPg = Target,
            SourceSchema = await PilotPgSchema.LoadAsync(s, default),
            TargetSchema = await PilotPgSchema.LoadAsync(t, default),
            TargetSettings = new ServerDatabaseSettings("pilot-ice_ru", Guid.NewGuid(), null,
                new[] { new ServerArchiveSettings(TargetArchive, false, true, "/a", null, null, null) }),
            Archives = new ArchiveTopology(Array.Empty<ResolvedArchive>(), Array.Empty<ResolvedArchive>()),
        };
    }

    public async ValueTask DisposeAsync()
    {
        await using (var admin = await Open(Admin))
        {
            foreach (var db in _databases)
                await Run(admin, $"DROP DATABASE IF EXISTS \"{db}\" WITH (FORCE)");
            foreach (var role in _roles)
                await Run(admin, $"DROP ROLE IF EXISTS \"{role}\"");
        }
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, true);
    }

    // --- схема: снимок dbp26.13.0.62219, типы колонок подобраны так, чтобы внешние ключи сходились ---

    private static string TypeOf(PgTable table, string column)
    {
        if (table.IdentityColumns.Contains(column))
            return "int generated by default as identity";
        if (column == "id")
            return table.Name is "objects" or "chats" or "messages" ? "uuid" : "int";
        return column switch
        {
            "person_id" or "creator_id" or "initiator_id" or "rule_id" or "changeset_data_id" => "int",
            "chat_id" or "message_id" or "messageid" or "object_id" or "obj_id" or "related_message_id" or "last_message_id" or "file_id" or "fa_id" => "uuid",
            "is_writable" => "boolean",
            "creation_date" => "timestamptz",
            "data" => "bytea",
            _ => "text",
        };
    }

    private string Ddl()
    {
        var q = PgNames.Quote(Schema.Schema);
        var sql = new System.Text.StringBuilder($"CREATE SCHEMA {q};\n");
        var referenced = Schema.ForeignKeys.Select(f => (f.RefTable, f.RefColumn)).ToHashSet();
        foreach (var table in Schema.Tables.Values)
        {
            var columns = table.Columns.Select(c => PgNames.Quote(c) + " " + TypeOf(table, c) + (referenced.Contains((table.Name, c)) ? " primary key" : ""));
            sql.Append($"CREATE TABLE {Schema.Q(table.Name)} ({string.Join(", ", columns)});\n");
        }
        foreach (var fk in Schema.ForeignKeys)
            sql.Append($"ALTER TABLE {Schema.Q(fk.Table)} ADD FOREIGN KEY ({PgNames.Quote(fk.Column)}) REFERENCES {Schema.Q(fk.RefTable)} ({PgNames.Quote(fk.RefColumn)});\n");
        return sql.ToString();
    }

    private static string Uuid(string prefix, string i) => $"('{prefix}' || lpad({i}::text, 12, '0'))::uuid";

    private static string SourceData() => $"""
        SET search_path = "{PgSchemaSnapshot.Name}";
        INSERT INTO databaseid (id, data, version, server_version, session_id) VALUES (1, decode('aa', 'hex'), '1', '26.13', 'source');
        INSERT INTO people (id, login, password, data, last_change) VALUES (1, 'admin', 'h1', decode('01', 'hex'), '2026-01-01'), (2, 'ivan', 'h2', decode('02', 'hex'), '2026-01-02');
        INSERT INTO objects (id, data, last_change) SELECT {Uuid("00000000-0000-0000-0002-", "i")}, decode('0a', 'hex'), 'c' FROM generate_series(1, 20) i;
        INSERT INTO chats (id, name, description, creator_id, type, last_message_id, creation_date_utc) VALUES
          ('00000000-0000-0000-0003-000000000001', 'chat 1', '', 1, '0', NULL, '2026-01-01'),
          ('00000000-0000-0000-0003-000000000002', 'chat 2', '', 2, '0', NULL, '2026-01-01');
        INSERT INTO messages (id, data, creator_id, date_time_server_utc, date_time_local_utc, chat_id, related_message_id, type)
          SELECT {Uuid("00000000-0000-0000-0001-", "i")}, decode('0b', 'hex'), 1, 'd', 'd',
                 CASE WHEN i % 2 = 1 THEN '00000000-0000-0000-0003-000000000001'::uuid ELSE '00000000-0000-0000-0003-000000000002'::uuid END, NULL, '0'
          FROM generate_series(1, 500) i;
        UPDATE chats SET last_message_id = {Uuid("00000000-0000-0000-0001-", "499")} WHERE name = 'chat 1';
        UPDATE chats SET last_message_id = {Uuid("00000000-0000-0000-0001-", "500")} WHERE name = 'chat 2';
        INSERT INTO chat_members (chat_id, person_id, is_admin, is_deleted, is_notifiable, date_updated, is_viewer_only) VALUES
          ('00000000-0000-0000-0003-000000000001', 1, 't', 'f', 't', 'd', 'f'), ('00000000-0000-0000-0003-000000000001', 2, 'f', 'f', 't', 'd', 'f');
        INSERT INTO message_person (chat_id, message_id, person_id, issent, mark_as_unread, date) VALUES
          ('00000000-0000-0000-0003-000000000001', {Uuid("00000000-0000-0000-0001-", "1")}, 2, 't', 'f', 'd');
        INSERT INTO history_items (id, object_id, reason, creator_id, created, object, due_to_person)
          SELECT i, {Uuid("00000000-0000-0000-0002-", "1")}, 'r', 1, 'd', 'o', 'p' FROM generate_series(1, 50) i;
        INSERT INTO subscriptions (obj_id, person_id) VALUES ({Uuid("00000000-0000-0000-0002-", "1")}, 2);
        INSERT INTO interitable_access (object_id, access) VALUES ({Uuid("00000000-0000-0000-0002-", "1")}, 'a');
        INSERT INTO rules (id, description) VALUES (1, 'rule');
        INSERT INTO changeset_data (id, data) VALUES (1, decode('ff', 'hex'));
        INSERT INTO rules_changeset_data (rule_id, changeset_data_id) VALUES (1, 1);
        INSERT INTO faslist (fa_id, name, creation_date, is_writable) VALUES ('{SourceArchive}', 'source archive', now(), true);
        INSERT INTO settings (key, data) VALUES ('k', decode('01', 'hex'));
        """;

    private static string TargetData() => $"""
        SET search_path = "{PgSchemaSnapshot.Name}";
        INSERT INTO databaseid (id, data, version, server_version, session_id) VALUES (1, decode('bb', 'hex'), '1', '26.13', 'target');
        INSERT INTO people (id, login, password, data, last_change) VALUES (1, 'old-admin', 'x', decode('01', 'hex'), 'z'), (2, 'old-ivan', 'x', decode('02', 'hex'), 'z'), (3, 'stale', 'x', decode('03', 'hex'), 'z');
        INSERT INTO objects (id, data, last_change) VALUES ('00000000-0000-0000-0002-0000000000ff', decode('0a', 'hex'), 'old');
        INSERT INTO chats (id, name, description, creator_id, type, last_message_id, creation_date_utc) VALUES ('00000000-0000-0000-0003-0000000000ff', 'old chat', '', 3, '0', NULL, 'z');
        INSERT INTO messages (id, data, creator_id, date_time_server_utc, date_time_local_utc, chat_id, related_message_id, type)
          VALUES ('00000000-0000-0000-0001-0000000000ff', decode('0b', 'hex'), 3, 'd', 'd', '00000000-0000-0000-0003-0000000000ff', NULL, '0');
        UPDATE chats SET last_message_id = '00000000-0000-0000-0001-0000000000ff';
        INSERT INTO faslist (fa_id, name, creation_date, is_writable) VALUES ('{TargetArchive}', 'target archive', now(), false), ('dddddddd-4444-4444-4444-444444444444', 'gone', now(), true);
        """;

    public static string Message(int i) => "00000000-0000-0000-0001-" + i.ToString("D12");
}

/// <summary>Движок переноса на настоящем PostgreSQL: полная копия, внешние ключи, определение общей базы и кластера, config.settings.</summary>
public class PostgresIntegrationTests
{
    private static readonly string[] CopiedTables = PgSchemaSnapshot.Load().Tables.Keys.Where(t => t is not ("databaseid" or "faslist")).ToArray();

    // --- схема ---

    [RequiresPostgresFact]
    public async Task SchemaIsReadFromPostgresLikeTheSnapshot()
    {
        await using var pair = await PgPair.CreateAsync(seed: false);
        await using var c = await PgPair.Open(pair.Target);

        var real = await PilotPgSchema.LoadAsync(c, default);

        Assert.Equal(PgSchemaSnapshot.Name, real.Schema);
        Assert.Equal(pair.Schema.Tables.Keys.Order(), real.Tables.Keys.Order());
        foreach (var expected in pair.Schema.Tables.Values)
        {
            Assert.Equal(expected.Columns, real.Tables[expected.Name].Columns);
            Assert.Equal(expected.IdentityColumns.Order(), real.Tables[expected.Name].IdentityColumns.Order());
        }
        Assert.Equal(pair.Schema.ForeignKeys.Order(Comparer<PgForeignKey>.Create((a, b) => string.CompareOrdinal(a.ToString(), b.ToString()))),
            real.ForeignKeys.Order(Comparer<PgForeignKey>.Create((a, b) => string.CompareOrdinal(a.ToString(), b.ToString()))));
        Assert.Empty(real.DifferencesFrom(pair.Schema));
    }

    // --- полная копия ---

    [RequiresPostgresFact]
    public async Task FullCopyReplacesAllTablesAndKeepsTargetIdentity()
    {
        await using var pair = await PgPair.CreateAsync();
        var ctx = await pair.ContextAsync();
        var log = pair.NewLog();
        var targetDatabaseId = await pair.FingerprintAsync(pair.Target, "databaseid");

        var referenced = await FullCopy.CopyDatabaseAsync(ctx, new FileTransfer(ctx.Archives!, log), log, default);

        foreach (var table in CopiedTables)
            Assert.True(await pair.FingerprintAsync(pair.Source, table) == await pair.FingerprintAsync(pair.Target, table), $"таблица {table} различается");
        Assert.Equal(500, await pair.CountAsync(pair.Target, "messages"));
        Assert.Equal(2, await pair.CountAsync(pair.Target, "people"));
        // Свой идентификатор базы остаётся, чужие тела файлов не ссылаются ни на один архив.
        Assert.Equal(targetDatabaseId, await pair.FingerprintAsync(pair.Target, "databaseid"));
        Assert.Empty(referenced);
        // faslist приведён к настройкам приёмника: архив источника и лишний архив приёмника исчезли.
        await using var c = await PgPair.Open(pair.Target);
        Assert.Equal(PgPair.TargetArchive, await PgPair.Scalar<Guid>(c, $"select fa_id from {pair.Schema.Q("faslist")}"));
        Assert.True(await PgPair.Scalar<bool>(c, $"select is_writable from {pair.Schema.Q("faslist")}"));
        // Последовательность identity сдвинута за максимум загруженных строк: иначе первая же новая запись упала бы на дубликате.
        Assert.Equal(3, await PgPair.Scalar<int>(c, $"insert into {pair.Schema.Q("people")} (login, password, data, last_change) values ('new', 'p', decode('03', 'hex'), 'x') returning id"));
    }

    [RequiresPostgresFact]
    public async Task ForeignKeysAreRestoredAndEnforcedAfterFullCopy()
    {
        await using var pair = await PgPair.CreateAsync();
        var ctx = await pair.ContextAsync();
        var log = pair.NewLog();
        await using var before = await PgPair.Open(pair.Target);
        var keysBefore = (await ForeignKeys.ReadAsync(before, null, pair.Schema, default)).Select(k => (k.Table, k.Name, k.Definition)).ToList();

        await FullCopy.CopyDatabaseAsync(ctx, new FileTransfer(ctx.Archives!, log), log, default);

        await using var c = await PgPair.Open(pair.Target);
        var keysAfter = (await ForeignKeys.ReadAsync(c, null, pair.Schema, default)).Select(k => (k.Table, k.Name, k.Definition)).ToList();
        Assert.Equal(pair.Schema.ForeignKeys.Count, keysAfter.Count);
        Assert.Equal(keysBefore, keysAfter);
        // chats / messages ссылаются друг на друга: при загрузке ключи сняты, а после неё проверяют строки как обычно.
        var ex = await Assert.ThrowsAsync<PostgresException>(() => PgPair.Run(c,
            $"insert into {pair.Schema.Q("messages")} (id, chat_id, type) values (gen_random_uuid(), gen_random_uuid(), '0')"));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, ex.SqlState);
    }

    [RequiresPostgresFact]
    public async Task FullCopyWithBrokenReferenceFailsAndLeavesTargetUntouched()
    {
        await using var pair = await PgPair.CreateAsync();
        await using (var s = await PgPair.Open(pair.Source))
        {
            // В источнике ключа нет (как в файловой базе): строка участника чата ссылается на несуществующего пользователя.
            var fk = (await ForeignKeys.ReadAsync(s, null, pair.Schema, default)).Single(k => k.Table == "chat_members" && k.Definition.Contains("(person_id)"));
            await PgPair.Run(s, $"ALTER TABLE {pair.Schema.Q("chat_members")} DROP CONSTRAINT {PgNames.Quote(fk.Name)}");
            await PgPair.Run(s, $"insert into {pair.Schema.Q("chat_members")} (chat_id, person_id) values ('00000000-0000-0000-0003-000000000001', 999)");
        }
        var ctx = await pair.ContextAsync();
        var log = pair.NewLog();
        var before = new Dictionary<string, string>();
        foreach (var table in pair.Schema.Tables.Keys)
            before[table] = await pair.FingerprintAsync(pair.Target, table);

        var ex = await Assert.ThrowsAsync<MigrationException>(() => FullCopy.CopyDatabaseAsync(ctx, new FileTransfer(ctx.Archives!, log), log, default));

        Assert.Contains("нарушают связь chat_members", ex.Message);
        Assert.Contains("Приёмник не изменён", ex.Message);
        foreach (var table in pair.Schema.Tables.Keys)
            Assert.True(before[table] == await pair.FingerprintAsync(pair.Target, table), $"таблица {table} приёмника изменилась после отката");
        await using var c = await PgPair.Open(pair.Target);
        Assert.Equal(pair.Schema.ForeignKeys.Count, (await ForeignKeys.ReadAsync(c, null, pair.Schema, default)).Count);
    }

    [RequiresPostgresFact]
    public async Task OrphanRowsAreRemovedWithTheirChildrenBeforeKeysAreRestored()
    {
        await using var pair = await PgPair.CreateAsync();
        await using var c = await PgPair.Open(pair.Target);
        await using var tx = await c.BeginTransactionAsync();
        var keys = await ForeignKeys.ReadAsync(c, tx, pair.Schema, default);
        await ForeignKeys.DropAsync(c, tx, pair.Schema, keys, default);
        var orphan = PgPair.Message(900);
        // Сообщение без чата и его адресат (сирота второго порядка: чат у адресата есть, а сообщения не станет).
        await PgPair.Run(c, $"insert into {pair.Schema.Q("messages")} (id, chat_id, type) values ('{orphan}', gen_random_uuid(), '0')", tx);
        await PgPair.Run(c, $"insert into {pair.Schema.Q("message_person")} (chat_id, message_id, person_id) values ('00000000-0000-0000-0003-0000000000ff', '{orphan}', 1)", tx);

        var removed = await ForeignKeys.CleanOrphansAsync(c, tx, pair.Schema, keys, pair.Schema.ForeignKeys, default);

        Assert.Equal(1, removed["messages.chat_id / chats"]);
        Assert.Equal(1, removed["message_person.message_id / messages"]);
        Assert.Equal(0, await PgPair.Scalar<long>(c, $"select count(*) from {pair.Schema.Q("messages")} where id = '{orphan}'", tx));
        // Остальные строки на месте, ключи возвращаются без нарушений.
        Assert.Equal(1, await PgPair.Scalar<long>(c, $"select count(*) from {pair.Schema.Q("messages")}", tx));
        await ForeignKeys.RestoreAsync(c, tx, pair.Schema, keys, default);
    }

    // --- одна и та же база ---

    [RequiresPostgresFact]
    public async Task SamePgDatabaseMatchesOnlyTheSameDatabase()
    {
        await using var pair = await PgPair.CreateAsync(seed: false);
        var b = new NpgsqlConnectionStringBuilder(pair.Source);
        // Та же база, записанная иначе: порт внутри Host, другой порядок и набор параметров.
        var sameBase = $"Database={b.Database};Username={b.Username};Host={b.Host!.ToUpperInvariant()}:{b.Port}";
        var sameBase2 = new NpgsqlConnectionStringBuilder(pair.Source) { Pooling = true, ApplicationName = "other" }.ConnectionString;

        Assert.True(PgConnections.SameDatabase(pair.Source, sameBase));
        Assert.True(PgConnections.SameDatabase(pair.Source, sameBase2));
        Assert.False(PgConnections.SameDatabase(pair.Source, pair.Target));
        Assert.False(PgConnections.SameDatabase(pair.Source, new NpgsqlConnectionStringBuilder(pair.Source) { Port = b.Port + 1 }.ConnectionString));
        Assert.False(PgConnections.SameDatabase(pair.Source, null));
        // Регистр хоста: у IP-адреса он ничего не меняет, поэтому проверяем на имени.
        Assert.True(PgConnections.SameDatabase("Host=localhost;Database=d;Username=u", "Host=LOCALHOST;Database=d;Username=u"));
        Assert.False(PgConnections.SameDatabase("Host=localhost;Database=d;Username=u", "Host=localhost;Database=D;Username=u"));
    }

    // --- две базы на одном кластере ---

    [RequiresPostgresFact]
    public async Task TwoDatabasesOnOneServerAreBlockedAsSharedDataDirectory()
    {
        await using var pair = await PgPair.CreateAsync(seed: false);
        await using var s = await PgPair.Open(pair.Source);
        await using var t = await PgPair.Open(pair.Target);
        var checks = new CheckList();

        await MigrationContext.CheckSeparateClustersAsync(s, t, "Прод", "Тест", checks, default);

        var item = Assert.Single(checks.Items);
        Assert.Equal(CheckLevel.Blocker, item.Level);
        Assert.Contains("одной и той же папке данных", item.Text);
        Assert.Contains("«Прод» и «Тест»", item.Text);
    }

    [RequiresPostgresFact]
    public async Task UserWithoutPgMonitorGetsWarningInsteadOfSilentPass()
    {
        await using var pair = await PgPair.CreateAsync(seed: false);
        var (user, password) = await pair.CreateUnprivilegedRoleAsync();
        string As(string cs) => new NpgsqlConnectionStringBuilder(cs) { Username = user, Password = password }.ConnectionString;
        await using var s = await PgPair.Open(As(pair.Source));
        await using var t = await PgPair.Open(As(pair.Target));
        var checks = new CheckList();

        await MigrationContext.CheckSeparateClustersAsync(s, t, "Прод", "Тест", checks, default);

        var item = Assert.Single(checks.Items);
        Assert.Equal(CheckLevel.Warning, item.Level);
        Assert.Contains("pg_monitor", item.Text);
        Assert.False(checks.HasBlockers);
    }

    [RequiresPostgresFact(needOtherCluster: true)]
    public async Task DatabasesOnDifferentServersPassTheClusterCheck()
    {
        await using var pair = await PgPair.CreateAsync(seed: false);
        await using var s = await PgPair.Open(pair.Source);
        await using var t = await PgPair.Open(Environment.GetEnvironmentVariable(RequiresPostgresFactAttribute.OtherVariable)!);
        var checks = new CheckList();

        await MigrationContext.CheckSeparateClustersAsync(s, t, "Прод", "Тест", checks, default);

        Assert.Empty(checks.Items);
    }

    // --- настройки кластера из config.settings ---

    private const string ClusterXml = """
        <?xml version="1.0" encoding="utf-16"?>
        <Configuration>
          <DatabaseParameters Name="pilot-ice_ru" Id="11111111-1111-1111-1111-111111111111">
            <FileArchiveParametersS3 Id="aaaaaaaa-0000-0000-0000-000000000001" IsWritable="true" Url="http://rustfs-test:9000" Bucket="pilot-ice-ru" AccessKeyId="s3-user" />
            <PgConnectionString ConnectionString="Host=postgres-test:5432;Username=pilotuser;Database=pilot-ice_ru" EncryptedPassword="c2VjcmV0" />
          </DatabaseParameters>
          <CheckChangeCreator>true</CheckChangeCreator>
          <IsSnapshotEnabled>false</IsSnapshotEnabled>
          <IsAutomationLoggingEnabled>true</IsAutomationLoggingEnabled>
          <RedisConnectionString>redis-test:6379</RedisConnectionString>
        </Configuration>
        """;

    /// <summary>Сервис кластерного контура: база конфигурации на живом сервере, пароль роли - из окружения сервиса.</summary>
    private static async Task<(ServerDatabaseSettings? Settings, List<CheckItem> Report)> ReadClusterSettingsAsync(PgPair pair, string configurationDatabase)
    {
        var admin = new NpgsqlConnectionStringBuilder(pair.Admin);
        var options = new ServiceOptions { ConfigurationDatabase = configurationDatabase, PgPasswords = { ["test"] = admin.Password! } };
        var db = new AdminDatabaseInfo
        {
            Name = "pilot-ice_ru",
            Id = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            RedisConnectionString = "redis-test:6379",
            PostgresConnectionString = $"host={admin.Host};port={admin.Port};username={admin.Username};database=pilot-ice_ru",
        };
        var report = new List<CheckItem>();
        var settings = await SettingsSource.ReadAsync(new EnvConfig { Key = "test", Title = "Тест" }, db, options,
            needArchives: true, needBehavior: true, (level, text) => report.Add(new CheckItem(level, text)), default);
        return (settings, report);
    }

    private static async Task<string> CreateConfigurationDatabaseAsync(PgPair pair, string? xml, bool withTable = true)
    {
        var cs = await pair.CreateDatabaseAsync("cfg");
        await using var c = await PgPair.Open(cs);
        if (withTable)
        {
            await PgPair.Run(c, "CREATE SCHEMA config; CREATE TABLE config.settings (id int primary key, data text)");
            if (xml is not null)
                await using (var cmd = new NpgsqlCommand("INSERT INTO config.settings (id, data) VALUES (1, @x)", c))
                {
                    cmd.Parameters.AddWithValue("x", xml);
                    await cmd.ExecuteNonQueryAsync();
                }
        }
        return new NpgsqlConnectionStringBuilder(cs).Database!;
    }

    [RequiresPostgresFact]
    public async Task ClusterSettingsAreReadFromConfigSettingsTable()
    {
        await using var pair = await PgPair.CreateAsync(seed: false);
        var database = await CreateConfigurationDatabaseAsync(pair, ClusterXml);

        var (settings, report) = await ReadClusterSettingsAsync(pair, database);

        Assert.Empty(report);
        Assert.NotNull(settings);
        Assert.Equal(Guid.Parse("11111111-1111-1111-1111-111111111111"), settings.Id);
        var archive = Assert.Single(settings.Archives);
        Assert.True(archive.IsS3);
        Assert.Equal("pilot-ice-ru", archive.S3Bucket);
        Assert.Equal("Host=postgres-test:5432;Username=pilotuser;Database=pilot-ice_ru", settings.PgConnectionString);
        Assert.Equal(2, settings.Behavior!.Count);
        Assert.True(settings.Behavior["CheckChangeCreator"]);
        Assert.False(settings.Behavior["IsSnapshotEnabled"]);
        Assert.DoesNotContain("IsAutomationLoggingEnabled", settings.Behavior.Keys);
    }

    [RequiresPostgresFact]
    public async Task MissingConfigSettingsTableIsBlockerWithItsName()
    {
        await using var pair = await PgPair.CreateAsync(seed: false);
        var database = await CreateConfigurationDatabaseAsync(pair, null, withTable: false);

        var (settings, report) = await ReadClusterSettingsAsync(pair, database);

        Assert.Null(settings);
        var item = Assert.Single(report);
        Assert.Equal(CheckLevel.Blocker, item.Level);
        Assert.Contains("config.settings", item.Text);
        Assert.Contains(database, item.Text);
    }

    [RequiresPostgresFact]
    public async Task EmptyConfigSettingsRowIsBlocker()
    {
        await using var pair = await PgPair.CreateAsync(seed: false);
        var database = await CreateConfigurationDatabaseAsync(pair, null);

        var (settings, report) = await ReadClusterSettingsAsync(pair, database);

        Assert.Null(settings);
        Assert.Contains("нет конфигурации кластера", Assert.Single(report).Text);
    }

    [RequiresPostgresFact]
    public async Task MissingConfigurationDatabaseIsBlockerNotCrash()
    {
        await using var pair = await PgPair.CreateAsync(seed: false);

        var (settings, report) = await ReadClusterSettingsAsync(pair, "mig_it_no_such_database");

        Assert.Null(settings);
        Assert.Equal(CheckLevel.Blocker, Assert.Single(report).Level);
    }
}
