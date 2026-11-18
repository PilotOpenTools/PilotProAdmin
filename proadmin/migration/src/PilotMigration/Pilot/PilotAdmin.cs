using Ascon.Pilot.Common;
using Ascon.Pilot.DataClasses;
using Ascon.Pilot.Server.Api;
using Ascon.Pilot.Server.Api.Contracts;
using PilotMigration.Config;

namespace PilotMigration.Pilot;

/// <summary>Админский API Pilot-Server: сведения о базе, остановка / запуск, метаданные. Одно короткое подключение на операцию.</summary>
public sealed class PilotAdmin
{
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromMinutes(3);

    private readonly ILogger<PilotAdmin> _logger;

    public PilotAdmin(ILogger<PilotAdmin> logger) => _logger = logger;

    public static (string ServerUrl, string Database) ParseUrl(string pilotUrl)
    {
        if (!Uri.TryCreate(pilotUrl, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
            throw new MigrationException($"Некорректный адрес Pilot «{pilotUrl}». Пример: http://pilot-server-test:5545/pilot-ice_ru");
        var database = Uri.UnescapeDataString(uri.AbsolutePath.Trim('/'));
        if (database.Length == 0 || database.Contains('/'))
            throw new MigrationException($"В адресе «{pilotUrl}» должна быть указана база: http://host:5545/имя_базы");
        return (uri.GetLeftPart(UriPartial.Authority), database);
    }

    public Task<AdminDatabaseInfo> GetDatabaseAsync(EnvConfig env, CancellationToken ct) =>
        RunAsync(env, async (api, database) => await FindAsync(api, database), ct);

    public Task<DMetadata> GetMetadataAsync(EnvConfig env, CancellationToken ct) =>
        RunAsync(env, (api, database) => api.GetMetadataAsync(database, 0), ct);

    /// <summary>Заменяет список автоматизаций базы. Возвращает число автоматизаций после замены.</summary>
    public Task<int> ReplaceAutomationsAsync(EnvConfig env, IReadOnlyList<AutomationScript> automations, CancellationToken ct) =>
        RunAsync(env, async (api, database) =>
        {
            var metadata = await api.GetMetadataAsync(database, 0);
            metadata.AutomationConfig.Clear();
            metadata.AutomationConfig.AddRange(automations);
            await api.UpdateMetadataAsync(database, metadata);
            return automations.Count;
        }, ct);

    /// <summary>Переводит базу в состояние и ждёт, пока сервер его сообщит.</summary>
    public async Task SetStateAndWaitAsync(EnvConfig env, DatabaseState state, TimeSpan timeout, Action<string> log, CancellationToken ct)
    {
        await RunAsync(env, async (api, database) =>
        {
            await api.SetDatabaseStateAsync(database, (int)state);
            return true;
        }, ct);

        var deadline = DateTimeOffset.UtcNow + timeout;
        DatabaseState? last = null;
        while (true)
        {
            var info = await GetDatabaseAsync(env, ct);
            if (info.State != last)
            {
                log($"База «{info.Name}»: {StateTitle(info.State)}" + (string.IsNullOrEmpty(info.StateReason) ? "" : $" ({info.StateReason})"));
                last = info.State;
            }
            if (info.State == state)
                return;
            if (info.State == DatabaseState.Broken)
                throw new MigrationException($"Сервер Pilot перевёл базу «{info.Name}» в состояние «Повреждена»: {info.StateReason ?? info.InitializingError}");
            if (DateTimeOffset.UtcNow > deadline)
                throw new MigrationException($"База «{info.Name}» не перешла в состояние «{StateTitle(state)}» за {timeout.TotalMinutes:0} мин (сейчас «{StateTitle(info.State)}»).");
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }
    }

    public Task AttrSearchRebuildIndexAsync(EnvConfig env, CancellationToken ct) =>
        RunAsync(env, async (api, database) =>
        {
            await api.AttrSearchRebuildIndexAsync(database);
            return true;
        }, ct);

    public static string StateTitle(DatabaseState state) => state switch
    {
        DatabaseState.Launched => "запущена",
        DatabaseState.Stopped => "остановлена",
        DatabaseState.Broken => "повреждена",
        DatabaseState.Updating => "обновляется",
        DatabaseState.Indexing => "индексируется",
        DatabaseState.Initializing => "инициализируется",
        DatabaseState.Importing => "импортируется",
        DatabaseState.MarkedForDeletion => "помечена на удаление",
        _ => state.ToString(),
    };

    private static async Task<AdminDatabaseInfo> FindAsync(IServerAdminAsyncApi api, string database)
    {
        var list = await api.GetDatabaseInfoListAsync();
        return list.FirstOrDefault(d => string.Equals(d.Name, database, StringComparison.OrdinalIgnoreCase))
               ?? throw new MigrationException($"База «{database}» не найдена на сервере. Есть: {string.Join(", ", list.Select(d => d.Name))}.");
    }

    private async Task<T> RunAsync<T>(EnvConfig env, Func<IServerAdminAsyncApi, string, Task<T>> action, CancellationToken ct)
    {
        if (!env.IsConfigured)
            throw new MigrationException($"Для контура «{env.Title}» не задан адрес сервера Pilot.");
        if (string.IsNullOrEmpty(env.PilotAdminLogin))
            throw new MigrationException($"Для контура «{env.Title}» не задан администратор сервера Pilot.");

        var (serverUrl, database) = ParseUrl(env.PilotUrl);
        HttpPilotClient? client = null;
        try
        {
            var work = Task.Run(async () =>
            {
                client = new HttpPilotClient(serverUrl);
                client.Connect(false);
                client.GetAuthenticationApi().LoginServerAdministrator(env.PilotAdminLogin, env.PilotAdminPassword.EncryptAes(), false);
                var api = client.GetServerAdminAsyncApi(new NullAdminCallback());
                return await action(api, database);
            }, ct);
            return await work.WaitAsync(OperationTimeout, ct);
        }
        catch (MigrationException)
        {
            throw;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Pilot {Url}", env.PilotUrl);
            throw new MigrationException($"Сервер Pilot «{env.Title}» ({serverUrl}): {Describe(ex)}", ex);
        }
        finally
        {
            if (client is not null)
            {
                try { client.Disconnect(); } catch { /* уже закрыто */ }
                try { client.Dispose(); } catch { /* уже закрыто */ }
            }
        }
    }

    private static string Describe(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            var name = e.GetType().Name;
            if (e is PilotSecurityAccessDeniedException or UnauthorizedAccessException || name.Contains("AccessDenied") || name.Contains("Authentication"))
                return "неверный логин или пароль администратора сервера.";
            if (e is HttpRequestException or System.Net.Sockets.SocketException or TimeoutException)
                return "сервер недоступен: " + e.Message;
        }
        return ex.Message;
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
