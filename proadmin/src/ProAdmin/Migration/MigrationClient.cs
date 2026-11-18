using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using ProAdmin.Infrastructure;
using Microsoft.Extensions.Options;

namespace ProAdmin.Migration;

/// <summary>HTTP-клиент сервиса pilot-migration (он делает сам перенос; админка - только интерфейс).</summary>
public sealed class MigrationClient
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly IHttpClientFactory _factory;
    private readonly MigrationServiceOptions _options;
    private readonly NetworkAddressMapper _network;

    public MigrationClient(IHttpClientFactory factory, IOptions<AppOptions> options, NetworkAddressMapper network)
    {
        _factory = factory;
        _options = options.Value.Migration;
        _network = network;
    }

    public bool IsConfigured => _options.IsConfigured;

    public string? Url => _options.Url;

    public Task<MigrationStatus> GetStatusAsync(CancellationToken ct) => GetAsync<MigrationStatus>("api/status", ct);

    /// <summary>Присланные файловые базы в каталоге импорта сервиса.</summary>
    public Task<ImportSourcesDto> GetImportSourcesAsync(CancellationToken ct) =>
        GetAsync<ImportSourcesDto>("api/import/sources", ct);

    /// <summary>Готовые выгрузки базы в файлы.</summary>
    public Task<ExportListDto> GetExportsAsync(CancellationToken ct) =>
        GetAsync<ExportListDto>("api/export/list", ct);

    public Task<MigrationPlanDto> PlanAsync(MigrationRequestDto request, CancellationToken ct) =>
        SendAsync<MigrationPlanDto>(HttpMethod.Post, "api/plan", request, null, ct);

    public async Task<MigrationRunDto> StartAsync(MigrationRequestDto request, string user, CancellationToken ct)
    {
        using var response = await SendRawAsync(HttpMethod.Post, "api/runs", new { request }, user, ct);
        if (response.StatusCode == HttpStatusCode.Conflict)
            throw new MigrationServiceException(await ErrorOf(response, ct) ?? "Уже идёт другой перенос.");
        await EnsureOk(response, ct);
        return (await response.Content.ReadFromJsonAsync<MigrationRunDto>(Json, ct))!;
    }

    public Task<List<MigrationRunDto>> ListRunsAsync(int limit, CancellationToken ct) => GetAsync<List<MigrationRunDto>>($"api/runs?limit={limit}", ct);

    public async Task<MigrationRunDto?> GetRunAsync(Guid id, CancellationToken ct)
    {
        using var response = await SendRawAsync(HttpMethod.Get, $"api/runs/{id}", null, null, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        await EnsureOk(response, ct);
        return await response.Content.ReadFromJsonAsync<MigrationRunDto>(Json, ct);
    }

    public async Task CancelAsync(Guid id, CancellationToken ct)
    {
        using var response = await SendRawAsync(HttpMethod.Post, $"api/runs/{id}/cancel", null, null, ct);
        await EnsureOk(response, ct);
    }

    public Task<List<ScheduleDto>> ListSchedulesAsync(CancellationToken ct) => GetAsync<List<ScheduleDto>>("api/schedules", ct);

    public async Task<ScheduleDto?> GetScheduleAsync(Guid id, CancellationToken ct)
    {
        using var response = await SendRawAsync(HttpMethod.Get, $"api/schedules/{id}", null, null, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        await EnsureOk(response, ct);
        return await response.Content.ReadFromJsonAsync<ScheduleDto>(Json, ct);
    }

    public Task<ScheduleDto> SaveScheduleAsync(ScheduleDto schedule, string user, CancellationToken ct) =>
        SendAsync<ScheduleDto>(HttpMethod.Post, "api/schedules", schedule, user, ct);

    public async Task DeleteScheduleAsync(Guid id, CancellationToken ct)
    {
        using var response = await SendRawAsync(HttpMethod.Delete, $"api/schedules/{id}", null, null, ct);
        await EnsureOk(response, ct);
    }

    public Task<MigrationConfigDto> GetConfigAsync(CancellationToken ct) => GetAsync<MigrationConfigDto>("api/config", ct);

    public Task<MigrationConfigDto> SaveConfigAsync(MigrationConfigDto config, string user, CancellationToken ct) =>
        SendAsync<MigrationConfigDto>(HttpMethod.Put, "api/config", config, user, ct);

    public Task<List<ConnectionCheckDto>> CheckConfigAsync(CancellationToken ct) =>
        SendAsync<List<ConnectionCheckDto>>(HttpMethod.Post, "api/config/check", null, null, ct);

    private Task<T> GetAsync<T>(string path, CancellationToken ct) => SendAsync<T>(HttpMethod.Get, path, null, null, ct);

    private async Task<T> SendAsync<T>(HttpMethod method, string path, object? body, string? user, CancellationToken ct)
    {
        using var response = await SendRawAsync(method, path, body, user, ct);
        await EnsureOk(response, ct);
        return (await response.Content.ReadFromJsonAsync<T>(Json, ct))!;
    }

    private async Task<HttpResponseMessage> SendRawAsync(HttpMethod method, string path, object? body, string? user, CancellationToken ct)
    {
        if (!IsConfigured)
            throw new MigrationServiceException("Сервис миграции не подключён: задайте App__Migration__Url и App__Migration__ApiKey.");
        var baseUrl = _network.ToInternal(_options.Url!.TrimEnd('/')) + "/";
        using var request = new HttpRequestMessage(method, new Uri(new Uri(baseUrl), path));
        request.Headers.Add("X-Api-Key", _options.ApiKey);
        if (user is not null)
            request.Headers.Add("X-User", Uri.EscapeDataString(user));
        if (body is not null)
            request.Content = JsonContent.Create(body, body.GetType(), options: Json);
        try
        {
            return await _factory.CreateClient("migration").SendAsync(request, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new MigrationServiceException($"Сервис миграции {_options.Url} недоступен: {ex.Message}", ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new MigrationServiceException($"Сервис миграции {_options.Url} не ответил вовремя.", ex);
        }
    }

    private static async Task EnsureOk(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
            return;
        var error = await ErrorOf(response, ct);
        throw new MigrationServiceException(response.StatusCode == HttpStatusCode.Unauthorized
            ? "Сервис миграции отклонил ключ API: проверьте App__Migration__ApiKey и MIGRATION_API_KEY."
            : $"Сервис миграции: {error ?? response.StatusCode.ToString()}");
    }

    private static async Task<string?> ErrorOf(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var doc = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
            return doc.TryGetProperty("error", out var e) ? e.GetString() : null;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            return null;
        }
    }
}

public sealed class MigrationServiceException : Exception
{
    public MigrationServiceException(string message, Exception? inner = null) : base(message, inner) { }
}
