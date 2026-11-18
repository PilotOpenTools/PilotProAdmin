namespace ReportMetrics.Collector;

/// <summary>
/// Событие клиентского модуля Pilot, как его присылает Pilot.ClientSdk (ModuleStats): загрузка модуля
/// или действие пользователя. Имена полей в JSON - snake_case.
/// </summary>
public sealed class ModuleEventDto
{
    public Guid EventId { get; set; }
    public DateTimeOffset? Ts { get; set; }
    public string? Kind { get; set; }
    public string? Module { get; set; }
    public string? ModuleTitle { get; set; }
    public string? ModuleVersion { get; set; }
    public string? Action { get; set; }
    public string? Status { get; set; }
    public long? DurationMs { get; set; }
    public long? Items { get; set; }
    public string? ErrorType { get; set; }
    public string? ErrorMessage { get; set; }
    public string? Login { get; set; }
    public string? Person { get; set; }
    public string? Server { get; set; }
    public string? ClientType { get; set; }
    public string? ClientVersion { get; set; }
    public string? Machine { get; set; }
    public string? LibVersion { get; set; }
}

/// <summary>Нормализованная запись для metrics.module_events.</summary>
public sealed record ModuleEventRow(
    Guid EventId,
    string Env,
    DateTimeOffset Ts,
    string Kind,
    string Module,
    string ModuleTitle,
    string? ModuleVersion,
    string? Action,
    string Status,
    long? DurationMs,
    long? Items,
    string? ErrorType,
    string? ErrorMessage,
    string? Login,
    string? Person,
    string? Server,
    string? ClientType,
    string? ClientVersion,
    string? Machine,
    string? LibVersion);

public static class ModuleEventKinds
{
    /// <summary>Модуль загружен клиентом - версия модуля на рабочем месте.</summary>
    public const string Load = "load";

    /// <summary>Действие пользователя.</summary>
    public const string Action = "action";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal) { Load, Action };
}

public static class ModuleEventStatus
{
    public const string Success = "success";
    public const string Error = "error";
    public const string Cancelled = "cancelled";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal) { Success, Error, Cancelled };
}
