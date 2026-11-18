using System.Text.Json.Serialization;

namespace PilotMigration.Config;

/// <summary>
/// Подключения сервиса миграции к контурам (data/config.json). Правится со страницы «Миграция / Подключения» в админке.
/// Пароли наружу не отдаются (см. <see cref="MigrationConfig.Masked"/>).
/// </summary>
public sealed class MigrationConfig
{
    public const string Test = "test";
    public const string Stage = "stage";
    public const string Prod = "prod";

    /// <summary>Значение, которое API отдаёт вместо пароля. Если оно пришло обратно - пароль не меняется.</summary>
    public const string SecretMask = "••••••";

    public List<EnvConfig> Environments { get; set; } = new();

    /// <summary>Сколько последних запусков хранить в data/runs.</summary>
    public int KeepRuns { get; set; } = 200;

    /// <summary>Сколько ждать смены состояния базы Pilot (остановка / запуск), минут.</summary>
    public int DatabaseStateTimeoutMinutes { get; set; } = 10;

    public DateTimeOffset? UpdatedAt { get; set; }
    public string? UpdatedBy { get; set; }

    public EnvConfig? Find(string? key) =>
        Environments.FirstOrDefault(e => string.Equals(e.Key, key, StringComparison.OrdinalIgnoreCase));

    /// <summary>Три контура в фиксированном порядке.</summary>
    public void Normalize()
    {
        var titles = new[] { (Test, "Тест"), (Stage, "Стейдж"), (Prod, "Прод") };
        var result = new List<EnvConfig>();
        foreach (var (key, title) in titles)
        {
            var env = Find(key) ?? new EnvConfig();
            env.Key = key;
            env.Title = title;
            env.Normalize();
            result.Add(env);
        }
        Environments = result;
        KeepRuns = Math.Clamp(KeepRuns, 10, 5000);
        DatabaseStateTimeoutMinutes = Math.Clamp(DatabaseStateTimeoutMinutes, 1, 120);
    }

    /// <summary>Копия без паролей - для отдачи наружу.</summary>
    public MigrationConfig Masked()
    {
        var copy = Clone();
        foreach (var env in copy.Environments)
        {
            env.PilotAdminPassword = Mask(env.PilotAdminPassword);
            foreach (var s3 in env.S3)
                s3.SecretKey = Mask(s3.SecretKey);
        }
        return copy;
    }

    /// <summary>
    /// Пароли, пришедшие как маска, берутся из текущей конфигурации, но только если адрес, к которому они
    /// относятся, не изменился: иначе сохранённый пароль можно было бы отправить на чужой адрес.
    /// </summary>
    public void RestoreSecretsFrom(MigrationConfig current)
    {
        foreach (var env in Environments)
        {
            var old = current.Find(env.Key);
            if (env.PilotAdminPassword == SecretMask)
            {
                var sameTarget = old is not null && S3Endpoint.SameServerUrl(old.PilotUrl, env.PilotUrl) &&
                                 string.Equals((old.PilotAdminLogin ?? "").Trim(), (env.PilotAdminLogin ?? "").Trim(), StringComparison.Ordinal);
                env.PilotAdminPassword = sameTarget ? old!.PilotAdminPassword : "";
            }
            foreach (var s3 in env.S3)
            {
                if (s3.SecretKey != SecretMask)
                    continue;
                var oldS3 = old?.S3.FirstOrDefault(o => S3Endpoint.SameServerUrl(o.ServerUrl, s3.ServerUrl));
                var sameUrl = oldS3 is not null && S3Endpoint.SameServerUrl(oldS3.EffectiveUrl, s3.EffectiveUrl);
                s3.SecretKey = sameUrl ? oldS3!.SecretKey : "";
            }
        }
    }

    public MigrationConfig Clone() =>
        System.Text.Json.JsonSerializer.Deserialize<MigrationConfig>(System.Text.Json.JsonSerializer.Serialize(this))!;

    private static string Mask(string? value) => string.IsNullOrEmpty(value) ? "" : SecretMask;
}

public sealed class EnvConfig
{
    public string Key { get; set; } = "";
    public string Title { get; set; } = "";

    /// <summary>Адрес сервера Pilot с базой, как его видит сервис: http://pilot-server-test:5545/pilot-ice_ru.</summary>
    public string PilotUrl { get; set; } = "";

    /// <summary>Администратор сервера Pilot: нужен, чтобы остановить и запустить базу, прочитать метаданные.</summary>
    public string PilotAdminLogin { get; set; } = "";
    public string PilotAdminPassword { get; set; } = "";

    /// <summary>
    /// settings.xml сервера Pilot, смонтированный в контейнер (только чтение). Из него берутся
    /// файловые архивы (папки, S3-адреса, бакеты) - админский API Pilot адрес S3 не отдаёт.
    /// </summary>
    public string SettingsXmlPath { get; set; } = "";

    /// <summary>Как пути сервера Pilot видны сервису: /usr/share/ascon/databases / /mnt/test/databases.</summary>
    public List<PathMapping> PathMappings { get; set; } = new();

    /// <summary>Доступ к S3-хранилищам архивов: ключ - адрес из settings.xml (Url).</summary>
    public List<S3Endpoint> S3 { get; set; } = new();

    [JsonIgnore]
    public bool IsConfigured => !string.IsNullOrWhiteSpace(PilotUrl);

    /// <summary>Пароль администратора Pilot очевидно слабый: короче 8 символов или равен логину.</summary>
    [JsonIgnore]
    public bool HasWeakAdminPassword =>
        !string.IsNullOrEmpty(PilotAdminPassword) &&
        (PilotAdminPassword.Length < 8 || string.Equals(PilotAdminPassword, PilotAdminLogin, StringComparison.OrdinalIgnoreCase));

    public void Normalize()
    {
        PilotUrl = (PilotUrl ?? "").Trim().TrimEnd('/');
        PilotAdminLogin = (PilotAdminLogin ?? "").Trim();
        PilotAdminPassword ??= "";
        SettingsXmlPath = (SettingsXmlPath ?? "").Trim();
        PathMappings = (PathMappings ?? new())
            .Select(m => new PathMapping { ServerPrefix = (m.ServerPrefix ?? "").Trim(), LocalPath = (m.LocalPath ?? "").Trim() })
            .Where(m => m.ServerPrefix.Length > 0 && m.LocalPath.Length > 0)
            .ToList();
        S3 = (S3 ?? new())
            .Select(s => new S3Endpoint
            {
                ServerUrl = (s.ServerUrl ?? "").Trim().TrimEnd('/'),
                Url = (s.Url ?? "").Trim().TrimEnd('/'),
                AccessKey = (s.AccessKey ?? "").Trim(),
                SecretKey = s.SecretKey ?? "",
            })
            .Where(s => s.ServerUrl.Length > 0)
            .ToList();
    }

    /// <summary>Путь сервера Pilot / путь в контейнере сервиса; null - соответствие не задано.</summary>
    public string? MapPath(string serverPath)
    {
        if (string.IsNullOrWhiteSpace(serverPath))
            return null;
        var normalized = serverPath.Replace('\\', '/').TrimEnd('/');
        foreach (var m in PathMappings.OrderByDescending(m => m.ServerPrefix.Length))
        {
            var prefix = m.ServerPrefix.Replace('\\', '/').TrimEnd('/');
            if (normalized.Equals(prefix, StringComparison.OrdinalIgnoreCase))
                return m.LocalPath.TrimEnd('/');
            if (normalized.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase))
                return m.LocalPath.TrimEnd('/') + normalized[prefix.Length..];
        }
        return null;
    }

    public S3Endpoint? FindS3(string serverUrl) =>
        S3.FirstOrDefault(s => S3Endpoint.SameServerUrl(s.ServerUrl, serverUrl));
}

public sealed class PathMapping
{
    public string ServerPrefix { get; set; } = "";
    public string LocalPath { get; set; } = "";
}

public sealed class S3Endpoint
{
    /// <summary>Адрес хранилища, как он записан в settings.xml сервера Pilot (атрибут Url).</summary>
    public string ServerUrl { get; set; } = "";

    /// <summary>Адрес, по которому хранилище доступно сервису. Пусто - тот же, что <see cref="ServerUrl"/>.</summary>
    public string Url { get; set; } = "";

    public string AccessKey { get; set; } = "";
    public string SecretKey { get; set; } = "";

    [JsonIgnore]
    public string EffectiveUrl => string.IsNullOrWhiteSpace(Url) ? ServerUrl : Url;

    /// <summary>Адрес S3 должен быть абсолютным http(s): другие схемы (file, gopher и т. п.) сервис не открывает.</summary>
    public static string? UrlError(string? url) =>
        Uri.TryCreate((url ?? "").Trim(), UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            ? null
            : $"адрес S3 «{url}» должен начинаться с http:// или https://";

    public static bool SameServerUrl(string? a, string? b) =>
        string.Equals((a ?? "").Trim().TrimEnd('/'), (b ?? "").Trim().TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
}
