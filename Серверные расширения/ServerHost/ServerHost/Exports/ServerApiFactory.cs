using System.ComponentModel.Composition;
using System.Text.Json;
using Ascon.Pilot.DataModifier;
using Ascon.Pilot.ServerExtensions.SDK;
using ServerHost.Contract;
using ServerHost.Hosting;

namespace ServerHost.Exports;

/// <summary>
/// Выставляет клиентам базы API <see cref="IServerModulesApi"/> (и API, которые выставляют сами модули).
/// Pilot вызывает TryCreate для каждого подключения, запросившего незнакомый интерфейс.
/// </summary>
[Export(typeof(IServerApiFactory))]
public sealed class ServerApiFactory : IServerApiFactory
{
    [ImportingConstructor]
    public ServerApiFactory([Import(AllowDefault = true)] IMetricsEvents? metrics)
    {
        ModuleHost.Instance.RegisterHostService(metrics);
    }

    public bool TryCreate(string interfaceName, IServerApiServiceContainer container, out object api)
    {
        if (interfaceName == nameof(IServerModulesApi))
        {
            api = new ServerModulesApi(ModuleHost.Instance, container.GetService<IModifierBackend>());
            return true;
        }
        if (ModuleHost.Instance.TryCreateModuleApi(interfaceName, container, out var moduleApi) && moduleApi is not null)
        {
            api = moduleApi;
            return true;
        }
        api = null!;
        return false;
    }
}

/// <summary>Реализация API на одно подключение: проверка прав и разбор JSON-запросов.</summary>
internal sealed class ServerModulesApi : IServerModulesApi
{
    private readonly ModuleHost _host;
    private readonly IModifierBackend _backend;

    public ServerModulesApi(ModuleHost host, IModifierBackend backend)
    {
        _host = host;
        _backend = backend;
    }

    public async Task<string> InvokeAsync(string requestJson)
    {
        try
        {
            var person = RequireAdmin();
            var request = JsonSerializer.Deserialize<HostRequest>(requestJson ?? "", ModuleStorage.Json) ?? new HostRequest();
            var actor = string.IsNullOrWhiteSpace(request.Actor) ? person : $"{request.Actor} / {person}";
            return Serialize(request.Op switch
            {
                HostOps.State => new HostResponse { Ok = true, State = _host.GetState() },
                HostOps.BeginUpload => new HostResponse { Ok = true, UploadId = _host.BeginUpload(request.FileName ?? "module.zip", request.Size) },
                HostOps.Validate => Report(await _host.ValidateAsync(request.UploadId ?? "", request.StripServerAssemblies)),
                HostOps.Install => Module(await _host.InstallAsync(request.UploadId ?? "", actor, request.Reason)),
                HostOps.SetEnabled => Module(await _host.SetEnabledAsync(RequireName(request), request.Enabled, actor)),
                HostOps.Reload => Module(await _host.ReloadAsync(RequireName(request), actor)),
                HostOps.Rollback => Module(await _host.RollbackAsync(RequireName(request), actor)),
                HostOps.Remove => await Removed(RequireName(request), actor),
                _ => new HostResponse { Error = $"Неизвестная операция «{request.Op}»." },
            });
        }
        catch (HostException ex)
        {
            return Serialize(new HostResponse { Error = ex.Message });
        }
        catch (Exception ex)
        {
            _host.Log("error", $"API: {ex.GetType().Name}: {ex.Message}");
            return Serialize(new HostResponse { Error = $"{ex.GetType().Name}: {ex.Message}" });
        }
    }

    public Task<string> UploadChunkAsync(string uploadId, byte[] data)
    {
        try
        {
            RequireAdmin();
            _host.AppendChunk(uploadId, data ?? Array.Empty<byte>());
            return Task.FromResult(Serialize(new HostResponse { Ok = true, UploadId = uploadId }));
        }
        catch (Exception ex) when (ex is HostException or UnauthorizedAccessException)
        {
            return Task.FromResult(Serialize(new HostResponse { Error = ex.Message }));
        }
    }

    private string RequireAdmin()
    {
        var person = _backend.CurrentPerson();
        if (person is null || !person.IsAdmin)
            throw new HostException("Управлять серверными модулями может только администратор базы.");
        return person.DisplayName;
    }

    private static string RequireName(HostRequest request) =>
        string.IsNullOrWhiteSpace(request.Name) ? throw new HostException("Не указано имя модуля.") : request.Name.Trim();

    private static HostResponse Report(ValidationReport report) => new() { Ok = report.CanInstall, Report = report, UploadId = report.UploadId };

    private static HostResponse Module((ModuleState Module, bool Rebind) result) =>
        new() { Ok = result.Module.Error is null, Module = result.Module, RebindRequired = result.Rebind, Error = result.Module.Error };

    private async Task<HostResponse> Removed(string name, string actor)
    {
        await _host.RemoveAsync(name, actor);
        return new HostResponse { Ok = true, Message = $"Модуль {name} удалён." };
    }

    private static string Serialize(HostResponse response) => JsonSerializer.Serialize(response, ModuleStorage.Json);
}
