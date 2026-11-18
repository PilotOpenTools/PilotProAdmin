namespace ReportMetrics.Collector;

/// <summary>
/// Проверяет и нормализует события от клиентов: обрезает строки, определяет контур
/// по адресу сервера Pilot, исправляет время с учётом сбитых часов на рабочем месте.
/// </summary>
public sealed class EventNormalizer
{
    // События могут прийти с опозданием (клиент был без сети и копил очередь) - это нормально.
    // Но старше этого срока или из будущего - явно сбитые часы без заголовка X-Client-Time.
    private static readonly TimeSpan MaxAge = TimeSpan.FromDays(60);
    private static readonly TimeSpan MaxFuture = TimeSpan.FromMinutes(10);
    // Меньшее расхождение часов не исправляем - это шум сети.
    private static readonly TimeSpan MinSkewToFix = TimeSpan.FromMinutes(2);

    private readonly EnvResolver _env;

    public EventNormalizer(EnvResolver env) => _env = env;

    /// <param name="clientSentAt">Время отправки по часам клиента (заголовок X-Client-Time), если есть.</param>
    public bool TryNormalize(ReportEventDto? dto, DateTimeOffset receivedAt, out ReportEventRow? row, out string? error,
        DateTimeOffset? clientSentAt = null)
    {
        row = null;
        error = null;

        if (dto == null)
        {
            error = "пустое событие (null)";
            return false;
        }

        if (dto.RunId == Guid.Empty)
        {
            error = "run_id пустой";
            return false;
        }

        var report = Clip(dto.Report, 300);
        if (string.IsNullOrWhiteSpace(report))
        {
            error = "report пустой";
            return false;
        }

        var status = dto.Status?.Trim().ToLowerInvariant();
        if (status == null || !ReportStatus.All.Contains(status))
        {
            error = $"неизвестный status '{ForError(dto.Status)}'";
            return false;
        }

        var ts = FixTime(dto.Ts, receivedAt, clientSentAt);

        long? duration = dto.DurationMs is >= 0 ? dto.DurationMs : null;
        int? pages = dto.Pages is >= 0 ? dto.Pages : null;
        var server = Clip(dto.Server, 300);

        row = new ReportEventRow(
            dto.RunId,
            _env.Resolve(server),
            ts.ToUniversalTime(),
            report.Trim(),
            dto.ReportId == Guid.Empty ? null : dto.ReportId,
            status,
            duration,
            pages,
            Clip(dto.ErrorType, 200),
            Clip(dto.ErrorMessage, 2000),
            Clip(dto.Login, 200),
            Clip(dto.Person, 300),
            server,
            Clip(dto.ClientType, 50),
            Clip(dto.ClientVersion, 50),
            ModuleVersions.Display(Clip(dto.ModuleVersion, 50)),
            Clip(dto.Machine, 100));
        return true;
    }

    internal static DateTimeOffset FixTime(DateTimeOffset? clientTs, DateTimeOffset receivedAt, DateTimeOffset? clientSentAt)
    {
        if (clientTs == null)
            return receivedAt;

        var ts = clientTs.Value;
        if (clientSentAt.HasValue)
        {
            // Часы клиента отстают/спешат на skew - сдвигаем время события на столько же.
            var skew = receivedAt - clientSentAt.Value;
            if (skew.Duration() > MinSkewToFix)
                ts += skew;
        }

        if (ts > receivedAt + MaxFuture || ts < receivedAt - MaxAge)
            return receivedAt;
        return ts;
    }

    /// <summary>Значение из чужого запроса для текста ошибки: обрезано и без переводов строк (текст попадает в ответ и журнал).</summary>
    internal static string ForError(string? value)
    {
        var clipped = Clip(value, 50) ?? "";
        return string.Concat(clipped.Select(c => char.IsControl(c) ? ' ' : c));
    }

    private static string? Clip(string? value, int max)
    {
        if (string.IsNullOrEmpty(value))
            return null;
        return value.Length <= max ? value : value.Substring(0, max);
    }
}

/// <summary>
/// Определяет контур (prod/test/stage) по адресу сервера Pilot, который прислал клиент.
/// Правила задаются строкой "подстрока=контур;подстрока=контур;*=контур_по_умолчанию".
/// Проверяются по порядку, первое совпадение (без учёта регистра) выигрывает.
/// </summary>
public sealed class EnvResolver
{
    private readonly List<(string Pattern, string Env)> _rules = new();
    private readonly string _fallback = "unknown";

    public EnvResolver(string? map)
    {
        if (string.IsNullOrWhiteSpace(map))
            return;

        foreach (var part in map.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0 || eq == part.Length - 1)
                continue;

            var pattern = part[..eq].Trim();
            var env = part[(eq + 1)..].Trim();
            if (pattern == "*")
                _fallback = env;
            else
                _rules.Add((pattern, env));
        }
    }

    public string Resolve(string? server)
    {
        if (!string.IsNullOrEmpty(server))
        {
            foreach (var (pattern, env) in _rules)
            {
                if (server.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                    return env;
            }
        }
        return _fallback;
    }
}
