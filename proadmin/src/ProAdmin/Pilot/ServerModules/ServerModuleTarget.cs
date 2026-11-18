using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using ProAdmin.Modules;

namespace ProAdmin.Pilot.ServerModules;

/// <summary>
/// Серверные модули как цель выкладки CI/CD: zip ставится в ServerHost (загрузка / проверка с удалением
/// серверных сборок / установка), новая версия работает сразу. Предыдущую версию хост хранит сам - откат = rollback.
/// Модуль хоста отображается в <see cref="PilotModule"/>: Id - производный от имени, Additional - «Версия X»,
/// Reason - причина установки (в ней коммит «ver · ref @ sha - notes», по нему видно, что выложено).
/// </summary>
public sealed class ServerModuleTarget : IModuleTarget
{
    private readonly PilotServerModuleGateway _host;
    private readonly ILogger<ServerModuleTarget> _logger;
    private readonly ConcurrentDictionary<Guid, string> _nameById = new();

    public ServerModuleTarget(PilotServerModuleGateway host, ILogger<ServerModuleTarget> logger)
    {
        _host = host;
        _logger = logger;
    }

    public ModuleKind Kind => ModuleKind.Server;

    public string AfterUpdateNote => "Новая версия работает сразу, без перезапуска сервера.";

    public string? AfterCreateWarning(string extensionName, string envTitle) => null;

    /// <summary>Устойчивый Id модуля хоста по имени (у модулей хоста нет объекта в базе).</summary>
    public static Guid IdFor(string name) =>
        new(MD5.HashData(Encoding.UTF8.GetBytes("server-module:" + name.ToLowerInvariant())));

    public async Task<PilotModuleList> ListAsync(string url, PilotLogin login, int licenseType, CancellationToken ct)
    {
        var state = await _host.GetStateAsync(url, login, licenseType, ct);
        var list = new PilotModuleList { RootFolderId = Guid.Empty };
        foreach (var m in state.Modules)
        {
            var id = IdFor(m.Name);
            _nameById[id] = m.Name;
            list.Modules.Add(new PilotModule(id, m.Name, $"Версия {ModuleVersion.Display(m.Version)}", "", Guid.Empty,
                m.Name + ".zip", null, 0, m.Reason, m.InstalledAt == default ? null : DateTime.SpecifyKind(m.InstalledAt, DateTimeKind.Utc)));
        }
        return list;
    }

    /// <summary>Предыдущую версию хранит хост - zip не нужен (непустой ответ включает откат через rollback).</summary>
    public Task<byte[]?> DownloadAsync(string url, PilotLogin login, int licenseType, PilotModule module, CancellationToken ct) =>
        Task.FromResult<byte[]?>(Array.Empty<byte>());

    public async Task<IReadOnlyList<PackageAssembly>?> AssembliesAsync(string url, PilotLogin login, int licenseType, PilotModule module, CancellationToken ct)
    {
        var state = await _host.GetStateAsync(url, login, licenseType, ct);
        var m = state.Modules.FirstOrDefault(x => string.Equals(x.Name, module.Name, StringComparison.OrdinalIgnoreCase));
        if (m is null)
            return null;
        var main = m.Name + ".servext";
        // Старый хост не присылает версии - только имена файлов.
        var list = m.Assemblies.Count > 0
            ? m.Assemblies.Select(a => (a.Name, Version: a.Version.Length > 0 ? a.Version : null))
            : m.Files.Where(f => f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)).Select(f => (Name: Path.GetFileNameWithoutExtension(f.Replace('\\', '/').Split('/').Last()), Version: (string?)null));
        return list.Select(a => new PackageAssembly(a.Name + ".dll", 0, a.Version is not null, a.Name, a.Version, null,
                string.Equals(a.Name, main, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(a => a.IsMain).ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public async Task UpdateAsync(string url, PilotLogin login, int licenseType, Guid moduleId, ModulePackageInfo package, string reason, CancellationToken ct) =>
        await InstallAsync(url, login, licenseType, package, reason, ct);

    public async Task<Guid> CreateAsync(string url, PilotLogin login, int licenseType, Guid folderId, ModulePackageInfo package, string reason, CancellationToken ct) =>
        IdFor(await InstallAsync(url, login, licenseType, package, reason, ct));

    public async Task RestoreAsync(string url, PilotLogin login, int licenseType, PilotModule previous, byte[] zip, string reason, CancellationToken ct) =>
        Check(await _host.InvokeAsync(url, login, licenseType,
            new HostRequest { Op = HostOps.Rollback, Name = previous.Name, Reason = reason, Actor = "CI/CD" }, ct), $"откат {previous.Name}");

    public async Task MoveToRecycleBinAsync(string url, PilotLogin login, int licenseType, Guid moduleId, CancellationToken ct)
    {
        if (!_nameById.TryGetValue(moduleId, out var name))
            throw new PilotGatewayException(PilotErrorKind.Other, $"Неизвестный серверный модуль {moduleId}: удалите его на странице «Серверные модули».");
        Check(await _host.InvokeAsync(url, login, licenseType, new HostRequest { Op = HostOps.Remove, Name = name, Actor = "CI/CD" }, ct), $"удаление {name}");
    }

    /// <summary>Загрузка, проверка (серверные сборки выбрасываются), установка, привязка новых активностей. Возвращает имя модуля.</summary>
    private async Task<string> InstallAsync(string url, PilotLogin login, int licenseType, ModulePackageInfo package, string reason, CancellationToken ct)
    {
        var validated = await _host.UploadAndValidateAsync(url, login, licenseType, package.FileName, package.Zip, stripServerAssemblies: true, ct);
        var report = validated.Report;
        if (report is null)
            throw new PilotGatewayException(PilotErrorKind.Other, $"ServerHost не проверил архив: {validated.Error ?? "нет отчёта"}");
        if (!report.CanInstall)
            throw new PilotGatewayException(PilotErrorKind.Other,
                $"ServerHost не принял архив {package.FileName}: " + string.Join("; ", report.Errors.DefaultIfEmpty("причина не указана")));

        var installed = Check(await _host.InvokeAsync(url, login, licenseType,
            new HostRequest { Op = HostOps.Install, UploadId = report.UploadId, Reason = reason, Actor = "CI/CD" }, ct), $"установка {report.ModuleName}");

        if (installed.RebindRequired && await _host.RebindAutomationsAsync(url, login, ct) is { } rebindError)
            _logger.LogWarning("Новые активности {Module} заработают после изменения автоматизаций: {Error}", report.ModuleName, rebindError);
        return installed.Module?.Name ?? report.ModuleName;
    }

    private static HostResponse Check(HostResponse response, string what) =>
        response.Error is null ? response : throw new PilotGatewayException(PilotErrorKind.Other, $"ServerHost, {what}: {response.Error}");
}
