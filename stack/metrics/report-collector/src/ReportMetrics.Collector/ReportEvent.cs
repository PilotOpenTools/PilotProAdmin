using System.Text.Json.Serialization;

namespace ReportMetrics.Collector;

/// <summary>
/// Одно построение отчёта, как его присылает клиентский модуль Reports.
/// Имена полей в JSON - snake_case (см. JsonOptions в Program.cs).
/// </summary>
public sealed class ReportEventDto
{
    public Guid RunId { get; set; }
    public DateTimeOffset? Ts { get; set; }
    public string? Report { get; set; }
    public Guid? ReportId { get; set; }
    public string? Status { get; set; }
    public long? DurationMs { get; set; }
    public int? Pages { get; set; }
    public string? ErrorType { get; set; }
    public string? ErrorMessage { get; set; }
    public string? Login { get; set; }
    public string? Person { get; set; }
    public string? Server { get; set; }
    public string? ClientType { get; set; }
    public string? ClientVersion { get; set; }
    public string? ModuleVersion { get; set; }
    public string? Machine { get; set; }
}

/// <summary>Нормализованная запись, готовая к вставке в metrics.report_events.</summary>
public sealed record ReportEventRow(
    Guid RunId,
    string Env,
    DateTimeOffset Ts,
    string Report,
    Guid? ReportId,
    string Status,
    long? DurationMs,
    int? Pages,
    string? ErrorType,
    string? ErrorMessage,
    string? Login,
    string? Person,
    string? Server,
    string? ClientType,
    string? ClientVersion,
    string? ModuleVersion,
    string? Machine);

public static class ReportStatus
{
    /// <summary>Отчёт построен.</summary>
    public const string Success = "success";
    /// <summary>Исключение в скрипте или при построении.</summary>
    public const string Error = "error";
    /// <summary>Скрипт сам остановил построение через ReportException («Выберите проект» и т.п.).</summary>
    public const string Validation = "validation";
    /// <summary>Окно закрыли до окончания построения.</summary>
    public const string Cancelled = "cancelled";

    public static readonly IReadOnlySet<string> All =
        new HashSet<string>(StringComparer.Ordinal) { Success, Error, Validation, Cancelled };
}
