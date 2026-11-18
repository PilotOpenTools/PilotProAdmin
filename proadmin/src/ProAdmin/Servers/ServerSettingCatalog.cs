namespace ProAdmin.Servers;

/// <summary>Как параметр ведёт себя при переносе данных между контурами.</summary>
public enum ServerSettingScope
{
    /// <summary>Свой у каждого контура (логирование, отладка) - миграция никогда не копирует.</summary>
    Contour,

    /// <summary>Поведение сервера - миграция копирует, если отмечено «Настройки сервера».</summary>
    Behavior,
}

/// <summary>
/// Параметр settings.xml сервера Pilot, которым управляет админка. Список совпадает с SERVER_SETTINGS_KEYS
/// в wait-for-postgres.sh (stage/, test/, prod/ и Install/) и с каталогом в сервисе миграции - меняйте все три.
/// </summary>
public sealed record ServerSettingDefinition(
    string Key,
    string Title,
    string Description,
    ServerSettingScope Scope,
    bool? RecommendedProd = null,
    bool? RecommendedOther = null)
{
    public bool? Recommended(string env) => env == Settings.AdminSettings.Prod ? RecommendedProd : RecommendedOther;
}

public static class ServerSettingCatalog
{
    public static readonly IReadOnlyList<ServerSettingDefinition> All = new[]
    {
        new ServerSettingDefinition("IsAutomationLoggingEnabled", "Логирование автоматизаций",
            "automation.log: запуски сценариев, пропуски по условиям, ошибки. Из него собирается статистика автоматизаций в Grafana. " +
            "На проде выключено - лог быстро растёт, статистика прода не собирается.",
            ServerSettingScope.Contour, RecommendedProd: false, RecommendedOther: true),
        new ServerSettingDefinition("IsExtendedLoggingEnabled", "Расширенное логирование сервера",
            "Подробный журнал Pilot-Server для диагностики. Заметно увеличивает объём логов.",
            ServerSettingScope.Contour),
        new ServerSettingDefinition("IsFileReadJournalingEnabled", "Журнал чтения файлов",
            "Записывать в журнал действий пользователей открытие и скачивание файлов.",
            ServerSettingScope.Contour),
        new ServerSettingDefinition("EnableCSharpScriptDebug", "Отладка C#-скриптов",
            "Отладочная сборка C#-кода автоматизаций. Только для отладки, на проде не включать.",
            ServerSettingScope.Contour, RecommendedProd: false),
        new ServerSettingDefinition("CheckChangeCreator", "Проверять автора изменений",
            "Параметр CheckChangeCreator: сервер сверяет автора изменения с пользователем подключения.",
            ServerSettingScope.Behavior),
        new ServerSettingDefinition("AreParallelChangesDisabled", "Запретить параллельные изменения",
            "Параметр AreParallelChangesDisabled: изменения объектов применяются строго по очереди.",
            ServerSettingScope.Behavior),
        new ServerSettingDefinition("IsSnapshotEnabled", "Снимки (IsSnapshotEnabled)",
            "Параметр IsSnapshotEnabled сервера Pilot - см. документацию Pilot-Server.",
            ServerSettingScope.Behavior),
    };

    public static ServerSettingDefinition? Find(string key) =>
        All.FirstOrDefault(d => string.Equals(d.Key, key, StringComparison.Ordinal));
}
