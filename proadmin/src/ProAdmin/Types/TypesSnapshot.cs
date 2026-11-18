using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using Ascon.Pilot.DataClasses;

namespace ProAdmin.Types;

/// <summary>
/// Содержимое репозитория типов (или сервера, приведённое к тому же виду): типы, состояния, группы состояний и версия.
/// Пути файлов: types/&lt;имя&gt;.json, states/&lt;имя&gt;.json, state-machines/&lt;заголовок&gt;.json, config.json.
/// </summary>
public sealed partial class TypesSnapshot
{
    public const string TypesDir = "types";
    public const string StatesDir = "states";
    public const string MachinesDir = "state-machines";
    public const string ConfigFile = "config.json";

    /// <summary>Состояние «нет состояния» в переходах группы.</summary>
    public const string NoState = "<Нет>";

    public TypesConfigInfo Config { get; set; } = new();
    public SortedDictionary<string, TypeDoc> Types { get; } = new(StringComparer.Ordinal);
    public SortedDictionary<string, StateDoc> States { get; } = new(StringComparer.Ordinal);
    public List<MachineDoc> Machines { get; } = new();

    /// <summary>Пути, из которых прочитаны элементы (файл группы называется по заголовку и при переименовании переезжает).</summary>
    public Dictionary<object, string> SourcePaths { get; } = new(ReferenceEqualityComparer.Instance);

    /// <summary>Ошибки разбора файлов (дубли, неверный JSON).</summary>
    public List<string> ParseErrors { get; } = new();

    public bool IsEmpty => Types.Count == 0 && States.Count == 0 && Machines.Count == 0;

    public MachineDoc? FindMachine(string? title) =>
        title is null ? null : Machines.FirstOrDefault(m => string.Equals(m.Title, title, StringComparison.Ordinal));

    // ---------------------------------------------------------------- пути

    public static string TypePath(string name) => $"{TypesDir}/{FileName(name)}.json";
    public static string StatePath(string name) => $"{StatesDir}/{FileName(name)}.json";

    public string MachinePath(MachineDoc machine)
    {
        if (SourcePaths.TryGetValue(machine, out var path) &&
            (path == MachineFile(machine.Title, null) || path == MachineFile(machine.Title, machine.Id)))
            return path;
        var plain = MachineFile(machine.Title, null);
        var taken = Machines.Any(m => !ReferenceEquals(m, machine) && PathOf(m) == plain);
        return taken ? MachineFile(machine.Title, machine.Id) : plain;
    }

    private string PathOf(object item, string fallback) => SourcePaths.TryGetValue(item, out var p) ? p : fallback;

    /// <summary>Взять пути файлов у другого снимка для тех же элементов (тип/состояние - по имени, группа - по Id или заголовку).</summary>
    public void AdoptPaths(TypesSnapshot from)
    {
        foreach (var t in Types.Values)
            if (from.Types.TryGetValue(t.Name, out var o) && from.SourcePaths.TryGetValue(o, out var p))
                SourcePaths[t] = p;
        foreach (var x in States.Values)
            if (from.States.TryGetValue(x.Name, out var o) && from.SourcePaths.TryGetValue(o, out var p))
                SourcePaths[x] = p;
        foreach (var m in Machines)
            if ((from.Machines.FirstOrDefault(o => o.Id is not null && o.Id == m.Id) ?? from.FindMachine(m.Title)) is { } o &&
                from.SourcePaths.TryGetValue(o, out var p))
                SourcePaths[m] = p;
    }

    private string PathOf(MachineDoc m) => SourcePaths.TryGetValue(m, out var p) ? p : MachineFile(m.Title, null);

    private static string MachineFile(string title, Guid? id) =>
        $"{MachinesDir}/{FileName(title)}{(id is { } g ? "-" + g.ToString("N")[..8] : "")}.json";

    /// <summary>Имя файла из имени элемента: недопустимые символы заменяются на «_».</summary>
    public static string FileName(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (var c in name.Trim())
            sb.Append(c < 32 || "<>:\"/\\|?*".Contains(c) ? '_' : c);
        var result = sb.ToString().Trim().TrimEnd('.');
        return result.Length == 0 ? "_" : result;
    }

    /// <summary>Что за элемент лежит по пути - для списка изменений коммита.</summary>
    public static (string Kind, string Name)? Describe(string? path)
    {
        if (path is null || !path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            return null;
        var name = Path.GetFileNameWithoutExtension(path);
        if (path.StartsWith(TypesDir + "/", StringComparison.Ordinal)) return ("тип", name);
        if (path.StartsWith(StatesDir + "/", StringComparison.Ordinal)) return ("состояние", name);
        if (path.StartsWith(MachinesDir + "/", StringComparison.Ordinal)) return ("группа состояний", name);
        return null;
    }

    // ---------------------------------------------------------------- чтение и запись

    /// <summary>Разбор файлов репозитория (путь от корня / текст). Чужие файлы игнорируются.</summary>
    public static TypesSnapshot Parse(IEnumerable<KeyValuePair<string, string>> files)
    {
        var snapshot = new TypesSnapshot();
        foreach (var (path, text) in files.OrderBy(f => f.Key, StringComparer.Ordinal))
        {
            try
            {
                if (path == ConfigFile)
                {
                    snapshot.Config = TypesJson.Read<TypesConfigInfo>(text, path);
                }
                else if (InDir(path, TypesDir))
                {
                    var doc = TypesJson.Read<TypeDoc>(text, path);
                    if (string.IsNullOrWhiteSpace(doc.Name))
                        snapshot.ParseErrors.Add($"{path}: не указано имя типа (name).");
                    else if (!snapshot.Types.TryAdd(doc.Name, doc))
                        snapshot.ParseErrors.Add($"{path}: тип «{doc.Name}» уже описан в {snapshot.SourcePaths[snapshot.Types[doc.Name]]}.");
                    else
                        snapshot.SourcePaths[doc] = path;
                }
                else if (InDir(path, StatesDir))
                {
                    var doc = TypesJson.Read<StateDoc>(text, path);
                    if (string.IsNullOrWhiteSpace(doc.Name))
                        snapshot.ParseErrors.Add($"{path}: не указано имя состояния (name).");
                    else if (!snapshot.States.TryAdd(doc.Name, doc))
                        snapshot.ParseErrors.Add($"{path}: состояние «{doc.Name}» уже описано в {snapshot.SourcePaths[snapshot.States[doc.Name]]}.");
                    else
                        snapshot.SourcePaths[doc] = path;
                }
                else if (InDir(path, MachinesDir))
                {
                    var doc = TypesJson.Read<MachineDoc>(text, path);
                    if (string.IsNullOrWhiteSpace(doc.Title))
                    {
                        snapshot.ParseErrors.Add($"{path}: не указан заголовок группы состояний (title).");
                        continue;
                    }
                    if (snapshot.FindMachine(doc.Title) is { } other)
                        snapshot.ParseErrors.Add($"{path}: группа состояний «{doc.Title}» уже описана в {snapshot.SourcePaths[other]}.");
                    snapshot.Machines.Add(doc);
                    snapshot.SourcePaths[doc] = path;
                }
            }
            catch (TypesFormatException ex)
            {
                snapshot.ParseErrors.Add(ex.Message);
            }
        }
        snapshot.Machines.Sort((a, b) => string.Compare(a.Title, b.Title, StringComparison.Ordinal));
        return snapshot;

        static bool InDir(string path, string dir) =>
            path.StartsWith(dir + "/", StringComparison.Ordinal) && path.EndsWith(".json", StringComparison.OrdinalIgnoreCase) &&
            path.IndexOf('/', dir.Length + 1) < 0;
    }

    /// <summary>Разбор zip-архива ветки с общей корневой папкой.</summary>
    public static TypesSnapshot FromArchive(byte[] zip, long maxJsonBytes = long.MaxValue)
    {
        using var archive = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read);
        var files = new List<KeyValuePair<string, string>>();
        foreach (var entry in archive.Entries)
        {
            if (entry.FullName.EndsWith('/'))
                continue;
            var slash = entry.FullName.IndexOf('/');
            var path = slash < 0 ? entry.FullName : entry.FullName[(slash + 1)..];
            if (path != ConfigFile && !path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                continue;
            if (entry.Length > maxJsonBytes)
                throw new TypesFormatException("JSON в архиве репозитория типов превышает допустимый объём.");
            maxJsonBytes -= entry.Length;
            using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
            files.Add(new(path, reader.ReadToEnd()));
        }
        return Parse(files);
    }

    /// <summary>Все файлы элементов и config.json - как они должны лежать в репозитории.</summary>
    public SortedDictionary<string, string> Render()
    {
        var files = new SortedDictionary<string, string>(StringComparer.Ordinal) { [ConfigFile] = TypesJson.Write(Config) };
        foreach (var t in Types.Values) files[PathOf(t, TypePath(t.Name))] = TypesJson.Write(t);
        foreach (var s in States.Values) files[PathOf(s, StatePath(s.Name))] = TypesJson.Write(s);
        foreach (var m in Machines) files[MachinePath(m)] = TypesJson.Write(m);
        return files;
    }

    // ---------------------------------------------------------------- ссылки внутри конфигураций

    [GeneratedRegex(@"\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b")]
    private static partial Regex GuidPattern();

    [GeneratedRegex(@"\{\{(state|machine):([^{}]+)\}\}")]
    public static partial Regex PlaceholderPattern();

    /// <summary>Id состояний и групп / {{state:имя}} / {{machine:заголовок}}; переводы строк - LF.</summary>
    public static string? EncodeReferences(string? configuration, IReadOnlyDictionary<Guid, string> states, IReadOnlyDictionary<Guid, string> machines)
    {
        if (string.IsNullOrEmpty(configuration))
            return null;
        var text = configuration.Replace("\r\n", "\n");
        return GuidPattern().Replace(text, m =>
        {
            var id = Guid.Parse(m.Value);
            if (states.TryGetValue(id, out var s)) return $"{{{{state:{s}}}}}";
            if (machines.TryGetValue(id, out var g)) return $"{{{{machine:{g}}}}}";
            return m.Value;
        });
    }

    /// <summary>Обратная замена для записи на сервер; ненайденные ссылки - в <paramref name="missing"/>.</summary>
    public static string? DecodeReferences(string? configuration, Func<string, Guid?> state, Func<string, Guid?> machine, ICollection<string> missing)
    {
        if (string.IsNullOrEmpty(configuration))
            return configuration;
        return PlaceholderPattern().Replace(configuration, m =>
        {
            var name = m.Groups[2].Value;
            var id = m.Groups[1].Value == "state" ? state(name) : machine(name);
            if (id is { } g)
                return g.ToString();
            missing.Add($"{(m.Groups[1].Value == "state" ? "состояние" : "группа состояний")} «{name}»");
            return m.Value;
        }).Replace("\r\n", "\n").Replace("\n", "\r\n");
    }

    /// <summary>Ссылки из конфигурации: (state|machine, имя).</summary>
    public static IEnumerable<(string Kind, string Name)> References(string? configuration) =>
        string.IsNullOrEmpty(configuration)
            ? Enumerable.Empty<(string, string)>()
            : PlaceholderPattern().Matches(configuration).Select(m => (m.Groups[1].Value, m.Groups[2].Value));
}
