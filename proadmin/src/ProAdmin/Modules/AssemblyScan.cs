using System.Collections.Concurrent;
using ProAdmin.Pilot;

namespace ProAdmin.Modules;

/// <summary>Модуль сервера и его сборки.</summary>
public sealed record ScannedModule(PilotModule Module, IReadOnlyList<PackageAssembly> Assemblies, string? MainAssembly, string? Error);

/// <summary>Сборка по всем модулям сервера: какие версии и в каких модулях лежат.</summary>
public sealed record ScannedAssembly(string Name, IReadOnlyList<(ScannedModule Module, PackageAssembly Assembly)> Uses)
{
    public IReadOnlyList<string> AssemblyVersions => Uses.Select(u => u.Assembly.AssemblyVersion ?? "native").Distinct().OrderBy(v => v).ToList();
    public IReadOnlyList<string> FileVersions => Uses.Select(u => u.Assembly.FileVersion).Where(v => v is not null).Select(v => v!).Distinct().OrderBy(v => v).ToList();

    /// <summary>В разных модулях - разные AssemblyVersion: какая загрузится, зависит от порядка загрузки модулей.</summary>
    public bool Conflicts => AssemblyVersions.Count > 1;
}

public sealed class AssemblyScanResult
{
    public required ModuleEnv Env { get; init; }
    public required DateTimeOffset At { get; init; }
    public required IReadOnlyList<ScannedModule> Modules { get; init; }

    /// <summary>Сторонние сборки (без главных сборок модулей), по имени.</summary>
    public IReadOnlyList<ScannedAssembly> Assemblies =>
        Modules.SelectMany(m => m.Assemblies.Where(a => !a.IsMain).Select(a => (m, a)))
            .GroupBy(x => x.a.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => new ScannedAssembly(g.First().a.Name, g.ToList()))
            .OrderByDescending(a => a.Uses.Count).ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
}

/// <summary>
/// Состав сборок всех клиентских модулей сервера контура: zip скачиваются с сервера (кэш по телу файла в шлюзе),
/// результат держится 10 минут - чтобы правка спецификации не требовала повторного скачивания.
/// </summary>
public sealed class AssemblyScanService(PilotModuleGateway gateway, ModuleTargets targets)
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(10);
    private readonly ConcurrentDictionary<string, AssemblyScanResult> _cache = new();

    public AssemblyScanResult? Cached(ModuleEnv env) =>
        _cache.TryGetValue(env.PilotUrl, out var r) && DateTimeOffset.UtcNow - r.At < CacheTtl ? r : null;

    public async Task<AssemblyScanResult> ScanAsync(ModuleEnv env, PilotLogin login, int licenseType, bool fresh, CancellationToken ct)
    {
        if (!fresh && Cached(env) is { } cached)
            return cached;

        var list = await gateway.ListAsync(env.PilotUrl, login, licenseType, ct);
        var modules = new List<ScannedModule>();
        foreach (var module in list.Modules.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var zip = await gateway.DownloadAsync(env.PilotUrl, login, licenseType, module, ct);
                if (zip is null)
                {
                    modules.Add(new ScannedModule(module, Array.Empty<PackageAssembly>(), null, "у объекта нет файла модуля"));
                    continue;
                }
                string? main = null;
                try { main = ModulePackage.Analyze(zip, module.FileName ?? module.Name + ".zip").AssemblyName; }
                catch (InvalidDataException) { /* нет *.ext2.dll - покажем всё как сторонние */ }
                modules.Add(new ScannedModule(module, ModulePackage.ListAssemblies(zip, main), main, null));
            }
            catch (Exception ex) when (ex is InvalidDataException or PilotGatewayException)
            {
                modules.Add(new ScannedModule(module, Array.Empty<PackageAssembly>(), null, ex.Message));
            }
        }

        var result = new AssemblyScanResult { Env = env, At = DateTimeOffset.UtcNow, Modules = modules };
        _cache[env.PilotUrl] = result;
        return result;
    }

    private static readonly TimeSpan SharedTtl = TimeSpan.FromMinutes(2);
    private readonly ConcurrentDictionary<string, (SharedModuleState State, DateTimeOffset At)> _shared = new();

    /// <summary>
    /// Общий модуль на сервере контура (клиентский - список модулей + один zip, серверный - состояние ServerHost),
    /// кэш 2 минуты; null - режим общего модуля выключен.
    /// </summary>
    public async Task<SharedModuleState?> SharedOnEnvAsync(AssemblySpec spec, ModuleEnv env, PilotLogin login, int licenseType, CancellationToken ct,
        ModuleKind kind = ModuleKind.Client)
    {
        if (!spec.IsShared)
            return null;
        var key = kind + "|" + env.PilotUrl + "|" + spec.SharedModule;
        if (_shared.TryGetValue(key, out var c) && DateTimeOffset.UtcNow - c.At < SharedTtl)
            return c.State;
        var target = targets.Get(kind);
        var list = await target.ListAsync(env.PilotUrl, login, licenseType, ct);
        var state = await LoadSharedAsync(spec, list.Modules, m => target.AssembliesAsync(env.PilotUrl, login, licenseType, m, ct))
                    ?? SharedModuleState.Missing;
        _shared[key] = (state, DateTimeOffset.UtcNow);
        return state;
    }

    public static AssemblyCheckResult Check(AssemblySpec spec, ScannedModule m, SharedModuleState? shared = null) =>
        AssemblyChecker.Check(spec, m.Assemblies, new[] { m.Module.Name, m.MainAssembly }, shared);

    /// <summary>Общий модуль спецификации среди модулей сервера; null - режим общего модуля выключен.</summary>
    public static SharedModuleState? Shared(AssemblySpec spec, AssemblyScanResult scan)
    {
        if (!spec.IsShared)
            return null;
        var m = scan.Modules.FirstOrDefault(x => IsShared(spec, x.Module, x.MainAssembly));
        return m is null ? SharedModuleState.Missing : new SharedModuleState(m.Module.Name, m.Module.Version, m.Module.Folder, m.Assemblies);
    }

    public static bool IsShared(AssemblySpec spec, PilotModule module, string? mainAssembly = null) =>
        AssemblyChecker.IsSharedModule(spec, AssemblyChecker.Keys(new[] { module.Name, mainAssembly }));

    /// <summary>Общий модуль среди модулей сервера со списком его сборок (<paramref name="assemblies"/> - сборки модуля на сервере).</summary>
    public static async Task<SharedModuleState?> LoadSharedAsync(AssemblySpec spec, IEnumerable<PilotModule> modules,
        Func<PilotModule, Task<IReadOnlyList<PackageAssembly>?>> assemblies)
    {
        if (!spec.IsShared)
            return null;
        var m = modules.FirstOrDefault(x => IsShared(spec, x));
        if (m is null)
            return SharedModuleState.Missing;
        return new SharedModuleState(m.Name, m.Version, m.Folder, await assemblies(m) ?? Array.Empty<PackageAssembly>());
    }
}
