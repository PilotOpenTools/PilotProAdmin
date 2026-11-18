using System.Globalization;
using System.Text;
using System.Xml.Linq;
using PilotMigration.Config;

namespace PilotMigration.Pilot;

/// <summary>
/// Перенос настроек сервера Pilot (опция «Настройки сервера»). Админка хранит их в
/// C:/Ascon-Docker/&lt;контур&gt;/server-settings/server-settings.conf, wait-for-postgres.sh применяет к settings.xml
/// при перезапуске контейнера. Каталог должен совпадать с каталогом в админке (Servers/ServerSettingCatalog.cs).
///
/// Копируются только параметры поведения сервера - берутся фактические значения из settings.xml источника.
/// Логирование и отладка свои у каждого контура (на проде логирование автоматизаций выключено, на тесте и стейдже
/// включено) - они не копируются никогда, иначе перенос с прода выключал бы сбор статистики на тесте.
/// </summary>
public static class ServerSettingsCopy
{
    public const string ConfFile = "server-settings.conf";

    /// <summary>Копируются при отмеченной опции.</summary>
    public static readonly IReadOnlyList<(string Key, string Title)> BehaviorKeys = new[]
    {
        ("CheckChangeCreator", "проверять автора изменений"),
        ("AreParallelChangesDisabled", "запретить параллельные изменения"),
        ("IsSnapshotEnabled", "снимки"),
    };

    /// <summary>Свои у каждого контура - не копируются.</summary>
    public static readonly IReadOnlyList<(string Key, string Title)> ContourKeys = new[]
    {
        ("IsAutomationLoggingEnabled", "логирование автоматизаций"),
        ("IsExtendedLoggingEnabled", "расширенное логирование"),
        ("IsFileReadJournalingEnabled", "журнал чтения файлов"),
        ("EnableCSharpScriptDebug", "отладка C#-скриптов"),
    };

    private static readonly string[] AllKeys = ContourKeys.Concat(BehaviorKeys).Select(k => k.Key).ToArray();

    public static string ContourKeysText => string.Join(", ", ContourKeys.Select(k => k.Title));

    public static string ContainerName(EnvConfig env) => "pilot-server-" + env.Key;

    /// <summary>
    /// Каталог server-settings контура в контейнере миграции: рядом с папкой settings.xml (/mnt/test/settings / /mnt/test/server-settings).
    /// В кластере путь к settings.xml не используется - каталог всегда /mnt/&lt;контур&gt;/server-settings.
    /// </summary>
    public static string? DirectoryFor(EnvConfig env, bool cluster = false)
    {
        if (cluster)
            return $"/mnt/{env.Key}/server-settings";
        if (string.IsNullOrWhiteSpace(env.SettingsXmlPath))
            return null;
        var settingsDir = Path.GetDirectoryName(env.SettingsXmlPath.TrimEnd('/', '\\'));
        var contourDir = settingsDir is null ? null : Path.GetDirectoryName(settingsDir);
        return contourDir is null ? null : Path.Combine(contourDir, "server-settings").Replace('\\', '/');
    }

    /// <summary>Фактические значения параметров поведения в настройках сервера (settings.xml или config.settings кластера).</summary>
    internal static Dictionary<string, bool> ReadBehavior(XDocument doc)
    {
        var result = new Dictionary<string, bool>(StringComparer.Ordinal);
        var root = doc.Root;
        if (root is null)
            return result;
        foreach (var (key, _) in BehaviorKeys)
        {
            var element = root.Elements().FirstOrDefault(e => e.Name.LocalName == key);
            if (element is not null && bool.TryParse(element.Value.Trim(), out var value))
                result[key] = value;
        }
        return result;
    }

    /// <summary>
    /// Записывает параметры поведения источника в server-settings.conf приёмника; остальные строки приёмника
    /// (логирование и т. п.) сохраняются. Возвращает изменённые параметры «ключ: было / стало».
    /// </summary>
    public static List<string> Apply(IReadOnlyDictionary<string, bool> values, EnvConfig target, bool targetIsCluster, string updatedBy)
    {
        var dir = DirectoryFor(target, targetIsCluster);
        if (dir is null || !Directory.Exists(dir))
            throw new MigrationException($"Каталог настроек сервера приёмника не смонтирован в сервис миграции ({dir ?? "путь к settings.xml не задан"}).");

        var path = Path.Combine(dir, ConfFile);
        var existing = File.Exists(path) ? File.ReadAllLines(path) : Array.Empty<string>();
        var (text, changes) = Merge(existing, values, target.Key, updatedBy, DateTimeOffset.Now);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, text, new UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
        return changes;
    }

    internal static (string Text, List<string> Changes) Merge(
        IEnumerable<string> existingLines, IReadOnlyDictionary<string, bool> behavior, string targetKey, string updatedBy, DateTimeOffset at)
    {
        var current = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var raw in existingLines)
        {
            var line = raw.Trim();
            var hash = line.IndexOf('#');
            if (hash >= 0)
                line = line[..hash];
            var eq = line.IndexOf('=');
            if (eq <= 0)
                continue;
            var key = line[..eq].Trim();
            if (AllKeys.Contains(key) && bool.TryParse(line[(eq + 1)..].Trim(), out var value))
                current[key] = value;
        }

        var changes = new List<string>();
        foreach (var (key, value) in behavior)
        {
            if (current.TryGetValue(key, out var old) && old == value)
                continue;
            changes.Add($"{key}: {(current.TryGetValue(key, out var o) ? o.ToString().ToLowerInvariant() : "не задано")} / {value.ToString().ToLowerInvariant()}");
            current[key] = value;
        }

        var sb = new StringBuilder();
        sb.Append($"# Настройки сервера Pilot (контур {targetKey}). Правятся в админке / «Серверы»; ")
          .Append($"применяются при перезапуске контейнера pilot-server-{targetKey} (wait-for-postgres.sh).\n");
        sb.Append($"# Изменено: {at.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture)}, {updatedBy.Replace(",", " ").Replace("\n", " ").Trim()}\n");
        foreach (var key in AllKeys)
            if (current.TryGetValue(key, out var value))
                sb.Append(key).Append('=').Append(value ? "true" : "false").Append('\n');
        return (sb.ToString(), changes);
    }
}
