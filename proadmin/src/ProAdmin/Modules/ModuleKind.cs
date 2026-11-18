using ProAdmin.Pilot;
using ProAdmin.Settings;

namespace ProAdmin.Modules;

/// <summary>
/// Вид модуля. Схема веток и контуров у обоих одна (dev / тест, release/X.Y.Z / стейдж, main / прод),
/// различаются группа GitLab, главная сборка, шаблон CI и куда пишется zip (<see cref="IModuleTarget"/>).
/// </summary>
public enum ModuleKind
{
    /// <summary>Клиентский модуль: *.ext2.dll, объект Extension в базе.</summary>
    Client,
    /// <summary>Серверный модуль: *.servext.dll, устанавливается в ServerHost без перезапуска сервера.</summary>
    Server,
}

public static class ModuleKinds
{
    public static ModuleKind Parse(string? value) =>
        string.Equals(value, "server", StringComparison.OrdinalIgnoreCase) ? ModuleKind.Server : ModuleKind.Client;

    /// <summary>Значение параметра kind в адресах страниц (null - клиентские, параметр не нужен).</summary>
    public static string? Query(this ModuleKind kind) => kind == ModuleKind.Server ? "server" : null;

    public static string Title(this ModuleKind kind) => kind == ModuleKind.Server ? "Серверные модули" : "Модули";

    /// <summary>Модуль в родительном/именительном для журнала: «серверный модуль» / «модуль».</summary>
    public static string Noun(this ModuleKind kind) => kind == ModuleKind.Server ? "серверный модуль" : "модуль";

    /// <summary>Расширение главной сборки без .dll.</summary>
    public static string AssemblySuffix(this ModuleKind kind) => kind == ModuleKind.Server ? ".servext" : ".ext2";

    public static string? GroupPath(this ModuleKind kind, AdminSettings s) =>
        kind == ModuleKind.Server ? s.ServerModulesGroupPath : s.ModulesGroupPath;

    public static string GroupUrl(this ModuleKind kind, AdminSettings s) =>
        kind == ModuleKind.Server ? s.ServerModulesGitLabGroupUrl : s.ModulesGitLabGroupUrl;

    /// <summary>Ключ операций в журнале: у серверных свой, чтобы операции разных видов не блокировали друг друга.</summary>
    public static string JobEnv(this ModuleKind kind, string env) => (kind == ModuleKind.Server ? "server-module:" : "module:") + env;

    public static string JobEnvTitle(this ModuleKind kind, string envTitle) =>
        $"{envTitle} ({(kind == ModuleKind.Server ? "серверные модули" : "модули")})";

    /// <summary>Имя сборки без суффикса вида и номера в начале: 087OpenStoradge.ext2 / OpenStoradge.</summary>
    public static string CoreName(string assemblyName)
    {
        foreach (var suffix in new[] { ".ext2", ".servext" })
            if (assemblyName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                assemblyName = assemblyName[..^suffix.Length];
        return assemblyName.TrimStart("0123456789".ToCharArray());
    }
}

/// <summary>
/// Куда выкладывается zip модуля на сервере контура. Клиентские - объект Extension (<see cref="PilotModuleGateway"/>),
/// серверные - центральный модуль ServerHost (<see cref="Pilot.ServerModules.ServerModuleTarget"/>).
/// Модуль сервера описывается тем же <see cref="PilotModule"/>: имя, версия, причина последней выкладки (с коммитом).
/// </summary>
public interface IModuleTarget
{
    ModuleKind Kind { get; }

    Task<PilotModuleList> ListAsync(string url, PilotLogin login, int licenseType, CancellationToken ct);

    /// <summary>Что нужно для отката к текущей версии (клиентские - zip; серверные - хост хранит предыдущую сам, пустой массив).</summary>
    Task<byte[]?> DownloadAsync(string url, PilotLogin login, int licenseType, PilotModule module, CancellationToken ct);

    Task UpdateAsync(string url, PilotLogin login, int licenseType, Guid moduleId, ModulePackageInfo package, string reason, CancellationToken ct);

    Task<Guid> CreateAsync(string url, PilotLogin login, int licenseType, Guid folderId, ModulePackageInfo package, string reason, CancellationToken ct);

    Task RestoreAsync(string url, PilotLogin login, int licenseType, PilotModule previous, byte[] zip, string reason, CancellationToken ct);

    Task MoveToRecycleBinAsync(string url, PilotLogin login, int licenseType, Guid moduleId, CancellationToken ct);

    /// <summary>
    /// Сборки модуля на сервере (для таблицы и проверки библиотек общего модуля): клиентские - из zip объекта,
    /// серверные - из состояния ServerHost. null - модуль без файла.
    /// </summary>
    Task<IReadOnlyList<PackageAssembly>?> AssembliesAsync(string url, PilotLogin login, int licenseType, PilotModule module, CancellationToken ct);

    /// <summary>Когда новая версия начинает работать (для журнала).</summary>
    string AfterUpdateNote { get; }

    /// <summary>Предупреждение после создания нового модуля на сервере; null - не нужно.</summary>
    string? AfterCreateWarning(string extensionName, string envTitle);
}

/// <summary>Цель выкладки по виду модуля (обе - singletons DI).</summary>
public sealed class ModuleTargets(PilotModuleGateway client, Pilot.ServerModules.ServerModuleTarget server)
{
    public IModuleTarget Get(ModuleKind kind) => kind == ModuleKind.Server ? server : client;
}
