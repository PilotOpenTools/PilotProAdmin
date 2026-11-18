using System.Security.Cryptography;
using System.Text;

namespace ReportMetrics.Collector;

/// <summary>Проверки входящих запросов: ключ API и период сводки.</summary>
public static class RequestGuards
{
    /// <summary>Максимум для параметра hours сводок (366 суток) - иначе запрос обходит всю таблицу или падает.</summary>
    public const int MaxSummaryHours = 24 * 366;

    /// <summary>
    /// Ключ не задан - запросы принимаются без ключа (так сервис поставляется; при старте пишется предупреждение).
    /// Иначе ключ сравнивается за постоянное время.
    /// </summary>
    public static bool IsKeyValid(string? configuredKey, string? providedKey)
    {
        if (string.IsNullOrEmpty(configuredKey))
            return true;
        if (providedKey == null)
            return false;
        // Хеши одной длины: время сравнения не зависит ни от длины, ни от содержимого ключа.
        return CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(configuredKey)),
            SHA256.HashData(Encoding.UTF8.GetBytes(providedKey)));
    }

    /// <summary>Не задан или не больше 0 - 24 часа; больше MaxSummaryHours - ошибка (400).</summary>
    public static bool TryGetSummaryPeriod(int? hours, out TimeSpan period)
    {
        var h = hours is > 0 ? hours.Value : 24;
        period = TimeSpan.FromHours(Math.Min(h, MaxSummaryHours));
        return h <= MaxSummaryHours;
    }
}
