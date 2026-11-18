using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ServerHost.Hosting;

/// <summary>
/// Статистика серверных модулей / report-metrics-collector (POST /api/module-events) / metrics.module_events /
/// Grafana «Pilot - серверные модули». Те же события, что шлёт клиентский Pilot.ClientSdk, но с client_type = "pilot-server":
/// kind=load - модуль загружен хостом (старт, установка, перезагрузка, откат), kind=action - вызов активности
/// (время, успех/ошибка). Отправка пачками в фоне; вызов активности сеть не ждёт, ошибки отправки только в журнал.
///
/// Настройка (по убыванию приоритета): переменные окружения STATS_URL / STATS_API_KEY / STATS_SERVER,
/// файл stats.json в папке модулей ({"enabled":true,"statsUrl":"…","apiKey":"…","server":"…"}),
/// по умолчанию - collector на хосте Docker (host.docker.internal:5310).
/// </summary>
internal sealed class ServerStats
{
    public const string ClientType = "pilot-server";
    private const string DefaultUrl = "http://host.docker.internal:5310/api/module-events";
    private const int MaxQueue = 5000;
    private const int MaxBatch = 100;
    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(15);

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly ConcurrentQueue<StatsEvent> _queue = new();
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private readonly Action<string, string> _log;
    private readonly string _server;
    private readonly string _hostVersion;
    private readonly Timer? _timer;
    private int _flushing;
    private DateTime _lastFailureLogged = DateTime.MinValue;

    public ServerStats(string storageRoot, string hostVersion, Action<string, string> log)
    {
        _log = log;
        _hostVersion = hostVersion;
        var file = ReadFile(Path.Combine(storageRoot, "stats.json"));
        Url = Env("STATS_URL") ?? file?.StatsUrl ?? DefaultUrl;
        Enabled = (file?.Enabled ?? true) && !string.Equals(Url, "off", StringComparison.OrdinalIgnoreCase);
        var apiKey = Env("STATS_API_KEY") ?? file?.ApiKey;
        if (!string.IsNullOrEmpty(apiKey))
            _http.DefaultRequestHeaders.Add("X-Api-Key", apiKey);
        _server = Env("STATS_SERVER") ?? file?.Server ?? DefaultServer();
        if (Enabled)
            _timer = new Timer(_ => _ = FlushAsync(), null, FlushInterval, FlushInterval);
    }

    public bool Enabled { get; }
    public string Url { get; }

    /// <summary>Модуль загружен хостом - версия модуля на сервере.</summary>
    public void Loaded(string module, string version) =>
        Enqueue(new StatsEvent { Kind = "load", Module = module, ModuleVersion = version, Status = "success" });

    /// <summary>Вызов активности модуля.</summary>
    public void Call(string module, string? version, string activity, long durationMs, Exception? error) =>
        Enqueue(new StatsEvent
        {
            Kind = "action",
            Module = module,
            ModuleVersion = version,
            Action = activity,
            Status = error is null ? "success" : error is OperationCanceledException ? "cancelled" : "error",
            DurationMs = durationMs,
            ErrorType = error is null or OperationCanceledException ? null : Root(error).GetType().FullName,
            ErrorMessage = error is null or OperationCanceledException ? null : Truncate(Root(error).Message),
        });

    private void Enqueue(StatsEvent e)
    {
        if (!Enabled)
            return;
        try
        {
            e.EventId = Guid.NewGuid();
            e.Ts = DateTimeOffset.UtcNow;
            e.ModuleTitle = e.Module;
            e.Server = _server;
            e.ClientType = ClientType;
            e.ClientVersion = _hostVersion;
            e.LibVersion = _hostVersion;
            e.Machine = Environment.MachineName;
            _queue.Enqueue(e);
            while (_queue.Count > MaxQueue && _queue.TryDequeue(out _)) { }
            if (_queue.Count >= MaxBatch)
                _ = FlushAsync();
        }
        catch
        {
            // статистика не должна ломать вызов активности
        }
    }

    public async Task FlushAsync()
    {
        if (Interlocked.Exchange(ref _flushing, 1) == 1)
            return;
        try
        {
            while (!_queue.IsEmpty)
            {
                var batch = new List<StatsEvent>(MaxBatch);
                while (batch.Count < MaxBatch && _queue.TryPeek(out var e))
                {
                    batch.Add(e);
                    _queue.TryDequeue(out _);
                }
                using var request = new HttpRequestMessage(HttpMethod.Post, Url) { Content = JsonContent.Create(batch, options: Json) };
                request.Headers.Add("X-Client-Time", DateTimeOffset.UtcNow.ToString("O"));
                using var response = await _http.SendAsync(request).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                    throw new HttpRequestException($"HTTP {(int)response.StatusCode}");
            }
        }
        catch (Exception ex)
        {
            // Пачка теряется (как в Pilot.ClientSdk); в журнал - не чаще раза в 10 минут, чтобы не засорять его при недоступном сервисе.
            if (DateTime.UtcNow - _lastFailureLogged > TimeSpan.FromMinutes(10))
            {
                _lastFailureLogged = DateTime.UtcNow;
                _log("warn", $"Статистика не отправлена в {Url}: {ex.Message}");
            }
        }
        finally
        {
            Volatile.Write(ref _flushing, 0);
        }
    }

    /// <summary>
    /// Имя сервера для статистики. Контур collector определяет по подстроке (test/stage, иначе prod);
    /// в Docker-контейнерах Pilot его подсказывает PG_WAIT_HOST (postgres-test, postgres-stage, postgres-prod).
    /// </summary>
    private static string DefaultServer()
    {
        var db = Env("PG_WAIT_HOST");
        return db is null ? $"pilot-server {Environment.MachineName}" : $"pilot-server {Environment.MachineName} ({db})";
    }

    /// <summary>Текст ошибки может содержать данные объектов - в статистику уходит только начало.</summary>
    internal static string Truncate(string text) => text.Length <= MaxErrorMessage ? text : text[..MaxErrorMessage];

    private const int MaxErrorMessage = 1000;

    private static Exception Root(Exception ex)
    {
        while ((ex is AggregateException { InnerExceptions.Count: 1 } || ex is System.Reflection.TargetInvocationException)
               && ex.InnerException is { } inner)
            ex = inner;
        return ex;
    }

    private static string? Env(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v.Trim() : null;

    private static StatsFile? ReadFile(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<StatsFile>(File.ReadAllText(path), ModuleStorage.Json) : null;
        }
        catch
        {
            return null;
        }
    }

    private sealed class StatsFile
    {
        public bool? Enabled { get; set; }
        public string? StatsUrl { get; set; }
        public string? ApiKey { get; set; }
        public string? Server { get; set; }
    }

    private sealed class StatsEvent
    {
        public Guid EventId { get; set; }
        public DateTimeOffset Ts { get; set; }
        public string Kind { get; set; } = "";
        public string Module { get; set; } = "";
        public string? ModuleTitle { get; set; }
        public string? ModuleVersion { get; set; }
        public string? Action { get; set; }
        public string Status { get; set; } = "";
        public long? DurationMs { get; set; }
        public string? ErrorType { get; set; }
        public string? ErrorMessage { get; set; }
        public string? Server { get; set; }
        public string? ClientType { get; set; }
        public string? ClientVersion { get; set; }
        public string? Machine { get; set; }
        public string? LibVersion { get; set; }
    }
}
