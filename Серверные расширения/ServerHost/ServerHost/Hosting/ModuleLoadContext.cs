using System.Reflection;
using System.Runtime.Loader;

namespace ServerHost.Hosting;

/// <summary>
/// Выгружаемый контекст одного модуля. Сборки, которые есть у сервера, берутся из основного контекста
/// (иначе типы Pilot SDK не совпадут), остальные - из папки модуля, а если там нет - из папок других модулей
/// (режим «общий модуль»: все библиотеки лежат в одном модуле, остальные содержат только свою *.servext.dll).
/// Файлы читаются в память, чтобы папку можно было заменить при обновлении.
/// </summary>
internal sealed class ModuleLoadContext : AssemblyLoadContext
{
    private readonly string _directory;
    private readonly ServerAssemblies _server;
    private readonly Func<string, string?>? _shared;

    /// <param name="shared">Путь к dll с этим именем в папке другого модуля; null - не искать.</param>
    public ModuleLoadContext(string moduleName, string directory, ServerAssemblies server, Func<string, string?>? shared = null)
        : base("cicd:" + moduleName, isCollectible: true)
    {
        _directory = directory;
        _server = server;
        _shared = shared;
    }

    /// <summary>Главная сборка модуля грузится явно - даже если сборка с тем же именем уже есть в основном контексте.</summary>
    public Assembly LoadMain(string path) => LoadFile(path);

    protected override Assembly? Load(AssemblyName name)
    {
        if (name.Name is null || _server.Contains(name.Name))
            return null;
        var path = Path.Combine(_directory, name.Name + ".dll");
        if (File.Exists(path))
            return LoadFile(path);
        return _shared?.Invoke(name.Name) is { } other ? LoadFile(other) : null;
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        foreach (var candidate in new[] { unmanagedDllName, unmanagedDllName + ".so", "lib" + unmanagedDllName + ".so", unmanagedDllName + ".dll" })
        {
            var path = Path.Combine(_directory, candidate);
            if (File.Exists(path))
                return LoadUnmanagedDllFromPath(path);
        }
        return IntPtr.Zero;
    }

    private Assembly LoadFile(string path)
    {
        using var assembly = new MemoryStream(File.ReadAllBytes(path));
        var pdbPath = Path.ChangeExtension(path, ".pdb");
        if (!File.Exists(pdbPath))
            return LoadFromStream(assembly);
        using var pdb = new MemoryStream(File.ReadAllBytes(pdbPath));
        return LoadFromStream(assembly, pdb);
    }
}
