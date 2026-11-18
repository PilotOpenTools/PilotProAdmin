using PilotMigration.Archives;
using PilotMigration.Config;
using PilotMigration.Database;
using PilotMigration.Engine;
using PilotMigration.Objects;

namespace PilotMigration.Tests;

public class SecurityTests
{
    private static MigrationConfig Saved() => new()
    {
        Environments =
        {
            new EnvConfig
            {
                Key = "test", PilotUrl = "http://pilot-server-test:5545/db", PilotAdminLogin = "admin", PilotAdminPassword = "secret-password",
                S3 = { new S3Endpoint { ServerUrl = "http://rustfs-test:9000", Url = "http://rustfs-test:9000", AccessKey = "ak", SecretKey = "s3-secret" } },
            },
        },
    };

    private static MigrationConfig Incoming(Action<EnvConfig> change)
    {
        var config = Saved().Masked();
        change(config.Environments[0]);
        config.RestoreSecretsFrom(Saved());
        return config;
    }

    [Fact]
    public void MaskedSecretsAreRestoredWhenTargetIsUnchanged()
    {
        var env = Incoming(_ => { }).Environments[0];
        Assert.Equal("secret-password", env.PilotAdminPassword);
        Assert.Equal("s3-secret", env.S3[0].SecretKey);
    }

    [Fact]
    public void TrailingSlashAndCaseDoNotCountAsAnotherAddress()
    {
        var env = Incoming(e => e.PilotUrl = "HTTP://pilot-server-test:5545/db/").Environments[0];
        Assert.Equal("secret-password", env.PilotAdminPassword);
    }

    [Fact]
    public void PilotPasswordIsNotRestoredForAnotherUrl()
    {
        var env = Incoming(e => e.PilotUrl = "http://attacker.example:5545/db").Environments[0];
        Assert.Equal("", env.PilotAdminPassword);
    }

    [Fact]
    public void PilotPasswordIsNotRestoredForAnotherLogin()
    {
        var env = Incoming(e => e.PilotAdminLogin = "other").Environments[0];
        Assert.Equal("", env.PilotAdminPassword);
    }

    [Fact]
    public void S3SecretIsNotRestoredWhenRealUrlChanges()
    {
        var env = Incoming(e => e.S3[0].Url = "http://attacker.example:9000").Environments[0];
        Assert.Equal("", env.S3[0].SecretKey);
    }

    [Fact]
    public void NewPasswordIsKeptAsIs()
    {
        var env = Incoming(e => { e.PilotUrl = "http://other:5545/db"; e.PilotAdminPassword = "typed-now"; }).Environments[0];
        Assert.Equal("typed-now", env.PilotAdminPassword);
    }

    [Fact]
    public void ConfigStoreDoesNotKeepPasswordForChangedUrl()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var store = new ConfigStore(new ServiceOptions { DataDirectory = dir });
            var first = Saved();
            first.Normalize();
            store.Save(first, "test");
            Assert.Equal("secret-password", store.Current.Find("test")!.PilotAdminPassword);

            var masked = store.Current.Masked();
            masked.Find("test")!.PilotUrl = "http://attacker.example:5545/db";
            store.Save(masked, "test");

            Assert.Equal("", store.Current.Find("test")!.PilotAdminPassword);
            Assert.DoesNotContain("secret-password", File.ReadAllText(Path.Combine(dir, "config.json")));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ConfigFileIsReadableOnlyByOwner()
    {
        if (OperatingSystem.IsWindows())
            return; // Права Unix: проверяется в Linux-контейнере CI.
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var store = new ConfigStore(new ServiceOptions { DataDirectory = dir });
            store.Save(Saved(), "test");
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(dir, "config.json")));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Theory]
    [InlineData("admin", "admin", true)]
    [InlineData("admin", "short", true)]
    [InlineData("admin", "long-enough-password", false)]
    [InlineData("admin", "", false)]
    public void WeakAdminPasswordIsDetected(string login, string password, bool weak) =>
        Assert.Equal(weak, new EnvConfig { PilotAdminLogin = login, PilotAdminPassword = password }.HasWeakAdminPassword);

    [Fact]
    public void ThrottleBlocksAfterLimitAndReleasesAfterWindow()
    {
        var now = DateTimeOffset.Parse("2026-01-01T10:00:00Z");
        var throttle = new AuthThrottle(10, TimeSpan.FromMinutes(5), () => now);
        for (var i = 0; i < 9; i++)
            throttle.Failure("1.1.1.1");
        Assert.Null(throttle.BlockedFor("1.1.1.1"));

        throttle.Failure("1.1.1.1");
        Assert.NotNull(throttle.BlockedFor("1.1.1.1"));
        Assert.Null(throttle.BlockedFor("2.2.2.2"));

        now += TimeSpan.FromMinutes(5);
        Assert.Null(throttle.BlockedFor("1.1.1.1"));
    }

    [Fact]
    public void ThrottleCountsOnlyFailuresInsideWindow()
    {
        var now = DateTimeOffset.Parse("2026-01-01T10:00:00Z");
        var throttle = new AuthThrottle(3, TimeSpan.FromMinutes(5), () => now);
        throttle.Failure("a");
        throttle.Failure("a");
        now += TimeSpan.FromMinutes(6);
        throttle.Failure("a");
        Assert.Null(throttle.BlockedFor("a"));
    }

    [Theory]
    [InlineData("key", "key", true)]
    [InlineData("key", "other", false)]
    [InlineData("", "key", false)]
    [InlineData("key-longer", "key", false)]
    public void KeyComparison(string actual, string expected, bool equal) =>
        Assert.Equal(equal, ApiSecurity.KeyEquals(actual, expected));

    [Fact]
    public void UserHeaderIsCleaned()
    {
        Assert.Equal("неизвестно", ApiSecurity.CleanUser(""));
        Assert.Equal("Иван Петров", ApiSecurity.CleanUser(Uri.EscapeDataString("Иван Петров")));
        Assert.Equal("adminfake", ApiSecurity.CleanUser("admin%0D%0Afake"));
        Assert.Equal(100, ApiSecurity.CleanUser(new string('x', 500)).Length);
    }

    [Fact]
    public void ProdAliasIsBlockedForNonProdTargets()
    {
        var prod = new EnvConfig
        {
            Key = "prod", Title = "Прод", PilotUrl = "http://pilot-server-prod:5545/db", SettingsXmlPath = "/mnt/prod/settings/settings.xml",
            PathMappings = { new PathMapping { ServerPrefix = "/usr/share/ascon/databases", LocalPath = "/mnt/prod/databases" } },
        };
        var config = new MigrationConfig { Environments = { prod } };

        Assert.Null(MigrationContext.ProdAliasError(config, new EnvConfig { Key = "test", Title = "Тест", PilotUrl = "http://pilot-server-test:5545/db" }));
        Assert.Null(MigrationContext.ProdAliasError(config, prod));
        Assert.Contains("адрес", MigrationContext.ProdAliasError(config, new EnvConfig { Key = "test", Title = "Тест", PilotUrl = "http://PILOT-server-prod:5545/db/" })!);
        Assert.Contains("settings.xml", MigrationContext.ProdAliasError(config, new EnvConfig { Key = "test", Title = "Тест", PilotUrl = "http://x:1/db", SettingsXmlPath = "/mnt/prod/settings/settings.xml" })!);
        Assert.Contains("архива", MigrationContext.ProdAliasError(config, new EnvConfig
        {
            Key = "stage", Title = "Стейдж", PilotUrl = "http://x:1/db",
            PathMappings = { new PathMapping { ServerPrefix = "/a", LocalPath = "/mnt/prod/databases/" } },
        })!);
    }

    [Theory]
    [InlineData("http://pilot-server-prod:5545/db", "https://PILOT-server-prod:5545/db/")]
    [InlineData("http://pilot-server-prod/db", "http://pilot-server-prod:80/db")]
    public void ProdAliasNormalizesAddresses(string prodUrl, string targetUrl)
    {
        var config = new MigrationConfig { Environments = { new EnvConfig { Key = "prod", Title = "Прод", PilotUrl = prodUrl } } };
        Assert.Contains("адрес", MigrationContext.ProdAliasError(config, new EnvConfig { Key = "test", Title = "Тест", PilotUrl = targetUrl })!);
    }

    [Fact]
    public void ProdAliasNormalizesPathsAndS3()
    {
        var prod = new EnvConfig
        {
            Key = "prod", Title = "Прод", PilotUrl = "http://pilot-server-prod:5545/db", SettingsXmlPath = "/mnt/prod/settings/settings.xml",
            PathMappings = { new PathMapping { ServerPrefix = "/a", LocalPath = "/mnt/prod/databases" } },
            S3 = { new S3Endpoint { ServerUrl = "http://rustfs-prod:9000" } },
        };
        var config = new MigrationConfig { Environments = { prod } };
        EnvConfig Target() => new() { Key = "test", Title = "Тест", PilotUrl = "http://x:1/db" };

        var t = Target();
        t.SettingsXmlPath = "/mnt/prod/settings/./settings.xml";
        Assert.Contains("settings.xml", MigrationContext.ProdAliasError(config, t)!);

        t = Target();
        t.PathMappings.Add(new PathMapping { ServerPrefix = "/a", LocalPath = "/mnt/prod/databases/../databases" });
        Assert.Contains("архива", MigrationContext.ProdAliasError(config, t)!);

        t = Target();
        t.S3.Add(new S3Endpoint { ServerUrl = "http://RUSTFS-prod:9000/" });
        Assert.Contains("S3", MigrationContext.ProdAliasError(config, t)!);

        t = Target();
        t.S3.Add(new S3Endpoint { ServerUrl = "http://rustfs-test:9000" });
        Assert.Null(MigrationContext.ProdAliasError(config, t));
    }

    [Fact]
    public void ConfigStoreRestrictsPermissionsOfCopiesAndStaleTmp()
    {
        if (OperatingSystem.IsWindows())
            return; // права Unix
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var tmp = Path.Combine(dir, "config.json.tmp");
            File.WriteAllText(tmp, "old");
            File.SetUnixFileMode(tmp, (UnixFileMode)0b110_100_100);
            File.WriteAllText(Path.Combine(dir, "config.json"), "{ broken");
            _ = new ConfigStore(new ServiceOptions { DataDirectory = dir });
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(dir, "config.json")));
            foreach (var f in Directory.GetFiles(dir, "config.json.broken-*"))
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(f));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void ProtoWireRejectsFieldLongerThanData()
    {
        // Поле 1, тип «длина», длина 0xFFFFFFFF: после приведения к int была бы -1 и цикл не завершался бы.
        byte[] negative = { 0x0A, 0xFF, 0xFF, 0xFF, 0xFF, 0x0F };
        Assert.Throws<FormatException>(() => ProtoWire.ReadFields(negative));

        byte[] tooLong = { 0x0A, 0x05, 0x01, 0x02 };
        Assert.Throws<FormatException>(() => ProtoWire.ReadFields(tooLong));

        byte[] ok = { 0x0A, 0x02, 0x01, 0x02 };
        Assert.Single(ProtoWire.ReadFields(ok));
    }

    [Theory]
    [InlineData("http://rustfs-test:9000", true)]
    [InlineData("https://s3.example.local", true)]
    [InlineData("file:///etc/passwd", false)]
    [InlineData("gopher://x", false)]
    [InlineData("rustfs-test:9000", false)]
    [InlineData("", false)]
    public void S3UrlMustBeHttp(string url, bool valid) => Assert.Equal(valid, S3Endpoint.UrlError(url) is null);

    [Fact]
    public void FileSourceArchiveWithNonHttpS3UrlIsUnusable()
    {
        var id = Guid.NewGuid();
        var source = new FileSourceOptions { Archives = { new FileSourceArchive { Id = id, S3Url = "file:///etc", S3Bucket = "b", AccessKey = "a", SecretKey = "s" } } };
        var resolved = ArchiveResolver.ResolveFileSource(new[] { new DbpArchive(id, "main", true, "") }, source, Path.GetTempPath(), Path.GetTempPath());
        var archive = Assert.Single(resolved);
        Assert.Null(archive.Store);
        Assert.Contains("http", archive.Problem);
    }
}
