using System.Text.Json;
using System.Text.Json.Serialization;

namespace ProAdmin.Pilot.ServerModules;

/// <summary>
/// Серверные модули через центральный модуль ServerHost на сервере Pilot: вход пользователем базы
/// (администратором), вызовы IServerModulesApi. Модули обновляются без перезапуска сервера.
/// </summary>
public sealed class PilotServerModuleGateway
{
    private const int ChunkSize = 512 * 1024;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
    };

    private readonly PilotReportGateway _sessions;
    private readonly PilotGateway _admin;
    private readonly ILogger<PilotServerModuleGateway> _logger;

    public PilotServerModuleGateway(PilotReportGateway sessions, PilotGateway admin, ILogger<PilotServerModuleGateway> logger)
    {
        _sessions = sessions;
        _admin = admin;
        _logger = logger;
    }

    public async Task<HostState> GetStateAsync(string url, PilotLogin login, int licenseType, CancellationToken ct) =>
        (await InvokeAsync(url, login, licenseType, new HostRequest { Op = HostOps.State }, ct)).State
        ?? throw new PilotGatewayException(PilotErrorKind.Other, "ServerHost не вернул состояние.");

    /// <summary>Загрузка архива, уже лежащего в памяти (выкладка из CI), кусками и проверка.</summary>
    public Task<HostResponse> UploadAndValidateAsync(string url, PilotLogin login, int licenseType, string fileName, byte[] zip,
        bool stripServerAssemblies, CancellationToken ct) =>
        UploadAndValidateAsync(url, login, licenseType, fileName, new MemoryStream(zip, writable: false), zip.Length, stripServerAssemblies, ct);

    /// <summary>Загрузка архива кусками прямо из потока (архив не копируется в память) и проверка. Архив остаётся на сервере до установки (30 минут).</summary>
    public Task<HostResponse> UploadAndValidateAsync(string url, PilotLogin login, int licenseType, string fileName, Stream zip, long length,
        bool stripServerAssemblies, CancellationToken ct) =>
        RunAsync(url, login, licenseType, api => UploadAsync(api, fileName, zip, length, stripServerAssemblies, ct), ct);

    internal static async Task<HostResponse> UploadAsync(IServerModulesApi api, string fileName, Stream zip, long length,
        bool stripServerAssemblies, CancellationToken ct)
    {
        var begin = Parse(await api.InvokeAsync(Serialize(new HostRequest { Op = HostOps.BeginUpload, FileName = fileName, Size = length })));
        if (begin.Error is not null || begin.UploadId is null)
            return begin;

        var buffer = new byte[ChunkSize];
        long sentBytes = 0;
        int read;
        while ((read = await zip.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, ct)) > 0)
        {
            // Последний кусок короче буфера: хвост прошлого куска в него попасть не должен.
            var sent = Parse(await api.UploadChunkAsync(begin.UploadId, read == buffer.Length ? buffer : buffer[..read]));
            if (sent.Error is not null)
                return sent;
            sentBytes += read;
        }
        if (sentBytes != length)
            return new HostResponse { Error = $"Прочитано {sentBytes} байт архива вместо {length}: загрузка прервана." };

        return Parse(await api.InvokeAsync(Serialize(new HostRequest
        {
            Op = HostOps.Validate, UploadId = begin.UploadId, StripServerAssemblies = stripServerAssemblies,
        })));
    }

    /// <summary>Повторная проверка уже загруженного архива (например, с удалением лишних сборок).</summary>
    public Task<HostResponse> RevalidateAsync(string url, PilotLogin login, int licenseType, string uploadId, bool stripServerAssemblies, CancellationToken ct) =>
        InvokeAsync(url, login, licenseType, new HostRequest { Op = HostOps.Validate, UploadId = uploadId, StripServerAssemblies = stripServerAssemblies }, ct);

    public async Task<HostResponse> InvokeAsync(string url, PilotLogin login, int licenseType, HostRequest request, CancellationToken ct)
    {
        try
        {
            return await RunAsync(url, login, licenseType, async api => Parse(await api.InvokeAsync(Serialize(request))), ct);
        }
        finally
        {
            if (request.Op is not (HostOps.State or HostOps.Validate))
                InvalidateActivities(url);
        }
    }

    /// <summary>
    /// Движок автоматизаций привязывает имена активностей при изменении метаданных базы. После появления новых
    /// имён админка сохраняет метаданные без изменений (учёткой администратора сервера) - Pilot пересобирает снимок.
    /// </summary>
    public async Task<string?> RebindAutomationsAsync(string url, PilotLogin serverAdmin, CancellationToken ct)
    {
        try
        {
            await _admin.UpdateAutomationsAsync(url, serverAdmin, _ => { }, ct);
            return null;
        }
        catch (PilotGatewayException ex)
        {
            _logger.LogWarning(ex, "Не удалось обновить метаданные {Url} для привязки активностей", url);
            return ex.Message;
        }
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTimeOffset At, Automations.Validation.HostedActivities Value)> _activitiesCache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Действия модулей, загруженных в ServerHost (горячая загрузка), - для проверки сценариев автоматизаций.
    /// Кэш на минуту; хост не установлен или недоступен - <see cref="Automations.Validation.HostedActivities.Available"/> = false.
    /// </summary>
    public async Task<Automations.Validation.HostedActivities> GetHostedActivitiesAsync(string url, PilotLogin login, int licenseType, CancellationToken ct)
    {
        var key = url.Trim().TrimEnd('/');
        if (_activitiesCache.TryGetValue(key, out var cached) && cached.At > DateTimeOffset.UtcNow.AddMinutes(-1))
            return cached.Value;

        Automations.Validation.HostedActivities result;
        try
        {
            result = Automations.Validation.HostedActivities.From(await GetStateAsync(url, login, licenseType, ct));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Проверка сценария не должна падать из-за хоста: без списка модулей линтер просто предупредит.
            result = Automations.Validation.HostedActivities.Unavailable(ex.Message);
        }
        _activitiesCache[key] = (DateTimeOffset.UtcNow, result);
        return result;
    }

    /// <summary>Сброс кэша действий после установки, отката, включения или удаления модуля.</summary>
    public void InvalidateActivities(string url) => _activitiesCache.TryRemove(url.Trim().TrimEnd('/'), out _);

    private Task<T> RunAsync<T>(string url, PilotLogin login, int licenseType, Func<IServerModulesApi, Task<T>> action, CancellationToken ct) =>
        _sessions.RunAsync(url, login, licenseType, async session =>
        {
            try
            {
                return await action(session.CustomApi<IServerModulesApi>());
            }
            catch (Exception ex) when (IsHostMissing(ex))
            {
                throw new PilotGatewayException(PilotErrorKind.Configuration,
                    "На сервере не установлен центральный модуль ServerHost (или сервер не перезапущен после его установки).", ex);
            }
        }, ct);

    private static bool IsHostMissing(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
            if (e is NotImplementedException || e.Message.Contains(nameof(IServerModulesApi), StringComparison.Ordinal))
                return true;
        return false;
    }

    private static string Serialize(HostRequest request) => JsonSerializer.Serialize(request, Json);

    internal static HostResponse Parse(string json) =>
        string.IsNullOrWhiteSpace(json)
            ? new HostResponse { Error = "Пустой ответ ServerHost." }
            : JsonSerializer.Deserialize<HostResponse>(json, Json) ?? new HostResponse { Error = "Пустой ответ ServerHost." };
}
