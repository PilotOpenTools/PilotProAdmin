using System.Collections.Concurrent;
using System.Net.Sockets;
using Ascon.Pilot.Common;
using Ascon.Pilot.DataClasses;
using Ascon.Pilot.Server.Api;
using Ascon.Pilot.Server.Api.Contracts;
using ProAdmin.Infrastructure;

namespace ProAdmin.Pilot;

/// <summary>
/// Работа с автоматизациями через админский API Pilot-Server (LoginServerAdministrator).
/// На каждую операцию открывается отдельное короткое подключение.
/// </summary>
public sealed class PilotGateway
{
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromMinutes(2);

    private readonly NetworkAddressMapper _network;
    private readonly ILogger<PilotGateway> _logger;

    // Метаданные пишутся целиком (read-modify-write) - сериализуем записи в одну базу.
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _writeLocks = new(StringComparer.OrdinalIgnoreCase);

    public PilotGateway(NetworkAddressMapper network, ILogger<PilotGateway> logger)
    {
        _network = network;
        _logger = logger;
    }

    /// <summary>Проверка входа администратора сервера и наличия базы (если она указана в адресе).</summary>
    public Task VerifyLoginAsync(string url, PilotLogin login, CancellationToken ct) =>
        RunAsync(url, login, requireDatabase: false, async (api, database) =>
        {
            if (database.Length == 0)
                return true;

            var databases = await api.GetDatabaseInfoListAsync();
            if (!databases.Any(d => string.Equals(d.Name, database, StringComparison.OrdinalIgnoreCase)))
            {
                var names = string.Join(", ", databases.Select(d => d.Name));
                throw new PilotGatewayException(PilotErrorKind.Configuration,
                    $"База «{database}» не найдена на сервере. Доступные базы: {(names.Length > 0 ? names : "нет")}.");
            }
            return true;
        }, ct);

    public Task<IReadOnlyList<AutomationSnapshot>> GetAutomationsAsync(string url, PilotLogin login, CancellationToken ct) =>
        RunAsync<IReadOnlyList<AutomationSnapshot>>(url, login, requireDatabase: true, async (api, database) =>
        {
            var metadata = await api.GetMetadataAsync(database, 0);
            return (metadata.AutomationConfig ?? new List<AutomationScript>())
                .Select(AutomationSnapshot.From)
                .ToList();
        }, ct);

    public async Task<AutomationSnapshot?> GetAutomationAsync(string url, PilotLogin login, string name, CancellationToken ct)
    {
        var all = await GetAutomationsAsync(url, login, ct);
        return all.FirstOrDefault(a => a.Name == name);
    }

    private readonly ConcurrentDictionary<string, (DateTimeOffset At, Automations.Validation.MetadataIndex Index)> _metadataCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Типы, атрибуты, состояния и C#-действия базы - для проверки сценариев. Кэш на 2 минуты.</summary>
    public async Task<Automations.Validation.MetadataIndex> GetMetadataIndexAsync(string url, PilotLogin login, CancellationToken ct)
    {
        var key = url.Trim().TrimEnd('/');
        if (_metadataCache.TryGetValue(key, out var cached) && cached.At > DateTimeOffset.UtcNow.AddMinutes(-2))
            return cached.Index;

        var index = await RunAsync(url, login, requireDatabase: true, async (api, database) =>
            Automations.Validation.MetadataIndex.From(await api.GetMetadataAsync(database, 0)), ct);
        _metadataCache[key] = (DateTimeOffset.UtcNow, index);
        return index;
    }

    /// <summary>Полная конфигурация базы: типы, атрибуты, состояния, группы состояний.</summary>
    public Task<DMetadata> GetMetadataAsync(string url, PilotLogin login, CancellationToken ct) =>
        RunAsync(url, login, requireDatabase: true, (api, database) => api.GetMetadataAsync(database, 0), ct);

    private void InvalidateMetadata(string url) => _metadataCache.TryRemove(url.Trim().TrimEnd('/'), out _);

    public Task<ScriptValidationResult> ValidateCSharpAsync(string url, PilotLogin login, string code, CancellationToken ct) =>
        RunAsync(url, login, requireDatabase: false, (api, _) => api.ValidateCSharpScriptAsync(code), ct);

    /// <summary>Атомарно меняет список автоматизаций базы. Возвращает новую версию метаданных.</summary>
    public async Task<long> UpdateAutomationsAsync(string url, PilotLogin login, Action<List<AutomationScript>> mutate, CancellationToken ct)
    {
        var gate = _writeLocks.GetOrAdd(url.Trim().TrimEnd('/'), _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            return await RunAsync(url, login, requireDatabase: true, async (api, database) =>
            {
                var metadata = await api.GetMetadataAsync(database, 0);
                mutate(metadata.AutomationConfig);
                return await api.UpdateMetadataAsync(database, metadata);
            }, ct);
        }
        finally
        {
            gate.Release();
            InvalidateMetadata(url);
        }
    }

    /// <summary>План записи ветки типов на сервер без изменений (что поменяется).</summary>
    public Task<Types.TypesPlan> PlanTypesAsync(string url, PilotLogin login, Types.TypesSnapshot repo, CancellationToken ct) =>
        RunAsync(url, login, requireDatabase: true, async (api, database) =>
            Types.TypesPlanner.Plan(repo, await api.GetMetadataAsync(database, 0)), ct);

    /// <summary>
    /// Записать ветку типов на сервер: план по свежим метаданным и UpdateMetadata одной операцией.
    /// Сервер не проверяет версию метаданных (устаревшая принимается), поэтому запись в базу сериализуется
    /// здесь, а план строится внутри той же блокировки. Ошибки плана - ничего не пишется.
    /// </summary>
    public async Task<Types.TypesPlan> ApplyTypesAsync(string url, PilotLogin login, Types.TypesSnapshot repo, CancellationToken ct)
    {
        var gate = _writeLocks.GetOrAdd(url.Trim().TrimEnd('/'), _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            return await RunAsync(url, login, requireDatabase: true, async (api, database) =>
            {
                var plan = Types.TypesPlanner.Plan(repo, await api.GetMetadataAsync(database, 0));
                if (!plan.CanApply || !plan.HasChanges)
                    return plan;
                var check = await api.GetMetadataAsync(database, 0);
                if (check.Version != plan.BaseVersion)
                    throw new PilotGatewayException(PilotErrorKind.Other,
                        $"Метаданные базы изменились во время записи (версия {plan.BaseVersion} / {check.Version}). Повторите операцию.");
                await api.UpdateMetadataAsync(database, plan.Metadata);
                return plan;
            }, ct);
        }
        finally
        {
            gate.Release();
            InvalidateMetadata(url);
        }
    }

    /// <summary>Откат записанного плана поверх текущих метаданных (см. <see cref="Types.TypesPlanner.Revert"/>).</summary>
    public async Task<List<string>> RevertTypesAsync(string url, PilotLogin login, Types.TypesPlan plan, CancellationToken ct)
    {
        var gate = _writeLocks.GetOrAdd(url.Trim().TrimEnd('/'), _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            return await RunAsync(url, login, requireDatabase: true, async (api, database) =>
            {
                var metadata = await api.GetMetadataAsync(database, 0);
                var notes = Types.TypesPlanner.Revert(plan, metadata);
                await api.UpdateMetadataAsync(database, metadata);
                return notes;
            }, ct);
        }
        finally
        {
            gate.Release();
            InvalidateMetadata(url);
        }
    }

    private async Task<T> RunAsync<T>(
        string url,
        PilotLogin login,
        bool requireDatabase,
        Func<IServerAdminAsyncApi, string, Task<T>> action,
        CancellationToken ct)
    {
        if (!PilotAddress.TryParse(url, out var address, out var error))
            throw new PilotGatewayException(PilotErrorKind.Configuration, error!);

        if (requireDatabase && address!.Database.Length == 0)
            throw new PilotGatewayException(PilotErrorKind.Configuration,
                $"В адресе «{url}» не указана база данных (пример: http://host:5545/pilot-ice_ru).");

        var serverUrl = _network.ToInternal(address!.ServerUrl);
        HttpPilotClient? client = null;
        try
        {
            var work = Task.Run(async () =>
            {
                client = new HttpPilotClient(serverUrl);
                client.Connect(false);
                client.GetAuthenticationApi().LoginServerAdministrator(login.Login, login.Password.EncryptAes(), false);
                var api = client.GetServerAdminAsyncApi(new NullAdminCallback());
                return await action(api, address.Database);
            }, ct);

            return await work.WaitAsync(OperationTimeout, ct);
        }
        catch (PilotGatewayException)
        {
            throw;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var translated = Translate(ex, address.ServerUrl);
            if (translated.Kind == PilotErrorKind.Authentication)
                _logger.LogInformation("Pilot {Url}: отказ во входе для {Login}", url, login.Login);
            else
                _logger.LogWarning(ex, "Ошибка Pilot {Url}", url);
            throw translated;
        }
        finally
        {
            if (client is not null)
            {
                try { client.Disconnect(); } catch { /* соединение уже закрыто */ }
                try { client.Dispose(); } catch { /* соединение уже закрыто */ }
            }
        }
    }

    private static PilotGatewayException Translate(Exception ex, string serverUrl)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            var typeName = e.GetType().Name;
            if (e is PilotSecurityAccessDeniedException or UnauthorizedAccessException ||
                typeName.Contains("AccessDenied") || typeName.Contains("Unauthorized") || typeName.Contains("Authentication"))
            {
                return new PilotGatewayException(PilotErrorKind.Authentication,
                    "Неверный логин или пароль администратора сервера Pilot.", ex);
            }

            if (e is HttpRequestException or SocketException or TimeoutException)
            {
                return new PilotGatewayException(PilotErrorKind.Connection,
                    $"Сервер Pilot {serverUrl} недоступен: {e.Message}", ex);
            }
        }

        return new PilotGatewayException(PilotErrorKind.Other, $"Ошибка сервера Pilot: {ex.Message}", ex);
    }

    private sealed class NullAdminCallback : IServerAdminCallback
    {
        public void NotifyDatabaseChanged(DatabaseChangeset changeset) { }
        public void NotifyPersonChanged(string database, PersonChangeset changeset) { }
        public void NotifyOrganisationUnitChanged(string database, OrganisationUnitChangeset changeset) { }
        public void NotifyDMetadataChanged(string database, DMetadataChangeset changeset) { }
        public void NotifyConnectionCountFromServer(int current, int max) { }
        public void NotifyReservationsChanged() { }
        public void NotifyIndexingStateChanged(string database, ushort state) { }
        public void NotifySessionsFromServer(int current, int max, int reserved, Guid licenseId) { }
        public void NotifyLicenseChanged() { }
    }
}
