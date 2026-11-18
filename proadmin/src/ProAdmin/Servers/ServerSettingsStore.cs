using System.Globalization;
using System.Text;
using ProAdmin.Infrastructure;
using Microsoft.Extensions.Options;

namespace ProAdmin.Servers;

/// <summary>Состояние настроек сервера контура: что задано в админке и что сервер применил при последнем старте.</summary>
public sealed class ServerSettingsState
{
    public required string Env { get; init; }
    public required string Directory { get; init; }

    /// <summary>Каталог контура смонтирован в админку.</summary>
    public bool Available { get; init; }

    /// <summary>Задано в server-settings.conf. Параметра нет - админка им не управляет, у сервера своё значение.</summary>
    public IReadOnlyDictionary<string, bool> Desired { get; init; } = new Dictionary<string, bool>();

    /// <summary>Фактические значения при последнем старте контейнера (applied.conf): settings.xml у монолита, конфигурация кластера у узла; null - файла нет.</summary>
    public IReadOnlyDictionary<string, string>? Applied { get; init; }

    public DateTimeOffset? AppliedAt { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }
    public string? UpdatedBy { get; init; }

    /// <summary>Параметры, у которых заданное значение ещё не применено сервером.</summary>
    public IReadOnlyList<string> PendingKeys =>
        Desired.Where(d => AppliedValue(d.Key) != d.Value).Select(d => d.Key).ToList();

    public bool RestartRequired => PendingKeys.Count > 0;

    /// <summary>Контур - кластер: так отметил себя узел при старте (Mode=cluster в applied.conf). До первого старта через обёртку режим неизвестен.</summary>
    public bool IsCluster => Applied is not null && Applied.TryGetValue(ServerSettingsStore.ModeKey, out var mode) && mode == "cluster";

    public bool? AppliedValue(string key) =>
        Applied is not null && Applied.TryGetValue(key, out var v) && bool.TryParse(v, out var b) ? b : null;
}

/// <summary>
/// Файлы настроек серверов Pilot. У каждого контура каталог C:/Ascon-Docker/&lt;контур&gt;/server-settings,
/// смонтированный в админку как {Root}/&lt;контур&gt; и в pilot-server как /server-settings.
/// Админка пишет server-settings.conf, а wait-for-postgres.sh при старте контейнера переносит его в settings.xml
/// и записывает applied.conf. Сам сервер админка не перезапускает (доступ к Docker ей не даётся).
/// </summary>
public sealed class ServerSettingsStore
{
    public const string ConfFile = "server-settings.conf";
    public const string AppliedFile = "applied.conf";
    public const string ModeKey = "Mode";

    /// <summary>Что делать, чтобы настройки вступили в силу в кластере: конфигурация общая, а работающий узел держит её в памяти.</summary>
    public const string ClusterRestartHint =
        "Перезапустите все узлы сервера Pilot контура: остановите все, затем запустите. " +
        "Пока не перезапущены все узлы, остановка или запуск базы может вернуть прежние значения.";
    private const string UpdatedPrefix = "# Изменено:";

    private readonly string? _root;
    private readonly object _lock = new();

    public ServerSettingsStore(IOptions<AppOptions> options)
    {
        var root = options.Value.ServerSettings.Root;
        _root = string.IsNullOrWhiteSpace(root) ? null : root.Trim();
    }

    public bool IsConfigured => _root is not null;

    public string? Root => _root;

    /// <summary>Имя контейнера сервера Pilot контура - для подсказки «перезапустите контейнер». Только монолит: у узлов кластера свои имена.</summary>
    public static string ContainerName(string env) => "pilot-server-" + env;

    public ServerSettingsState Read(string env)
    {
        var dir = DirectoryFor(env);
        if (dir is null || !System.IO.Directory.Exists(dir))
            return new ServerSettingsState { Env = env, Directory = dir ?? "", Available = false };

        var confPath = Path.Combine(dir, ConfFile);
        var appliedPath = Path.Combine(dir, AppliedFile);
        var desired = new Dictionary<string, bool>(StringComparer.Ordinal);
        DateTimeOffset? updatedAt = null;
        string? updatedBy = null;
        if (File.Exists(confPath))
        {
            foreach (var raw in File.ReadAllLines(confPath))
            {
                var line = raw.Trim();
                if (line.StartsWith(UpdatedPrefix, StringComparison.Ordinal))
                {
                    var parts = line[UpdatedPrefix.Length..].Split(',', 2, StringSplitOptions.TrimEntries);
                    if (DateTimeOffset.TryParse(parts[0], CultureInfo.InvariantCulture, DateTimeStyles.None, out var at))
                        updatedAt = at;
                    updatedBy = parts.Length > 1 ? parts[1] : null;
                    continue;
                }
                if (ParseLine(line) is { } kv && ServerSettingCatalog.Find(kv.Key) is not null && bool.TryParse(kv.Value, out var b))
                    desired[kv.Key] = b;
            }
        }

        Dictionary<string, string>? applied = null;
        DateTimeOffset? appliedAt = null;
        if (File.Exists(appliedPath))
        {
            applied = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var raw in File.ReadAllLines(appliedPath))
            {
                if (ParseLine(raw.Trim()) is not { } kv)
                    continue;
                if (kv.Key == "AppliedAt")
                {
                    if (DateTimeOffset.TryParse(kv.Value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var at))
                        appliedAt = at;
                }
                else
                {
                    applied[kv.Key] = kv.Value;
                }
            }
        }

        return new ServerSettingsState
        {
            Env = env,
            Directory = dir,
            Available = true,
            Desired = desired,
            Applied = applied,
            AppliedAt = appliedAt,
            UpdatedAt = updatedAt,
            UpdatedBy = updatedBy,
        };
    }

    /// <summary>Записывает server-settings.conf контура. Параметры вне каталога отбрасываются.</summary>
    public void Write(string env, IReadOnlyDictionary<string, bool> desired, string updatedBy)
    {
        var dir = DirectoryFor(env) ?? throw new InvalidOperationException("Каталог настроек серверов не задан (App__ServerSettings__Root).");
        if (!System.IO.Directory.Exists(dir))
            throw new InvalidOperationException($"Каталог {dir} не смонтирован в админку.");

        lock (_lock)
        {
            var path = Path.Combine(dir, ConfFile);
            var tmp = path + ".tmp";
            // LF и без BOM: файл читает bash в контейнере pilot-server.
            File.WriteAllText(tmp, Format(env, desired, DateTimeOffset.Now, updatedBy, Read(env).IsCluster), new UTF8Encoding(false));
            File.Move(tmp, path, overwrite: true);
        }
    }

    internal static string Format(string env, IReadOnlyDictionary<string, bool> desired, DateTimeOffset at, string updatedBy, bool cluster = false)
    {
        var sb = new StringBuilder();
        sb.Append($"# Настройки сервера Pilot (контур {env}). Правятся в админке / «Серверы»; ")
          .Append(cluster
              ? "применяются при перезапуске узлов кластера (wait-for-postgres.sh), перезапускайте все узлы.\n"
              : $"применяются при перезапуске контейнера {ContainerName(env)} (wait-for-postgres.sh).\n");
        sb.Append($"{UpdatedPrefix} {at.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture)}, {Sanitize(updatedBy)}\n");
        foreach (var def in ServerSettingCatalog.All)
            if (desired.TryGetValue(def.Key, out var value))
                sb.Append(def.Key).Append('=').Append(value ? "true" : "false").Append('\n');
        return sb.ToString();
    }

    private string? DirectoryFor(string env) =>
        _root is null || env.Any(c => !char.IsAsciiLetterLower(c)) ? null : Path.Combine(_root, env);

    private static KeyValuePair<string, string>? ParseLine(string line)
    {
        if (line.Length == 0 || line[0] == '#')
            return null;
        var hash = line.IndexOf('#');
        if (hash >= 0)
            line = line[..hash];
        var eq = line.IndexOf('=');
        if (eq <= 0)
            return null;
        return new(line[..eq].Trim(), line[(eq + 1)..].Trim());
    }

    private static string Sanitize(string value) =>
        new string(value.Where(c => !char.IsControl(c) && c != ',').ToArray()).Trim();
}
