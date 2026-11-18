using System.Reflection;
using System.Runtime.Loader;

namespace ServerHost.Hosting;

/// <summary>
/// Сборки, которые уже есть у Pilot-Server: платформа .NET, сборки приложения (/App) и всё, что загружено в
/// основной контекст. Модули получают их из основного контекста (общие типы Pilot, MEF и т. п.),
/// поэтому класть такие сборки в архив модуля бессмысленно.
/// </summary>
internal sealed class ServerAssemblies
{
    private readonly Dictionary<string, Version?> _byName;

    public ServerAssemblies(IEnumerable<(string Name, Version? Version)> assemblies)
    {
        _byName = new Dictionary<string, Version?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, version) in assemblies)
            if (!_byName.TryGetValue(name, out var known) || (version is not null && (known is null || version > known)))
                _byName[name] = version;
    }

    public bool Contains(string name) => _byName.ContainsKey(name);

    public bool TryGet(string name, out Version? version) => _byName.TryGetValue(name, out version);

    /// <summary>Снимок текущего процесса: TPA-список (платформа + сборки приложения) и основной контекст.</summary>
    public static ServerAssemblies Capture(IEnumerable<string>? exclude = null)
    {
        var skip = new HashSet<string>(exclude ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        var list = new List<(string, Version?)>();

        if (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") is string tpa)
        {
            foreach (var path in tpa.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                var name = Path.GetFileNameWithoutExtension(path);
                if (skip.Contains(name))
                    continue;
                list.Add((name, TryReadVersion(path)));
            }
        }

        foreach (var asm in AssemblyLoadContext.Default.Assemblies)
        {
            var name = asm.GetName();
            if (name.Name is null || skip.Contains(name.Name))
                continue;
            list.Add((name.Name, name.Version));
        }
        return new ServerAssemblies(list);
    }

    private static Version? TryReadVersion(string path)
    {
        try { return AssemblyName.GetAssemblyName(path).Version; }
        catch { return null; }
    }
}
