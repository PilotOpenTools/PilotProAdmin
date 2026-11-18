using System.Text.Json;

namespace PilotMigration.Config;

/// <summary>data/config.json. При первом запуске создаётся из переменных окружения Defaults__*.</summary>
public sealed class ConfigStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _path;
    private readonly object _lock = new();
    private MigrationConfig _current;

    public ConfigStore(ServiceOptions options)
    {
        _path = Path.Combine(options.DataDirectory, "config.json");
        _current = Load() ?? CreateDefault(options);
        _current.Normalize();
        if (!File.Exists(_path))
            Write(_current);
        else
            RestrictAccess(_path);
    }

    public MigrationConfig Current
    {
        get { lock (_lock) return _current.Clone(); }
    }

    public void Save(MigrationConfig config, string? updatedBy)
    {
        lock (_lock)
        {
            config.RestoreSecretsFrom(_current);
            config.Normalize();
            config.UpdatedAt = DateTimeOffset.Now;
            config.UpdatedBy = updatedBy;
            Write(config);
            _current = config;
        }
    }

    private MigrationConfig? Load()
    {
        if (!File.Exists(_path))
            return null;
        try
        {
            return JsonSerializer.Deserialize<MigrationConfig>(File.ReadAllText(_path));
        }
        catch (JsonException)
        {
            var broken = _path + ".broken-" + DateTime.Now.ToString("yyyyMMddHHmmss");
            File.Copy(_path, broken, overwrite: true);
            RestrictAccess(broken);
            return null;
        }
    }

    private void Write(MigrationConfig config)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var tmp = _path + ".tmp";
        File.Delete(tmp); // остаток прежней версии мог иметь права 644, а UnixCreateMode действует только при создании
        // В файле пароли администраторов Pilot и ключи S3 открытым текстом: читать его может только владелец.
        var stream = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
            stream.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using (var writer = new StreamWriter(tmp, stream))
            writer.Write(JsonSerializer.Serialize(config, JsonOptions));
        File.Move(tmp, _path, overwrite: true);
    }

    /// <summary>Файл, созданный прежней версией, мог получить права по умолчанию (644): сужаем до владельца.</summary>
    internal static void RestrictAccess(string path)
    {
        if (OperatingSystem.IsWindows())
            return;
        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            Console.Error.WriteLine($"Не удалось ограничить права на {path}: {ex.Message}. Ограничьте доступ к каталогу данных вручную.");
        }
    }

    private static MigrationConfig CreateDefault(ServiceOptions options)
    {
        var config = new MigrationConfig();
        foreach (var d in options.Defaults)
        {
            config.Environments.Add(new EnvConfig
            {
                Key = d.Key,
                PilotUrl = d.PilotUrl ?? "",
                PilotAdminLogin = d.PilotAdminLogin ?? "",
                PilotAdminPassword = d.PilotAdminPassword ?? "",
                SettingsXmlPath = d.SettingsXmlPath ?? "",
                PathMappings = string.IsNullOrWhiteSpace(d.ArchiveLocalRoot)
                    ? new()
                    : new() { new PathMapping { ServerPrefix = d.ArchiveServerRoot ?? "/usr/share/ascon/databases", LocalPath = d.ArchiveLocalRoot! } },
                S3 = string.IsNullOrWhiteSpace(d.S3ServerUrl)
                    ? new()
                    : new() { new S3Endpoint { ServerUrl = d.S3ServerUrl!, Url = d.S3Url ?? "", AccessKey = d.S3AccessKey ?? "", SecretKey = d.S3SecretKey ?? "" } },
            });
        }
        config.UpdatedBy = "начальные настройки (переменные окружения)";
        config.UpdatedAt = DateTimeOffset.Now;
        return config;
    }
}

/// <summary>Параметры запуска (appsettings.json / переменные окружения).</summary>
public sealed class ServiceOptions
{
    public string DataDirectory { get; set; } = "data";

    /// <summary>Ключ, который админка передаёт в заголовке X-Api-Key. Пусто - сервис отвечает только на /health.</summary>
    public string ApiKey { get; set; } = "";

    /// <summary>
    /// Каталог, в подпапках которого лежат присланные файловые базы Pilot (base.dbp и соседние файлы).
    /// Смонтирован в контейнер только на чтение; импортировать можно только из него.
    /// </summary>
    public string ImportDirectory { get; set; } = "/mnt/import";

    /// <summary>Каталог выгрузок базы в файлы (папка на выгрузку). Смонтирован на запись.</summary>
    public string ExportDirectory { get; set; } = "/mnt/export";

    /// <summary>Начальные подключения: Defaults__0__Key=test, Defaults__0__PilotUrl=… Применяются, пока нет data/config.json.</summary>
    public List<EnvDefaults> Defaults { get; set; } = new();

    /// <summary>
    /// Пароль PostgreSQL (роль pilotuser) по ключу контура: Migration__PgPasswords__test=… В кластере в настройках сервера пароль
    /// зашифрован, а админский API его не отдаёт. Задаётся окружением, а не конфигурацией, чтобы PUT /api/config его не стирал.
    /// </summary>
    public Dictionary<string, string> PgPasswords { get; set; } = new();

    /// <summary>База конфигурации кластера Pilot-Server на том же сервере PostgreSQL, что и база Pilot.</summary>
    public string ConfigurationDatabase { get; set; } = "configurationdb";

    public string? PgPassword(string envKey) =>
        PgPasswords.FirstOrDefault(p => string.Equals(p.Key, envKey, StringComparison.OrdinalIgnoreCase)).Value is { Length: > 0 } v ? v : null;

    public static string PgPasswordVariable(string envKey) => $"Migration__PgPasswords__{envKey}";
}

public sealed class EnvDefaults
{
    public string Key { get; set; } = "";
    public string? PilotUrl { get; set; }
    public string? PilotAdminLogin { get; set; }
    public string? PilotAdminPassword { get; set; }
    public string? SettingsXmlPath { get; set; }
    public string? ArchiveServerRoot { get; set; }
    public string? ArchiveLocalRoot { get; set; }
    public string? S3ServerUrl { get; set; }
    public string? S3Url { get; set; }
    public string? S3AccessKey { get; set; }
    public string? S3SecretKey { get; set; }
}
