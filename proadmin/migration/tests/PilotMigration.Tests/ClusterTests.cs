using Ascon.Pilot.DataClasses;
using Microsoft.Extensions.Configuration;
using PilotMigration.Config;
using PilotMigration.Database;
using PilotMigration.Engine;
using PilotMigration.Pilot;

namespace PilotMigration.Tests;

/// <summary>Кластерный Pilot-Server: признак кластера, настройки из config.settings, пароль PostgreSQL из окружения, устаревший settings.xml, защита прода.</summary>
public class ClusterTests
{
    private static readonly Guid DbId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid ArchiveA = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid ArchiveB = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");

    // Конфигурация кластера, как её отдаёт Pilot-Server 26.13 (config.settings, id = 1): тот же формат, что у settings.xml,
    // пароль PostgreSQL только зашифрованный, параметры поведения в корне, строка Redis.
    private const string ClusterXml = """
        <?xml version="1.0" encoding="utf-16"?>
        <Configuration>
          <DatabaseParameters Name="pilot-ice_ru" Id="11111111-1111-1111-1111-111111111111">
            <FileArchiveParametersS3 Id="aaaaaaaa-0000-0000-0000-000000000001" IsWritable="true" Url="http://rustfs-test:9000" Bucket="pilot-ice-ru" AccessKeyId="s3-user" />
            <PgConnectionString ConnectionString="Host=postgres-test:5432;Username=pilotuser;Database=pilot-ice_ru" EncryptedPassword="c2VjcmV0" />
          </DatabaseParameters>
          <CheckChangeCreator>true</CheckChangeCreator>
          <AreParallelChangesDisabled>true</AreParallelChangesDisabled>
          <IsSnapshotEnabled>false</IsSnapshotEnabled>
          <IsAutomationLoggingEnabled>true</IsAutomationLoggingEnabled>
          <IsExtendedLoggingEnabled>false</IsExtendedLoggingEnabled>
          <IsFileReadJournalingEnabled>false</IsFileReadJournalingEnabled>
          <EnableCSharpScriptDebug>false</EnableCSharpScriptDebug>
          <RedisConnectionString>redis-test:6379</RedisConnectionString>
        </Configuration>
        """;

    private const string ApiPg = "host=postgres-test:5432;username=pilotuser;include error detail=True;database=pilot-ice_ru";

    private static AdminDatabaseInfo Db(string? redis = null, string? pg = ApiPg, Guid? id = null, IEnumerable<Guid>? archives = null) => new()
    {
        Name = "pilot-ice_ru",
        Id = id ?? DbId,
        RedisConnectionString = redis,
        PostgresConnectionString = pg,
        FileArchives = (archives ?? Array.Empty<Guid>()).Select(a => new DFileArchiveInfo { Id = a }).ToList(),
    };

    private static ServerDatabaseSettings File(Guid? id = null, string? pg = "Host=postgres-test:5432;Username=pilotuser;Password=x;Database=pilot-ice_ru", IEnumerable<Guid>? archives = null) =>
        new("pilot-ice_ru", id ?? DbId, pg, (archives ?? Array.Empty<Guid>()).Select(a => new ServerArchiveSettings(a, false, true, "/a", null, null, null)).ToList());

    private static readonly EnvConfig Test = new() { Key = "test", Title = "Тест", SettingsXmlPath = "/mnt/test/settings/settings.xml" };

    // --- признак кластера ---

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("  ", false)]
    [InlineData("redis-test:6379", true)]
    public void ClusterIsRecognizedByRedis(string? redis, bool expected) =>
        Assert.Equal(expected, SettingsSource.IsCluster(Db(redis)));

    // --- настройки кластера из config.settings ---

    [Fact]
    public void ClusterXmlIsParsedLikeSettingsXml()
    {
        var s = SettingsSource.ParseCluster(ClusterXml, "pilot-ice_ru", "configurationdb");

        Assert.Equal(DbId, s.Id);
        var archive = Assert.Single(s.Archives);
        Assert.True(archive.IsS3);
        Assert.Equal("http://rustfs-test:9000", archive.S3Url);
        Assert.Equal("pilot-ice-ru", archive.S3Bucket);
        Assert.Equal("s3-user", archive.S3AccessKeyId);
        // Пароля в строке нет: он зашифрован (EncryptedPassword), берётся из окружения сервиса.
        Assert.Equal("Host=postgres-test:5432;Username=pilotuser;Database=pilot-ice_ru", s.PgConnectionString);
        Assert.Equal(3, s.Behavior!.Count);
        Assert.True(s.Behavior["CheckChangeCreator"]);
        Assert.True(s.Behavior["AreParallelChangesDisabled"]);
        Assert.False(s.Behavior["IsSnapshotEnabled"]);
        Assert.DoesNotContain("IsAutomationLoggingEnabled", s.Behavior.Keys);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void EmptyClusterConfigurationIsAnError(string? xml) =>
        Assert.Contains("нет конфигурации кластера", Assert.Throws<MigrationException>(() => SettingsSource.ParseCluster(xml, "pilot-ice_ru", "configurationdb")).Message);

    [Fact]
    public void BrokenClusterXmlIsAnError() =>
        Assert.Contains("Не удалось разобрать", Assert.Throws<MigrationException>(() => SettingsSource.ParseCluster("<Configuration><a>", "pilot-ice_ru", "configurationdb")).Message);

    [Fact]
    public void DatabaseMissingInClusterConfigurationIsAnError() =>
        Assert.Contains("нет базы «other»", Assert.Throws<MigrationException>(() => SettingsSource.ParseCluster(ClusterXml, "other", "configurationdb")).Message);

    // --- пароль PostgreSQL из окружения ---

    [Fact]
    public void PgPasswordComesFromEnvironmentByContourKey()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Migration:PgPasswords:TEST"] = "env-secret",
            ["Migration:ConfigurationDatabase"] = "cfgdb",
        }).Build();

        var options = configuration.GetSection("Migration").Get<ServiceOptions>()!;

        Assert.Equal("env-secret", options.PgPassword("test"));
        Assert.Null(options.PgPassword("stage"));
        Assert.Equal("cfgdb", options.ConfigurationDatabase);
        Assert.Equal("configurationdb", new ServiceOptions().ConfigurationDatabase);
        Assert.Equal("Migration__PgPasswords__test", ServiceOptions.PgPasswordVariable("test"));
    }

    [Fact]
    public void EmptyPasswordVariableIsNoPassword() =>
        Assert.Null(new ServiceOptions { PgPasswords = { ["test"] = "" } }.PgPassword("test"));

    [Fact]
    public void PasswordFromSettingsXmlWinsOverEnvironment()
    {
        var cs = new Npgsql.NpgsqlConnectionStringBuilder(PgConnections.Build(Test, "Host=pg:5432;Username=u;Password=from-file;Database=d", "from-env"));
        Assert.Equal("from-file", cs.Password);
    }

    [Fact]
    public void EnvironmentPasswordIsUsedWhenServerGivesNone()
    {
        var cs = new Npgsql.NpgsqlConnectionStringBuilder(PgConnections.Build(Test, ApiPg, "from-env", cluster: true));
        Assert.Equal("from-env", cs.Password);
        Assert.Equal("postgres-test", cs.Host);
        Assert.Equal(5432, cs.Port);
        Assert.Equal("pilot-ice_ru", cs.Database);
    }

    [Fact]
    public void ConfigurationDatabaseReplacesDatabaseOnTheSameServer()
    {
        var cs = new Npgsql.NpgsqlConnectionStringBuilder(PgConnections.Build(Test, ApiPg, "pw", cluster: true, database: "configurationdb"));
        Assert.Equal("configurationdb", cs.Database);
        Assert.Equal("postgres-test", cs.Host);
        Assert.Equal("pilotuser", cs.Username);
    }

    [Fact]
    public void ClusterWithoutPasswordNamesTheVariable()
    {
        var error = Assert.Throws<MigrationException>(() => PgConnections.Build(Test, ApiPg, null, cluster: true));
        Assert.Contains("Migration__PgPasswords__test", error.Message);
        Assert.Contains("TEST_PILOT_PG_PASSWORD", error.Message);
    }

    [Fact]
    public void MonolithWithoutPasswordStillBuildsAsBefore() =>
        Assert.Null(new Npgsql.NpgsqlConnectionStringBuilder(PgConnections.Build(Test, ApiPg)).Password);

    // --- одинаковая база PostgreSQL ---

    [Theory]
    [InlineData("host=postgres-test:5432;username=a;database=db", "Host=POSTGRES-test;Port=5432;Username=b;Database=db", true)]
    [InlineData("host=postgres-test;database=db", "host=postgres-test:5432;database=db", true)]
    [InlineData("host=postgres-test:5432;database=db", "host=postgres-test:5433;database=db", false)]
    [InlineData("host=postgres-test:5432;database=db", "host=postgres-stage:5432;database=db", false)]
    [InlineData("host=postgres-test:5432;database=db", "host=postgres-test:5432;database=other", false)]
    [InlineData("host=postgres-test:5432;database=db", null, false)]
    [InlineData("не строка подключения", "host=postgres-test:5432;database=db", false)]
    public void SamePostgresDatabaseIsNormalized(string a, string? b, bool same) =>
        Assert.Equal(same, PgConnections.SameDatabase(a, b));

    // --- устаревший settings.xml ---

    [Fact]
    public void MatchingSettingsXmlIsNotStale() =>
        Assert.Null(SettingsSource.StaleError(Test, File(archives: new[] { ArchiveA, ArchiveB }), Db(archives: new[] { ArchiveB, ArchiveA })));

    [Fact]
    public void SettingsXmlWithoutPostgresStringIsComparedByTheRest() =>
        Assert.Null(SettingsSource.StaleError(Test, File(pg: null, archives: new[] { ArchiveA }), Db(pg: null, archives: new[] { ArchiveA })));

    [Fact]
    public void StaleSettingsXmlWithOtherDatabaseIdIsReported()
    {
        var error = SettingsSource.StaleError(Test, File(id: Guid.NewGuid(), archives: new[] { ArchiveA }), Db(archives: new[] { ArchiveA }))!;
        Assert.Contains("settings.xml не соответствует серверу: очистите путь или обновите файл", error);
        Assert.Contains("идентификатор базы", error);
        Assert.Contains("/mnt/test/settings/settings.xml", error);
    }

    [Fact]
    public void StaleSettingsXmlWithOtherArchivesIsReported()
    {
        Assert.Contains("файловых архивов", SettingsSource.StaleError(Test, File(archives: new[] { ArchiveA }), Db(archives: new[] { ArchiveB }))!);
        Assert.Contains("файловых архивов", SettingsSource.StaleError(Test, File(archives: new[] { ArchiveA }), Db(archives: new[] { ArchiveA, ArchiveB }))!);
    }

    [Fact]
    public void StaleSettingsXmlWithOldPostgresIsReported()
    {
        var old = "Host=postgres-old:5432;Username=pilotuser;Password=x;Database=pilot-ice_ru";
        var error = SettingsSource.StaleError(Test, File(pg: old, archives: new[] { ArchiveA }), Db(archives: new[] { ArchiveA }))!;
        Assert.Contains("postgres-old:5432/pilot-ice_ru", error);
        Assert.Contains("postgres-test:5432/pilot-ice_ru", error);
        Assert.DoesNotContain("Password", error);
    }

    [Fact]
    public async Task StaleFileIsBlockedAndNotUsed()
    {
        var path = WriteSettingsXml("""
            <Configuration><DatabaseParameters Name="pilot-ice_ru" Id="22222222-2222-2222-2222-222222222222">
              <PgConnectionString ConnectionString="Host=postgres-old:5432;Username=pilotuser;Password=x;Database=pilot-ice_ru" />
            </DatabaseParameters></Configuration>
            """);
        var reports = new List<(CheckLevel Level, string Text)>();

        try
        {
            var settings = await SettingsSource.ReadAsync(new EnvConfig { Key = "test", Title = "Тест", SettingsXmlPath = path },
                Db(), new ServiceOptions(), false, false, (l, t) => reports.Add((l, t)), CancellationToken.None);

            Assert.Null(settings);
            var report = Assert.Single(reports);
            Assert.Equal(CheckLevel.Blocker, report.Level);
            Assert.Contains("не соответствует серверу", report.Text);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public async Task ActualFileIsReadWithBehavior()
    {
        var path = WriteSettingsXml("""
            <Configuration>
              <DatabaseParameters Name="pilot-ice_ru" Id="11111111-1111-1111-1111-111111111111">
                <PgConnectionString ConnectionString="Host=postgres-test:5432;Username=pilotuser;Password=x;Database=pilot-ice_ru" />
              </DatabaseParameters>
              <IsSnapshotEnabled>true</IsSnapshotEnabled>
            </Configuration>
            """);
        var reports = new List<(CheckLevel Level, string Text)>();

        try
        {
            var settings = await SettingsSource.ReadAsync(new EnvConfig { Key = "test", Title = "Тест", SettingsXmlPath = path },
                Db(), new ServiceOptions(), true, true, (l, t) => reports.Add((l, t)), CancellationToken.None);

            Assert.Empty(reports);
            Assert.True(settings!.Behavior!["IsSnapshotEnabled"]);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Theory]
    [InlineData(true, CheckLevel.Blocker)]
    [InlineData(false, CheckLevel.Warning)]
    public async Task UnreadableFileIsBlockerOnlyWhenArchivesAreNeeded(bool needArchives, CheckLevel expected)
    {
        var reports = new List<(CheckLevel Level, string Text)>();

        var settings = await SettingsSource.ReadAsync(new EnvConfig { Key = "test", Title = "Тест", SettingsXmlPath = "/no/such/settings.xml" },
            Db(), new ServiceOptions(), needArchives, false, (l, t) => reports.Add((l, t)), CancellationToken.None);

        Assert.Null(settings);
        Assert.Equal(expected, Assert.Single(reports).Level);
    }

    // --- кластер: чтение без обращения к базе конфигурации ---

    [Fact]
    public async Task ClusterIsNotReadWhenNeitherArchivesNorBehaviorAreNeeded()
    {
        var reports = new List<(CheckLevel, string)>();

        var settings = await SettingsSource.ReadAsync(Test, Db("redis-test:6379"), new ServiceOptions(), false, false, (l, t) => reports.Add((l, t)), CancellationToken.None);

        Assert.Null(settings);
        Assert.Empty(reports);
    }

    [Fact]
    public async Task ClusterWithoutPasswordIsBlockerWithVariableName()
    {
        var reports = new List<(CheckLevel Level, string Text)>();

        var settings = await SettingsSource.ReadAsync(Test, Db("redis-test:6379"), new ServiceOptions(), true, false, (l, t) => reports.Add((l, t)), CancellationToken.None);

        Assert.Null(settings);
        // Путь к settings.xml в кластере не используется - об этом предупреждение, а блокирует отсутствие пароля.
        Assert.Contains(reports, r => r.Level == CheckLevel.Warning && r.Text.Contains("путь к settings.xml не используется"));
        Assert.Contains(reports, r => r.Level == CheckLevel.Blocker && r.Text.Contains("Migration__PgPasswords__test"));
    }

    // --- защита прода без settings.xml ---

    [Fact]
    public void TargetWithProdDatabaseIdIsBlocked()
    {
        var error = MigrationContext.ProdIdentityError(Db(id: DbId), Db(pg: "host=postgres-test:5432;database=other", id: DbId), "Тест")!;
        Assert.Contains("идентификатор базы", error);
        Assert.Contains("Запись в прод запрещена", error);
    }

    [Fact]
    public void TargetWithProdPostgresIsBlocked()
    {
        var prod = Db(pg: "host=postgres-prod:5432;username=pilotuser;database=pilot-ice_ru", id: Guid.NewGuid());
        var target = Db(pg: "Host=POSTGRES-prod;Port=5432;Username=other;Database=pilot-ice_ru", id: Guid.NewGuid());

        Assert.Contains("postgres-prod:5432/pilot-ice_ru", MigrationContext.ProdIdentityError(prod, target, "Тест")!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SeparateTargetIsNotBlocked()
    {
        var prod = Db(pg: "host=postgres-prod:5432;database=pilot-ice_ru", id: Guid.NewGuid());
        var target = Db(pg: "host=postgres-test:5432;database=pilot-ice_ru", id: Guid.NewGuid());

        Assert.Null(MigrationContext.ProdIdentityError(prod, target, "Тест"));
    }

    [Fact]
    public void EmptyIdsAreNotTreatedAsSame() =>
        Assert.Null(MigrationContext.ProdIdentityError(
            Db(pg: "host=a:5432;database=x", id: Guid.Empty), Db(pg: "host=b:5432;database=x", id: Guid.Empty), "Тест"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a connection string;;=")]
    [InlineData("username=pilotuser")]
    public void UnreadableTargetPostgresAddressIsBlocked(string? targetPg)
    {
        var prod = Db(pg: "host=postgres-prod:5432;database=pilot-ice_ru", id: Guid.NewGuid());

        Assert.Contains("Нельзя сравнить", MigrationContext.ProdIdentityError(prod, Db(pg: targetPg, id: Guid.NewGuid()), "Тест")!);
        Assert.Contains("Нельзя сравнить", MigrationContext.ProdIdentityError(Db(pg: targetPg, id: Guid.NewGuid()), prod, "Тест")!);
    }

    [Fact]
    public void SameIdMessageDoesNotAdviseFixingAddress() =>
        Assert.DoesNotContain("адрес Pilot", MigrationContext.ProdIdentityError(Db(id: DbId), Db(pg: "host=other:5432;database=x", id: DbId), "Тест")!);

    // --- CheckNotProdAsync: прод недоступен - блокер, прод-источник - без лишнего запроса, прод не настроен - пропуск ---

    private static readonly EnvConfig ProdEnv = new() { Key = "prod", Title = "Прод", PilotUrl = "http://pilot-prod:5545/pilot-ice_ru" };

    private static async Task<(CheckList Checks, int Calls)> RunCheckNotProd(MigrationConfig config, EnvConfig source, EnvConfig target,
        AdminDatabaseInfo? sourceDb, AdminDatabaseInfo targetDb, Func<AdminDatabaseInfo> prodDb)
    {
        var ctx = new MigrationContext
        {
            Request = new MigrationRequest(),
            Config = config,
            SourceEnv = source,
            TargetEnv = target,
            Options = new ServiceOptions(),
            SourceDb = sourceDb,
            TargetDb = targetDb,
        };
        var checks = new CheckList();
        var calls = 0;
        await MigrationContext.CheckNotProdAsync(ctx, config, (_, _) => { calls++; return Task.FromResult(prodDb()); }, checks, CancellationToken.None);
        return (checks, calls);
    }

    [Fact]
    public async Task UnavailableProdIsBlocker()
    {
        var config = new MigrationConfig { Environments = { ProdEnv } };

        var (checks, _) = await RunCheckNotProd(config, new EnvConfig { Key = "stage", Title = "Стейдж" }, Test, Db(), Db(),
            () => throw new MigrationException("нет связи"));

        var item = Assert.Single(checks.Items);
        Assert.Equal(CheckLevel.Blocker, item.Level);
        Assert.Contains("нет связи", item.Text);
    }

    [Fact]
    public async Task ProdSourceIsNotQueriedAgain()
    {
        var config = new MigrationConfig { Environments = { ProdEnv } };
        var prod = Db(pg: "host=postgres-prod:5432;database=pilot-ice_ru", id: Guid.NewGuid());

        var (ok, calls) = await RunCheckNotProd(config, ProdEnv, Test, prod, Db(pg: "host=postgres-test:5432;database=pilot-ice_ru", id: Guid.NewGuid()),
            () => throw new InvalidOperationException("не должен вызываться"));
        Assert.Equal(0, calls);
        Assert.Empty(ok.Items);

        var (blocked, _) = await RunCheckNotProd(config, ProdEnv, Test, prod, Db(pg: prod.PostgresConnectionString, id: Guid.NewGuid()),
            () => throw new InvalidOperationException("не должен вызываться"));
        Assert.Contains(blocked.Items, i => i.Level == CheckLevel.Blocker && i.Text.Contains("Запись в прод запрещена"));
    }

    [Fact]
    public async Task TargetOnProdDatabaseFetchedFromServerIsBlocker()
    {
        var config = new MigrationConfig { Environments = { ProdEnv } };
        var prod = Db(pg: "host=postgres-prod:5432;database=pilot-ice_ru", id: Guid.NewGuid());

        var (checks, calls) = await RunCheckNotProd(config, new EnvConfig { Key = "stage", Title = "Стейдж" }, Test, Db(),
            Db(pg: "host=POSTGRES-PROD:5432;database=pilot-ice_ru", id: Guid.NewGuid()), () => prod);

        Assert.Equal(1, calls);
        Assert.Contains(checks.Items, i => i.Level == CheckLevel.Blocker && i.Text.Contains("Запись в прод запрещена"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnconfiguredProdOrProdTargetIsSkipped(bool targetIsProd)
    {
        var config = targetIsProd
            ? new MigrationConfig { Environments = { ProdEnv } }
            : new MigrationConfig { Environments = { new EnvConfig { Key = "prod", Title = "Прод" } } };

        var (checks, calls) = await RunCheckNotProd(config, new EnvConfig { Key = "stage", Title = "Стейдж" }, targetIsProd ? ProdEnv : Test, Db(), Db(),
            () => throw new InvalidOperationException("не должен вызываться"));

        Assert.Equal(0, calls);
        Assert.Empty(checks.Items);
    }

    // --- каталог server-settings приёмника и проверка переноса настроек ---

    [Fact]
    public void ClusterServerSettingsDirectoryIsByContourKey()
    {
        Assert.Equal("/mnt/test/server-settings", ServerSettingsCopy.DirectoryFor(new EnvConfig { Key = "test", SettingsXmlPath = "/old/x/settings/settings.xml" }, cluster: true));
        Assert.Equal("/mnt/stage/server-settings", ServerSettingsCopy.DirectoryFor(new EnvConfig { Key = "stage" }, cluster: true));
        Assert.Null(ServerSettingsCopy.DirectoryFor(new EnvConfig { Key = "stage" }, cluster: false));
    }

    [Fact]
    public void ServerSettingsCopyIsRefusedWhenTargetDirectoryIsMissing()
    {
        var checks = new CheckList();
        CheckServerSettings(checks, target: Test, targetDb: Db(), sourceBehavior: new Dictionary<string, bool>());

        Assert.Contains(checks.Items, i => i.Level == CheckLevel.Blocker && i.Text.Contains("не смонтирован"));
    }

    [Fact]
    public void ServerSettingsCopyIsRefusedForClusterTargetWithoutMount()
    {
        var checks = new CheckList();
        CheckServerSettings(checks, target: Test, targetDb: Db("redis-test:6379"), sourceBehavior: new Dictionary<string, bool>());

        // /mnt/test/server-settings в тестовой среде не смонтирован.
        Assert.Contains(checks.Items, i => i.Level == CheckLevel.Blocker && i.Text.Contains("/mnt/test/server-settings"));
    }

    [Fact]
    public void ServerSettingsCopyIsRefusedWhenSourceHasNoBehavior()
    {
        var root = Directory.CreateTempSubdirectory("migration-ss-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "test", "server-settings"));
            var target = new EnvConfig { Key = "test", Title = "Тест", SettingsXmlPath = root.Replace('\\', '/') + "/test/settings/settings.xml" };

            var fromApi = new ServerDatabaseSettings("pilot-ice_ru", DbId, null, Array.Empty<ServerArchiveSettings>());
            var noPath = new CheckList();
            CheckServerSettings(noPath, target, Db(), sourceBehavior: null, source: new EnvConfig { Key = "stage", Title = "Стейдж" }, sourceSettings: fromApi);
            Assert.Contains(noPath.Items, i => i.Level == CheckLevel.Blocker && i.Text.Contains("не задан путь к settings.xml источника"));
            Assert.DoesNotContain(noPath.Items, i => i.Text.Contains("не смонтирован"));

            var cluster = new CheckList();
            CheckServerSettings(cluster, target, Db(), sourceBehavior: null, sourceDb: Db("redis-stage:6379"), source: new EnvConfig { Key = "stage", Title = "Стейдж" });
            Assert.Contains(cluster.Items, i => i.Level == CheckLevel.Blocker && i.Text.Contains("конфигурация кластера недоступна"));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void ServerSettingsCopyIsAllowedWhenDirectoryAndSourceAreThere()
    {
        var root = Directory.CreateTempSubdirectory("migration-ss-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "test", "server-settings"));
            var target = new EnvConfig { Key = "test", Title = "Тест", SettingsXmlPath = root.Replace('\\', '/') + "/test/settings/settings.xml" };
            var checks = new CheckList();

            CheckServerSettings(checks, target, Db(), sourceBehavior: new Dictionary<string, bool> { ["IsSnapshotEnabled"] = true });

            Assert.Empty(checks.Items);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void ServerSettingsAreWrittenToTargetDirectory()
    {
        var root = Directory.CreateTempSubdirectory("migration-ss-").FullName;
        try
        {
            var dir = Directory.CreateDirectory(Path.Combine(root, "test", "server-settings")).FullName;
            var target = new EnvConfig { Key = "test", Title = "Тест", SettingsXmlPath = root.Replace('\\', '/') + "/test/settings/settings.xml" };

            var changes = ServerSettingsCopy.Apply(new Dictionary<string, bool> { ["CheckChangeCreator"] = true }, target, false, "миграция (admin)");

            Assert.Equal(new[] { "CheckChangeCreator: не задано / true" }, changes);
            Assert.Contains("\nCheckChangeCreator=true\n", System.IO.File.ReadAllText(Path.Combine(dir, ServerSettingsCopy.ConfFile)));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void ServerSettingsAreNotWrittenWithoutMountedDirectory() =>
        Assert.Throws<MigrationException>(() => ServerSettingsCopy.Apply(new Dictionary<string, bool>(), new EnvConfig { Key = "test" }, true, "миграция (admin)"));

    private static void CheckServerSettings(CheckList checks, EnvConfig target, AdminDatabaseInfo targetDb, IReadOnlyDictionary<string, bool>? sourceBehavior,
        EnvConfig? source = null, AdminDatabaseInfo? sourceDb = null, ServerDatabaseSettings? sourceSettings = null)
    {
        source ??= new EnvConfig { Key = "stage", Title = "Стейдж", SettingsXmlPath = "/mnt/stage/settings/settings.xml" };
        var ctx = new MigrationContext
        {
            Request = new MigrationRequest { Mode = MigrationMode.Selective, CopyServerSettings = true },
            Config = new MigrationConfig(),
            SourceEnv = source,
            TargetEnv = target,
            Options = new ServiceOptions(),
            SourceDb = sourceDb ?? Db(),
            TargetDb = targetDb,
            SourceSettings = sourceSettings ?? (sourceBehavior is null ? null : new ServerDatabaseSettings("pilot-ice_ru", DbId, null, Array.Empty<ServerArchiveSettings>(), sourceBehavior)),
        };
        MigrationContext.CheckServerSettingsCopy(ctx, checks);
    }

    private static string WriteSettingsXml(string xml)
    {
        var path = Path.Combine(Path.GetTempPath(), $"migration-settings-{Guid.NewGuid():N}.xml");
        System.IO.File.WriteAllText(path, xml);
        return path;
    }
}
