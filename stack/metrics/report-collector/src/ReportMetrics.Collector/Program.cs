using System.Text.Json;
using Npgsql;
using ReportMetrics.Collector;

// Сервис приёма статистики Pilot: построения отчётов и использование клиентских модулей.
// Reports шлёт POST /api/report-events / metrics.report_events,
// Pilot.ClientSdk шлёт POST /api/module-events / metrics.module_events; Grafana читает обе таблицы.

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("Metrics")
    ?? throw new InvalidOperationException("Не задана строка подключения ConnectionStrings__Metrics");
var apiKey = builder.Configuration["Collector:ApiKey"];
var maxBatch = builder.Configuration.GetValue("Collector:MaxBatch", 500);
// Размер тела запроса: пачка до MaxBatch событий с обрезанными полями укладывается с запасом.
var maxBodyBytes = builder.Configuration.GetValue("Collector:MaxBodyBytes", 10L * 1024 * 1024);
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = maxBodyBytes);

builder.Services.AddSingleton(NpgsqlDataSource.Create(connectionString));
builder.Services.AddSingleton(new EnvResolver(builder.Configuration["Collector:EnvMap"]));
builder.Services.AddSingleton<EventNormalizer>();
builder.Services.AddSingleton<ReportEventStore>();
builder.Services.AddSingleton<ModuleEventNormalizer>();
builder.Services.AddSingleton<ModuleEventStore>();
builder.Services.AddSingleton<IRetentionTarget>(sp => sp.GetRequiredService<ReportEventStore>());
builder.Services.AddSingleton<IRetentionTarget>(sp => sp.GetRequiredService<ModuleEventStore>());
// Срок хранения событий с логинами, ФИО и именами машин. Служба стартует в app.Run - после EnsureSchema.
var retentionDays = RetentionService.ParseDays(builder.Configuration["Collector:RetentionDays"]);
builder.Services.AddHostedService(sp => new RetentionService(
    sp.GetServices<IRetentionTarget>(), retentionDays, sp.GetRequiredService<ILogger<RetentionService>>()));
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
    o.SerializerOptions.PropertyNameCaseInsensitive = true;
});

var app = builder.Build();

if (string.IsNullOrEmpty(apiKey))
    app.Logger.LogWarning("Collector:ApiKey не задан (COLLECTOR_API_KEY): приём событий и сводки открыты без ключа. " +
        "Задайте ключ, если порт доступен не только доверенной сети.");

await app.Services.GetRequiredService<ReportEventStore>().EnsureSchemaAsync(app.Lifetime.ApplicationStopping);
await app.Services.GetRequiredService<ModuleEventStore>().EnsureSchemaAsync(app.Lifetime.ApplicationStopping);

var jsonOptions = new JsonSerializerOptions
{
    PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    PropertyNameCaseInsensitive = true,
};

app.MapGet("/health", async (ReportEventStore store, CancellationToken ct) =>
    await store.PingAsync(ct)
        ? Results.Ok(new { status = "ok" })
        : Results.Json(new { status = "db-unavailable" }, statusCode: StatusCodes.Status503ServiceUnavailable));

// Принимает одно событие или массив событий.
app.MapPost("/api/report-events", async (
    HttpRequest request,
    EventNormalizer normalizer,
    ReportEventStore store,
    ILogger<Program> logger,
    CancellationToken ct) =>
{
    if (!RequestGuards.IsKeyValid(apiKey, request.Headers["X-Api-Key"]))
        return Results.Unauthorized();

    List<ReportEventDto>? events;
    try
    {
        using var doc = await JsonDocument.ParseAsync(request.Body, cancellationToken: ct);
        events = doc.RootElement.ValueKind switch
        {
            JsonValueKind.Array => doc.RootElement.Deserialize<List<ReportEventDto>>(jsonOptions),
            JsonValueKind.Object => new List<ReportEventDto> { doc.RootElement.Deserialize<ReportEventDto>(jsonOptions)! },
            _ => null,
        };
    }
    catch (JsonException ex)
    {
        return Results.BadRequest(new { error = "Некорректный JSON: " + ex.Message });
    }

    if (events == null)
        return Results.BadRequest(new { error = "Ожидается объект или массив событий" });
    if (events.Count > maxBatch)
        return Results.BadRequest(new { error = $"Слишком большая пачка: {events.Count} > {maxBatch}" });

    var receivedAt = DateTimeOffset.UtcNow;
    DateTimeOffset? clientSentAt = DateTimeOffset.TryParse(request.Headers["X-Client-Time"], out var sent) ? sent : null;
    var rows = new List<ReportEventRow>(events.Count);
    var rejected = new List<string>();
    foreach (var e in events)
    {
        if (normalizer.TryNormalize(e, receivedAt, out var row, out var error, clientSentAt))
            rows.Add(row!);
        else
            rejected.Add($"{e?.RunId}: {error}");
    }

    var inserted = await store.InsertAsync(rows, ct);
    if (rejected.Count > 0)
        logger.LogWarning("Отклонено событий: {Count}. {Details}", rejected.Count, string.Join("; ", rejected.Take(5)));

    // Отклонённые события клиенту повторять бесполезно - поэтому 200, а подробности в ответе.
    return Results.Ok(new
    {
        accepted = inserted,
        duplicates = rows.Count - inserted,
        rejected,
    });
});

app.MapGet("/api/report-events/summary", async (HttpRequest request, int? hours, ReportEventStore store, CancellationToken ct) =>
{
    if (!RequestGuards.IsKeyValid(apiKey, request.Headers["X-Api-Key"]))
        return Results.Unauthorized();
    if (!RequestGuards.TryGetSummaryPeriod(hours, out var period))
        return Results.BadRequest(new { error = $"hours не больше {RequestGuards.MaxSummaryHours}" });
    return Results.Ok(await store.SummaryAsync(period, ct));
});

// События клиентских модулей (Pilot.ClientSdk): одно событие или массив - модуль шлёт пачками.
app.MapPost("/api/module-events", async (
    HttpRequest request,
    ModuleEventNormalizer normalizer,
    ModuleEventStore store,
    ILogger<Program> logger,
    CancellationToken ct) =>
{
    if (!RequestGuards.IsKeyValid(apiKey, request.Headers["X-Api-Key"]))
        return Results.Unauthorized();

    List<ModuleEventDto>? events;
    try
    {
        using var doc = await JsonDocument.ParseAsync(request.Body, cancellationToken: ct);
        events = doc.RootElement.ValueKind switch
        {
            JsonValueKind.Array => doc.RootElement.Deserialize<List<ModuleEventDto>>(jsonOptions),
            JsonValueKind.Object => new List<ModuleEventDto> { doc.RootElement.Deserialize<ModuleEventDto>(jsonOptions)! },
            _ => null,
        };
    }
    catch (JsonException ex)
    {
        return Results.BadRequest(new { error = "Некорректный JSON: " + ex.Message });
    }

    if (events == null)
        return Results.BadRequest(new { error = "Ожидается объект или массив событий" });
    if (events.Count > maxBatch)
        return Results.BadRequest(new { error = $"Слишком большая пачка: {events.Count} > {maxBatch}" });

    var receivedAt = DateTimeOffset.UtcNow;
    DateTimeOffset? clientSentAt = DateTimeOffset.TryParse(request.Headers["X-Client-Time"], out var sent) ? sent : null;
    var rows = new List<ModuleEventRow>(events.Count);
    var rejected = new List<string>();
    foreach (var e in events)
    {
        if (normalizer.TryNormalize(e, receivedAt, out var row, out var error, clientSentAt))
            rows.Add(row!);
        else
            rejected.Add($"{e?.EventId}: {error}");
    }

    var inserted = await store.InsertAsync(rows, ct);
    if (rejected.Count > 0)
        logger.LogWarning("Отклонено событий модулей: {Count}. {Details}", rejected.Count, string.Join("; ", rejected.Take(5)));

    return Results.Ok(new { accepted = inserted, duplicates = rows.Count - inserted, rejected });
});

app.MapGet("/api/module-events/summary", async (HttpRequest request, int? hours, ModuleEventStore store, CancellationToken ct) =>
{
    if (!RequestGuards.IsKeyValid(apiKey, request.Headers["X-Api-Key"]))
        return Results.Unauthorized();
    if (!RequestGuards.TryGetSummaryPeriod(hours, out var period))
        return Results.BadRequest(new { error = $"hours не больше {RequestGuards.MaxSummaryHours}" });
    return Results.Ok(await store.SummaryAsync(period, ct));
});

app.Run();

public partial class Program;
