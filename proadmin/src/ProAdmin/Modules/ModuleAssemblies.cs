using System.IO.Compression;
using System.Reflection.Metadata;
using System.Text;
using System.Reflection.PortableExecutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using ProAdmin.GitLab;
using ProAdmin.Infrastructure;
using Microsoft.Extensions.Options;

namespace ProAdmin.Modules;

/// <summary>Сборка (dll/exe) внутри zip модуля.</summary>
public sealed record PackageAssembly(
    string Path,
    long Size,
    bool IsManaged,
    string? AssemblyName,
    string? AssemblyVersion,
    string? FileVersion,
    bool IsMain,
    IReadOnlyList<AssemblyRef>? References = null,
    IReadOnlyList<string>? Exports = null)
{
    /// <summary>На какие сборки ссылается (для .NET-сборок).</summary>
    public IReadOnlyList<AssemblyRef> Refs => References ?? Array.Empty<AssemblyRef>();

    /// <summary>Контракты [Export(typeof(…))] из Ascon.Pilot.SDK: «IMenu&lt;ObjectsViewContext&gt;», «IDataPlugin».</summary>
    public IReadOnlyList<string> PilotExports => Exports ?? Array.Empty<string>();

    /// <summary>Имя файла без .dll/.exe - по нему сборка ищется в спецификации.</summary>
    public string Name => System.IO.Path.GetFileNameWithoutExtension(Path.Replace('\\', '/').Split('/').Last());

    /// <summary>Другой модуль, положенный внутрь этого (*.ext2.dll / *.servext.dll, кроме главной сборки).</summary>
    public bool IsOtherModule => !IsMain && AssemblyChecker.IsModuleName(Name);
}

/// <summary>Ссылка сборки на другую сборку.</summary>
public sealed record AssemblyRef(string Name, string Version, string? PublicKeyToken)
{
    /// <summary>
    /// Сборка .NET Framework / WPF (ключи Microsoft и ECMA). У части NuGet-пакетов тот же ключ
    /// (System.Runtime.CompilerServices.Unsafe), но они есть в папке клиента Pilot - правило «есть в клиенте Pilot».
    /// </summary>
    public bool IsFramework =>
        Name is "mscorlib" or "netstandard" ||
        PublicKeyToken is "b77a5c561934e089" or "b03f5f7f11d50a3a" or "31bf3856ad364e35";
}

/// <summary>Управление сторонними библиотеками модулей (Настройки / «Библиотеки модулей»), отдельно для клиентских и серверных.</summary>
public enum AssemblySpecMode
{
    /// <summary>
    /// 1. Доп. библиотеки запрещены: все они - в одном общем модуле (клиентский - в папке расширений Preload: клиент Pilot
    /// грузит его раньше остальных, а недостающие dll других модулей ищет и в его папке; серверный - ServerHost ищет
    /// недостающие dll в папках других модулей). В zip остальных модулей - только своя главная сборка, а ссылаться она может
    /// только на библиотеки общего модуля. Нарушение - ошибка CI и запрет выкладки.
    /// </summary>
    SharedModule,
    /// <summary>
    /// 2. Только библиотеки из списка: в zip модуля доп. библиотек быть не должно, ссылаться модуль может только на
    /// разрешённые в списке (в указанных версиях). Нарушение - ошибка CI и запрет выкладки.
    /// </summary>
    AllowList,
    /// <summary>3. Предупреждать: как «только из списка», но нарушение - предупреждение, и библиотеки из списка можно класть в zip.</summary>
    WarnList,
    /// <summary>Прежний режим «каждый модуль со своими библиотеками»: при чтении становится <see cref="WarnList"/>.</summary>
    PerModule,
}

public enum AssemblyRule
{
    /// <summary>Можно класть в модуль (в указанных версиях).</summary>
    Allowed,
    /// <summary>Класть нельзя.</summary>
    Forbidden,
    /// <summary>Есть в папке клиента Pilot - класть не нужно ни в один модуль, клиент отдаст свою.</summary>
    Pilot,
}

/// <summary>Строка спецификации: какая сторонняя сборка допустима в модулях и в каких версиях.</summary>
public sealed class AssemblySpecEntry
{
    /// <summary>Имя файла без .dll: Newtonsoft.Json, System.Memory, x64/SQLite.Interop / SQLite.Interop.</summary>
    public string Name { get; set; } = "";

    public AssemblyRule Rule { get; set; } = AssemblyRule.Allowed;

    /// <summary>Допустимые AssemblyVersion: «13.0.0.0», «6.*», «&gt;=6.0 &lt;7»; несколько вариантов через «;». Пусто - любая.</summary>
    public string AssemblyVersion { get; set; } = "";

    /// <summary>Допустимые версии файла (AssemblyFileVersion), тот же синтаксис. Пусто - любая.</summary>
    public string FileVersion { get; set; } = "";

    /// <summary>Только для этих модулей (имя в Pilot, сборка или репозиторий); пусто - для всех.</summary>
    public List<string> Modules { get; set; } = new();

    public string Comment { get; set; } = "";

    /// <summary>NuGet-пакет и версия, из которых берётся сборка: «Humanizer.Core 2.14.1» (пакет и dll могут называться по-разному).</summary>
    public string Package { get; set; } = "";

    /// <summary>Имя пакета без версии; пусто - пакет не указан.</summary>
    public string PackageId => Package.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";

    /// <summary>Версия пакета («2.14.1»); пусто - не указана.</summary>
    public string PackageVersion => Package.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).FirstOrDefault() ?? "";

    /// <summary>Общая библиотека режима общего модуля: разрешена и не ограничена модулями.</summary>
    public bool IsSharedLibrary => Rule == AssemblyRule.Allowed && Modules.Count == 0;

    public AssemblySpecEntry Clone() => new()
    {
        Name = Name, Rule = Rule, AssemblyVersion = AssemblyVersion, FileVersion = FileVersion,
        Modules = Modules.ToList(), Comment = Comment, Package = Package,
    };
}

public sealed class AssemblySpecChange
{
    public DateTimeOffset At { get; set; }
    public string By { get; set; } = "";
    public string Text { get; set; } = "";
}

/// <summary>
/// Спецификация библиотек модулей одного вида (data/assembly-spec.json - клиентские, assembly-spec-server.json - серверные):
/// режим (<see cref="AssemblySpecMode"/>), общий модуль и список сторонних dll с допустимыми версиями.
/// Главная сборка модуля разрешена всегда.
/// </summary>
public sealed class AssemblySpec
{
    public const string DefaultSharedModule = "Pilot.ClientSdk";
    public const string PreloadFolder = "Preload";

    public AssemblySpecMode Mode { get; set; } = AssemblySpecMode.WarnList;

    /// <summary>Общий модуль для режима <see cref="AssemblySpecMode.SharedModule"/>: имя в Pilot, код в скобках, сборка или репозиторий.</summary>
    public string SharedModule { get; set; } = DefaultSharedModule;

    public bool IsShared => Mode == AssemblySpecMode.SharedModule && SharedModule.Length > 0;

    /// <summary>Режим со списком разрешённых библиотек (2 и 3): список редактируется на странице модулей.</summary>
    public bool IsList => Mode is AssemblySpecMode.AllowList or AssemblySpecMode.WarnList;

    /// <summary>Разрешённые библиотеки списка (без «есть в Pilot» и запрещённых).</summary>
    public IEnumerable<AssemblySpecEntry> Allowed => Entries.Where(e => e.Rule == AssemblyRule.Allowed);

    public List<AssemblySpecEntry> Entries { get; set; } = new();

    public DateTimeOffset? UpdatedAt { get; set; }
    public string? UpdatedBy { get; set; }

    /// <summary>Последние изменения (новые первыми).</summary>
    public List<AssemblySpecChange> History { get; set; } = new();

    public AssemblySpecEntry? Find(string name) =>
        Entries.FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Нарушения - ошибка (CI красный, выкладка запрещена на всех контурах); иначе предупреждение.</summary>
    public bool Blocks => Mode is AssemblySpecMode.SharedModule or AssemblySpecMode.AllowList;

    public void Normalize()
    {
        if (Mode == AssemblySpecMode.PerModule)
            Mode = AssemblySpecMode.WarnList;
        SharedModule = SharedModule?.Trim() ?? "";
        foreach (var e in Entries)
        {
            e.Name = NormalizeName(e.Name);
            e.AssemblyVersion = e.AssemblyVersion?.Trim() ?? "";
            e.FileVersion = e.FileVersion?.Trim() ?? "";
            e.Comment = e.Comment?.Trim() ?? "";
            e.Package = string.Join(' ', (e.Package ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries));
            e.Modules = (e.Modules ?? new()).Select(m => m.Trim()).Where(m => m.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }
        Entries = Entries.Where(e => e.Name.Length > 0)
            .GroupBy(e => e.Name, StringComparer.OrdinalIgnoreCase).Select(g => g.Last())
            .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
        History = (History ?? new()).Take(AssemblySpecStore.HistoryLimit).ToList();
    }

    /// <summary>«x64\SQLite.Interop.dll» / «SQLite.Interop».</summary>
    public static string NormalizeName(string? name)
    {
        var n = (name ?? "").Trim().Replace('\\', '/').Split('/').Last();
        foreach (var ext in new[] { ".dll", ".exe" })
            if (n.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                n = n[..^ext.Length];
        return n.Trim();
    }

    public AssemblySpec Clone() => new()
    {
        Mode = Mode,
        SharedModule = SharedModule,
        Entries = Entries.Select(e => e.Clone()).ToList(),
        UpdatedAt = UpdatedAt,
        UpdatedBy = UpdatedBy,
        History = History.ToList(),
    };

    public static readonly AssemblySpecMode[] Modes = { AssemblySpecMode.SharedModule, AssemblySpecMode.AllowList, AssemblySpecMode.WarnList };

    public static string ModeTitle(AssemblySpecMode m) => m switch
    {
        AssemblySpecMode.SharedModule => "Запретить доп. библиотеки",
        AssemblySpecMode.AllowList => "Разрешать библиотеки только из списка",
        _ => "Предупреждать о библиотеках",
    };

    public static string ModeHint(AssemblySpecMode m) => m switch
    {
        AssemblySpecMode.SharedModule =>
            "Все библиотеки ставит один общий модуль. В zip остальных модулей - только своя главная сборка, " +
            "а ссылаться она может только на библиотеки общего модуля. Нарушение - ошибка CI, выкладка запрещена.",
        AssemblySpecMode.AllowList =>
            "Модуль может ссылаться только на библиотеки из списка (в указанных версиях); в zip доп. библиотек быть не должно. " +
            "Нарушение - ошибка CI, выкладка запрещена.",
        _ =>
            "Как «только из списка», но нарушение - предупреждение (CI жёлтый, выкладка идёт). " +
            "В zip можно класть библиотеки, но только из списка.",
    };

    public static string RuleTitle(AssemblyRule r) => r switch
    {
        AssemblyRule.Forbidden => "запрещена",
        AssemblyRule.Pilot => "есть в клиенте Pilot",
        _ => "разрешена",
    };
}

/// <summary>
/// Шаблон версии: «13.0.0.0» - ровно эта (недостающие части - нули), «13.0.*» - по префиксу,
/// «&gt;=6.0 &lt;7» - диапазон (условия через пробел), варианты через «;» или «|». Пусто - любая версия.
/// </summary>
public static class VersionPattern
{
    public static bool IsAny(string? pattern) => string.IsNullOrWhiteSpace(pattern) || pattern.Trim() == "*";

    public static bool Matches(string? pattern, string? version)
    {
        if (IsAny(pattern))
            return true;
        if (!TryParse(version, out var v))
            return false;
        return pattern!.Split(new[] { ';', '|' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(alt => alt.Split(' ', StringSplitOptions.RemoveEmptyEntries).All(term => MatchTerm(term, v!)));
    }

    /// <summary>Ошибка в шаблоне (для формы); null - шаблон корректен.</summary>
    public static string? Validate(string? pattern)
    {
        if (IsAny(pattern))
            return null;
        foreach (var alt in pattern!.Split(new[] { ';', '|' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        foreach (var term in alt.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var (_, rest) = SplitOp(term);
            var body = rest.EndsWith(".*") ? rest[..^2] : rest == "*" ? "0" : rest;
            if (!TryParse(body, out _))
                return $"«{term}» - не версия (пример: 13.0.0.0, 6.*, >=6.0 <7).";
        }
        return null;
    }

    private static bool MatchTerm(string term, Version v)
    {
        var (op, rest) = SplitOp(term);
        if (op.Length == 0 && rest.EndsWith(".*"))
        {
            var prefix = rest[..^2].Split('.');
            var parts = new[] { v.Major, v.Minor, Math.Max(v.Build, 0), Math.Max(v.Revision, 0) };
            return prefix.Length <= 4 && prefix.Select((p, i) => int.TryParse(p, out var n) && n == parts[i]).All(x => x);
        }
        if (!TryParse(rest, out var target))
            return false;
        var cmp = Normalize(v).CompareTo(Normalize(target!));
        return op switch
        {
            ">=" => cmp >= 0,
            ">" => cmp > 0,
            "<=" => cmp <= 0,
            "<" => cmp < 0,
            _ => cmp == 0,
        };
    }

    private static (string Op, string Value) SplitOp(string term)
    {
        foreach (var op in new[] { ">=", "<=", ">", "<", "=" })
            if (term.StartsWith(op, StringComparison.Ordinal))
                return (op == "=" ? "" : op, term[op.Length..].Trim());
        return ("", term.Trim());
    }

    private static bool TryParse(string? text, out Version? version)
    {
        version = null;
        if (string.IsNullOrWhiteSpace(text))
            return false;
        var t = text.Trim();
        var end = 0;
        while (end < t.Length && (char.IsDigit(t[end]) || t[end] == '.'))
            end++;
        t = t[..end].Trim('.');
        if (!t.Contains('.'))
            t += ".0";
        return Version.TryParse(t, out version);
    }

    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0), Math.Max(v.Revision, 0));
}

public enum AssemblyVerdict
{
    /// <summary>Главная сборка модуля.</summary>
    Main,
    Ok,
    /// <summary>Нет в спецификации - лишняя dll.</summary>
    Unknown,
    /// <summary>В спецификации запрещена.</summary>
    Forbidden,
    /// <summary>Есть в папке клиента Pilot - класть не нужно.</summary>
    ProvidedByPilot,
    /// <summary>Разрешена, но не в этой версии.</summary>
    WrongVersion,
    /// <summary>Разрешена только другим модулям.</summary>
    NotForModule,
    /// <summary>Внутри лежит другой клиентский модуль (*.ext2.dll).</summary>
    OtherModule,
    /// <summary>Режимы 1 и 2: в zip модуля доп. библиотек быть не должно.</summary>
    SharedOnly,
    /// <summary>Модуль ссылается на сборку, которой нет ни в нём, ни в общем модуле, ни в клиенте Pilot.</summary>
    MissingReference,
    /// <summary>Режимы 2 и 3: модуль ссылается на библиотеку не из списка разрешённых (или не в той версии).</summary>
    NotAllowedReference,
    /// <summary>Серверный модуль: сборка есть у Pilot-Server, ServerHost выбросит её при установке (не нарушение).</summary>
    ProvidedByServer,
    /// <summary>Два [Export] одного контракта, который клиент Pilot импортирует в одном экземпляре (меню, тулбар…): не подключится ни один.</summary>
    DuplicateExport,
    /// <summary>Общий модуль: нет на контуре или лежит не в папке Preload.</summary>
    SharedModuleSetup,
}

public sealed record AssemblyCheckItem(PackageAssembly Assembly, AssemblyVerdict Verdict, string Text, AssemblySpecEntry? Entry)
{
    public bool IsProblem => Verdict is not (AssemblyVerdict.Main or AssemblyVerdict.Ok or AssemblyVerdict.ProvidedByServer);

    /// <summary>Что показать в колонке «файл»: путь в zip или ссылка/контракт для проверок не по файлу.</summary>
    public string Subject { get; init; } = Assembly.Path;

    /// <summary>Строка относится к самому файлу (а не к ссылке, контракту или общему модулю).</summary>
    public bool IsFile => Subject == Assembly.Path;
}

public sealed class AssemblyCheckResult
{
    public required IReadOnlyList<AssemblyCheckItem> Items { get; init; }
    public IEnumerable<AssemblyCheckItem> Problems => Items.Where(i => i.IsProblem);
    public bool HasProblems => Items.Any(i => i.IsProblem);

    /// <summary>Сводка для журнала: «нет в спецификации: 2, запрещены: 1».</summary>
    public string Summary
    {
        get
        {
            var parts = new List<string>();
            void Add(AssemblyVerdict v, string text)
            {
                var n = Items.Count(i => i.Verdict == v);
                if (n > 0) parts.Add($"{text}: {n}");
            }
            Add(AssemblyVerdict.SharedOnly, "доп. библиотеки в zip");
            Add(AssemblyVerdict.NotAllowedReference, "ссылки на неразрешённые библиотеки");
            Add(AssemblyVerdict.Unknown, "нет в списке");
            Add(AssemblyVerdict.Forbidden, "запрещены");
            Add(AssemblyVerdict.ProvidedByPilot, "есть в клиенте Pilot");
            Add(AssemblyVerdict.WrongVersion, "не та версия");
            Add(AssemblyVerdict.NotForModule, "не для этого модуля");
            Add(AssemblyVerdict.OtherModule, "чужой модуль внутри");
            Add(AssemblyVerdict.MissingReference, "не найдены зависимости");
            Add(AssemblyVerdict.DuplicateExport, "двойные меню/тулбары");
            Add(AssemblyVerdict.SharedModuleSetup, "общий модуль");
            return parts.Count == 0 ? "библиотеки по правилам" : string.Join(", ", parts);
        }
    }

    /// <summary>Таблица для подробностей журнала.</summary>
    public string Details => string.Join("\n", Items.Select(i =>
        $"{(i.IsProblem ? "X" : "")} {i.Subject}" +
        (i.IsFile
            ? $"  {i.Assembly.AssemblyVersion ?? "native"}" + (i.Assembly.FileVersion is { } f && f != i.Assembly.AssemblyVersion ? $" (файл {f})" : "")
            : "") +
        $" - {i.Text}"));
}

/// <summary>Общий модуль на контуре: где лежит и какие библиотеки в нём (для режима <see cref="AssemblySpecMode.SharedModule"/>).</summary>
public sealed record SharedModuleState(string? Name, string? Version, string? Folder, IReadOnlyList<PackageAssembly> Assemblies)
{
    public bool Found => Name is not null;

    public bool InPreload => Folder is not null &&
                             (Folder == AssemblySpec.PreloadFolder || Folder.StartsWith(AssemblySpec.PreloadFolder + "/", StringComparison.Ordinal));

    public static SharedModuleState Missing { get; } = new(null, null, null, Array.Empty<PackageAssembly>());
}

public static class AssemblyChecker
{
    /// <summary>Контракты Pilot SDK, которые клиент импортирует списком ([ImportMany]) - их в модуле может быть сколько угодно.</summary>
    private static readonly HashSet<string> ManyContracts = new(StringComparer.Ordinal)
    {
        "IDataPlugin", "ICommandHandler", "ISettingsFeature", "ISettingsFeature2", "IObjectChangeHandler", "IObjectChangeProcessor",
        "IObjectCardHandler", "IObjectCardValidationBeforeSubmitHandler", "IWorkflowObjectCardValidationBeforeSubmitHandler",
        "IAutomationActivity", "IHtmlRenderer", "IPdfStamper", "IPilotStorageListener", "IReportExtension", "IXpsAnnotationHandler",
        "IDocumentsExplorerDetailsViewProvider",
    };

    /// <summary>Ключи имён модуля (в Pilot, сборка, код в скобках, репозиторий) - для «только модули» и поиска общего модуля.</summary>
    public static HashSet<string> Keys(IEnumerable<string?> names) =>
        names.Where(n => !string.IsNullOrWhiteSpace(n))
            .SelectMany(n => new[] { n!, ModulePackage.CodeFromName(n!) ?? "" })
            .Where(n => n.Length > 0)
            .SelectMany(n => new[] { ProjectNames.Key(n), ProjectNames.Key(ModuleKinds.CoreName(n)) })
            .Where(k => k.Length > 0).ToHashSet();

    /// <summary>Модуль с этими ключами имён - общий модуль спецификации.</summary>
    public static bool IsSharedModule(AssemblySpec spec, HashSet<string> moduleKeys) =>
        spec.IsShared && Keys(new[] { spec.SharedModule }).Overlaps(moduleKeys);

    /// <summary>Имя главной сборки модуля: клиентского (*.ext2) или серверного (*.servext).</summary>
    public static bool IsModuleName(string name) =>
        name.EndsWith(".ext2", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".servext", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Сборка, которую отдаёт сама платформа: .NET, Pilot SDK, список «есть в клиенте Pilot»; у серверных модулей - ещё
    /// System.* / Microsoft.* (их ServerHost берёт у Pilot-Server и выбрасывает из архива).
    /// </summary>
    public static bool IsPlatform(AssemblySpec spec, string name, ModuleKind kind, string? publicKeyToken = null) =>
        name.StartsWith("Ascon.Pilot.", StringComparison.OrdinalIgnoreCase) ||
        spec.Find(name)?.Rule == AssemblyRule.Pilot ||
        new AssemblyRef(name, "", publicKeyToken).IsFramework ||
        (kind == ModuleKind.Server && (name.StartsWith("System.", StringComparison.OrdinalIgnoreCase) ||
                                       name.StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase) || name is "System"));

    /// <summary>
    /// Сверяет сборки zip модуля и ссылки его главной сборки с режимом библиотек. <paramref name="moduleNames"/> - имена
    /// модуля (в Pilot, сборка, репозиторий): по ним проверяется ограничение «только для модулей» и узнаётся общий модуль.
    /// <paramref name="shared"/> - общий модуль на контуре (режим 1): без него ссылки и Preload не проверяются.
    /// </summary>
    public static AssemblyCheckResult Check(AssemblySpec spec, IEnumerable<PackageAssembly> assemblies, IEnumerable<string?> moduleNames,
        SharedModuleState? shared = null, ModuleKind kind = ModuleKind.Client)
    {
        var list = assemblies.ToList();
        var keys = Keys(moduleNames);
        var isShared = IsSharedModule(spec, keys);
        var items = list.Select(a => CheckOne(spec, a, keys, isShared, kind)).ToList();
        items.AddRange(DuplicateExports(list));
        if (spec.IsShared && shared is not null)
            items.AddRange(SharedChecks(spec, list, isShared, shared, kind));
        if (spec.IsList && !isShared)
            items.AddRange(ListReferenceChecks(spec, list, keys, kind));
        return new AssemblyCheckResult { Items = items };
    }

    public static AssemblyCheckResult Check(AssemblySpec spec, ModulePackageInfo package, string? repoName = null, SharedModuleState? shared = null,
        ModuleKind kind = ModuleKind.Client) =>
        Check(spec, package.Assemblies, new[] { package.ExtensionName, package.AssemblyName, repoName }, shared, kind);

    private static AssemblyCheckItem CheckOne(AssemblySpec spec, PackageAssembly a, HashSet<string> moduleKeys, bool isShared, ModuleKind kind)
    {
        if (a.IsMain)
            return new(a, AssemblyVerdict.Main, isShared ? "главная сборка общего модуля" : "главная сборка модуля", null);
        if (a.IsOtherModule)
            return new(a, AssemblyVerdict.OtherModule, "это другой модуль - он грузится сам, класть его в zip нельзя", null);

        var entry = spec.Find(a.Name);
        if (kind == ModuleKind.Server && IsPlatform(spec, a.AssemblyName ?? a.Name, kind))
            return new(a, AssemblyVerdict.ProvidedByServer, "есть у Pilot-Server - ServerHost выбросит её при установке", entry);
        if (entry?.Rule == AssemblyRule.Pilot)
            return new(a, AssemblyVerdict.ProvidedByPilot,
                "есть в папке клиента Pilot - клиент отдаст свою копию, в модуль не кладите (ExcludeAssets=runtime)", entry);
        if (entry?.Rule == AssemblyRule.Forbidden)
            return new(a, AssemblyVerdict.Forbidden,
                "запрещена спецификацией" + (entry.Comment.Length > 0 ? $": {entry.Comment}" : "") +
                " - исключите из сборки (Private=false / ExcludeAssets=runtime)", entry);

        // Общий модуль сам определяет список библиотек (режим 1): всё, что в нём лежит, и есть разрешённое.
        if (isShared)
            return new(a, AssemblyVerdict.Ok, "библиотека общего модуля", entry);

        var exception = entry is not null && entry.Modules.Count > 0 && entry.Modules.Any(m => moduleKeys.Contains(ProjectNames.Key(m)));
        if (spec.Mode != AssemblySpecMode.WarnList && !exception)
            return new(a, AssemblyVerdict.SharedOnly, spec.IsShared
                ? $"доп. библиотеки в zip запрещены - она должна лежать в общем модуле «{spec.SharedModule}», а в проекте подключаться только для компиляции (ExcludeAssets=runtime / Private=false)"
                : "доп. библиотеки в zip запрещены - подключайте её только для компиляции (ExcludeAssets=runtime / Private=false)", entry);

        if (entry is null)
            return new(a, AssemblyVerdict.Unknown, "нет в списке разрешённых библиотек - уберите из zip или добавьте в список", null);
        if (entry.Modules.Count > 0 && !exception)
            return new(a, AssemblyVerdict.NotForModule, $"разрешена только модулям: {string.Join(", ", entry.Modules)}", entry);

        if (!a.IsManaged)
            return new(a, AssemblyVerdict.Ok, "native-сборка, версия не проверяется", entry);
        if (!VersionPattern.Matches(entry.AssemblyVersion, a.AssemblyVersion))
            return new(a, AssemblyVerdict.WrongVersion, $"AssemblyVersion {a.AssemblyVersion ?? "?"}, разрешена - {entry.AssemblyVersion}", entry);
        if (!VersionPattern.Matches(entry.FileVersion, a.FileVersion ?? a.AssemblyVersion))
            return new(a, AssemblyVerdict.WrongVersion, $"версия файла {a.FileVersion ?? "?"}, разрешена - {entry.FileVersion}", entry);
        return new(a, AssemblyVerdict.Ok, exception ? "исключение для этого модуля" : "из списка разрешённых", entry);
    }

    /// <summary>
    /// Режимы 2 и 3: на какие библиотеки ссылается главная сборка модуля. Разрешены платформа (.NET, Pilot), другие модули
    /// и строки списка «разрешена» (без «только модули» или с этим модулем) в версии из списка.
    /// </summary>
    private static IEnumerable<AssemblyCheckItem> ListReferenceChecks(AssemblySpec spec, List<PackageAssembly> module, HashSet<string> moduleKeys, ModuleKind kind)
    {
        var main = module.FirstOrDefault(a => a.IsMain);
        if (main is null)
            yield break;
        foreach (var r in main.Refs.DistinctBy(r => r.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (IsPlatform(spec, r.Name, kind, r.PublicKeyToken) || IsModuleName(r.Name))
                continue;
            var entry = spec.Find(r.Name);
            var forModule = entry is { Rule: AssemblyRule.Allowed } &&
                            (entry.Modules.Count == 0 || entry.Modules.Any(m => moduleKeys.Contains(ProjectNames.Key(m))));
            if (forModule && VersionPattern.Matches(entry!.AssemblyVersion, r.Version))
                continue;
            var why = entry switch
            {
                null => "её нет в списке разрешённых библиотек",
                { Rule: AssemblyRule.Forbidden } => "она запрещена" + (entry.Comment.Length > 0 ? $": {entry.Comment}" : ""),
                _ when !forModule => $"она разрешена только модулям: {string.Join(", ", entry.Modules)}",
                _ => $"разрешена версия {entry.AssemblyVersion}",
            };
            yield return new(main, AssemblyVerdict.NotAllowedReference,
                $"{main.Name} ссылается на {r.Name} {r.Version}, а {why} - уберите зависимость или добавьте библиотеку в список", entry)
                { Subject = $"{r.Name} {r.Version}" };
        }
    }

    /// <summary>Меню, тулбар и другие контракты с одиночным импортом: в модуле - не больше одного каждого вида.</summary>
    private static IEnumerable<AssemblyCheckItem> DuplicateExports(IEnumerable<PackageAssembly> assemblies)
    {
        foreach (var a in assemblies.Where(a => a.IsManaged))
        foreach (var g in a.PilotExports.GroupBy(e => e).Where(g => g.Count() > 1 && !ManyContracts.Contains(BaseName(g.Key))))
            yield return new(a, AssemblyVerdict.DuplicateExport,
                $"{g.Count()} класса с [Export(typeof({g.Key}))] - клиент Pilot импортирует один, при двух не подключит ни одного. Объедините в один класс.", null)
            { Subject = $"{a.Path}: {g.Key}" };
    }

    private static string BaseName(string contract)
    {
        var i = contract.IndexOf('<');
        return i < 0 ? contract : contract[..i];
    }

    private static IEnumerable<AssemblyCheckItem> SharedChecks(AssemblySpec spec, List<PackageAssembly> module, bool isShared, SharedModuleState shared,
        ModuleKind kind)
    {
        var main = module.FirstOrDefault(a => a.IsMain) ?? module.FirstOrDefault();
        if (main is null)
            yield break;

        if (isShared)
        {
            if (kind == ModuleKind.Server)
                yield break; // у серверных модулей папок расширений нет: хост ищет dll во всех модулях
            // Это и есть общий модуль: на контуре он должен лежать в Preload (объект обновляется на месте - папка не меняется).
            if (!shared.Found)
                yield return new(main, AssemblyVerdict.SharedModuleSetup,
                    $"общего модуля ещё нет на контуре - после выкладки перенесите его в папку расширений «{AssemblySpec.PreloadFolder}»", null)
                    { Subject = "папка расширений" };
            else if (!shared.InPreload)
                yield return new(main, AssemblyVerdict.SharedModuleSetup,
                    $"общий модуль лежит в папке «{(string.IsNullOrEmpty(shared.Folder) ? "корень" : shared.Folder)}», а должен - в папке расширений " +
                    $"«{AssemblySpec.PreloadFolder}»: иначе модули, загруженные раньше него, не найдут библиотеки", null)
                    { Subject = "папка расширений" };
            yield break;
        }

        if (!shared.Found)
        {
            yield return new(main, AssemblyVerdict.SharedModuleSetup, $"на контуре нет общего модуля «{spec.SharedModule}» - выложите его первым", null)
                { Subject = "общий модуль" };
            yield break;
        }
        if (kind == ModuleKind.Client && !shared.InPreload)
            yield return new(main, AssemblyVerdict.SharedModuleSetup,
                $"общий модуль «{shared.Name}» лежит не в папке «{AssemblySpec.PreloadFolder}» - порядок загрузки не гарантирован", null)
                { Subject = "общий модуль" };

        // Зависимости: всё, на что ссылается модуль, должно найтись - в нём самом, в общем модуле, в платформе (.NET, Pilot).
        var own = module.SelectMany(a => new[] { a.Name, a.AssemblyName }).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var sharedNames = shared.Assemblies.SelectMany(a => new[] { a.Name, a.AssemblyName }).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var a in module.Where(a => a.IsManaged))
        foreach (var r in a.Refs.DistinctBy(r => r.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (own.Contains(r.Name) || sharedNames.Contains(r.Name) || IsModuleName(r.Name) || IsPlatform(spec, r.Name, kind, r.PublicKeyToken))
                continue;
            yield return new(a, AssemblyVerdict.MissingReference,
                $"{a.Name} ссылается на {r.Name} {r.Version}, а её нет в общем модуле «{shared.Name}» {shared.Version} на контуре - " +
                "библиотека не разрешена: добавьте её в общий модуль и выложите его первым", null)
                { Subject = $"{r.Name} {r.Version}" };
        }
    }
}

public static partial class ModulePackage
{
    /// <summary>Все dll/exe в zip модуля с версиями, ссылками и экспортами (сборки не загружаются, читаются метаданные).</summary>
    public static IReadOnlyList<PackageAssembly> ListAssemblies(byte[] zip, string? mainAssemblyName = null)
    {
        using var archive = OpenZip(zip, "module.zip");
        var list = new List<PackageAssembly>();
        foreach (var e in archive.Entries)
        {
            if (e.Length == 0 || !(e.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || e.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)))
                continue;
            var meta = ReadMetadata(ReadEntry(e));
            var isMain = mainAssemblyName is not null && string.Equals(meta.Name, mainAssemblyName, StringComparison.OrdinalIgnoreCase) &&
                         !e.FullName.Contains('/') && !e.FullName.Contains('\\');
            list.Add(new PackageAssembly(e.FullName, e.Length, meta.Managed, meta.Name, meta.Version, meta.FileVersion, isMain, meta.References, meta.Exports));
        }
        return list.OrderByDescending(a => a.IsMain).ThenBy(a => a.Path, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private sealed record AssemblyMeta(bool Managed, string? Name, string? Version, string? FileVersion,
        IReadOnlyList<AssemblyRef>? References, IReadOnlyList<string>? Exports)
    {
        public static AssemblyMeta Native { get; } = new(false, null, null, null, null, null);
    }

    private static AssemblyMeta ReadMetadata(byte[] dll)
    {
        try
        {
            using var pe = new PEReader(new MemoryStream(dll, writable: false));
            if (!pe.HasMetadata)
                return AssemblyMeta.Native;
            var md = pe.GetMetadataReader();
            if (!md.IsAssembly)
                return AssemblyMeta.Native;
            var def = md.GetAssemblyDefinition();
            string? fileVersion = null;
            foreach (var handle in def.GetCustomAttributes())
            {
                var attr = md.GetCustomAttribute(handle);
                if (AttributeTypeName(md, attr) != "System.Reflection.AssemblyFileVersionAttribute")
                    continue;
                var blob = md.GetBlobReader(attr.Value);
                if (blob.ReadUInt16() == 1)
                    fileVersion = blob.ReadSerializedString();
            }

            var refs = md.AssemblyReferences.Select(h => md.GetAssemblyReference(h))
                .Select(r => new AssemblyRef(md.GetString(r.Name), r.Version.ToString(), Token(md.GetBlobBytes(r.PublicKeyOrToken))))
                .ToList();
            return new AssemblyMeta(true, md.GetString(def.Name), def.Version.ToString(), fileVersion, refs, ReadExports(md));
        }
        catch (Exception ex) when (ex is BadImageFormatException or InvalidOperationException)
        {
            return AssemblyMeta.Native;
        }
    }

    /// <summary>[Export(typeof(X))] на классах сборки: короткое имя контракта Pilot SDK («IMenu&lt;ObjectsViewContext&gt;»).</summary>
    private static List<string> ReadExports(MetadataReader md)
    {
        var result = new List<string>();
        foreach (var typeHandle in md.TypeDefinitions)
        foreach (var handle in md.GetTypeDefinition(typeHandle).GetCustomAttributes())
        {
            var attr = md.GetCustomAttribute(handle);
            if (AttributeTypeName(md, attr) != "System.ComponentModel.Composition.ExportAttribute")
                continue;
            try
            {
                var blob = md.GetBlobReader(attr.Value);
                if (blob.ReadUInt16() != 1 || blob.RemainingBytes <= 2)
                    continue; // [Export] без аргументов - контракт = сам класс
                var type = blob.ReadSerializedString();
                if (type is not null && type.StartsWith("Ascon.Pilot.SDK", StringComparison.Ordinal))
                    result.Add(ShortTypeName(type));
            }
            catch (BadImageFormatException)
            {
            }
        }
        return result;
    }

    /// <summary>«Ascon.Pilot.SDK.Menu.IMenu`1[[Ascon.Pilot.SDK.ObjectsViewContext, Ascon.Pilot.SDK, Version=…]], Ascon.Pilot.SDK» / «IMenu&lt;ObjectsViewContext&gt;».</summary>
    internal static string ShortTypeName(string serialized)
    {
        var pos = 0;
        return Parse(serialized, ref pos);

        static string Parse(string s, ref int pos)
        {
            var start = pos;
            while (pos < s.Length && s[pos] is not ('[' or ']' or ','))
                pos++;
            var name = s[start..pos].Trim();
            var dot = name.LastIndexOf('.');
            if (dot >= 0) name = name[(dot + 1)..];
            var tick = name.IndexOf('`');
            if (tick >= 0) name = name[..tick];

            var args = new List<string>();
            if (pos + 1 < s.Length && s[pos] == '[' && s[pos + 1] == '[')
            {
                pos++; // внешняя «[»
                while (pos < s.Length && s[pos] == '[')
                {
                    pos++;
                    args.Add(Parse(s, ref pos));
                    SkipToClose(s, ref pos); // остаток «, Сборка, Version=…]»
                    if (pos < s.Length && s[pos] == ',') pos++;
                }
                if (pos < s.Length && s[pos] == ']') pos++;
            }
            return args.Count == 0 ? name : $"{name}<{string.Join(", ", args)}>";
        }

        static void SkipToClose(string s, ref int pos)
        {
            var depth = 0;
            while (pos < s.Length)
            {
                var c = s[pos++];
                if (c == '[') depth++;
                else if (c == ']')
                {
                    if (depth == 0) return;
                    depth--;
                }
            }
        }
    }

    private static string? Token(byte[] keyOrToken)
    {
        if (keyOrToken.Length == 0)
            return null;
        var token = keyOrToken.Length == 8
            ? keyOrToken
            : System.Security.Cryptography.SHA1.HashData(keyOrToken).TakeLast(8).Reverse().ToArray();
        var sb = new StringBuilder();
        foreach (var b in token)
            sb.Append(b.ToString("x2"));
        return sb.ToString();
    }
}

/// <summary>
/// Хранилище спецификаций библиотек: data/assembly-spec.json (клиентские модули) и data/assembly-spec-server.json (серверные).
/// Первый запуск - заготовка (у клиентских - с библиотеками клиента Pilot).
/// </summary>
public sealed class AssemblySpecStore
{
    public const int HistoryLimit = 50;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _dataDir;
    private readonly ILogger<AssemblySpecStore> _logger;
    private readonly object _lock = new();
    private readonly Dictionary<ModuleKind, AssemblySpec> _current = new();

    public AssemblySpecStore(IOptions<AppOptions> options, ILogger<AssemblySpecStore> logger)
    {
        _logger = logger;
        _dataDir = Path.GetFullPath(options.Value.DataDirectory);
        foreach (var kind in new[] { ModuleKind.Client, ModuleKind.Server })
            _current[kind] = Load(kind);
    }

    /// <summary>Клиентские модули.</summary>
    public AssemblySpec Current => For(ModuleKind.Client);

    public AssemblySpec For(ModuleKind kind)
    {
        lock (_lock) return _current[kind].Clone();
    }

    private string PathFor(ModuleKind kind) =>
        Path.Combine(_dataDir, kind == ModuleKind.Server ? "assembly-spec-server.json" : "assembly-spec.json");

    /// <summary>Сохраняет спецификацию клиентских модулей.</summary>
    public void Save(AssemblySpec spec, string by) => Save(ModuleKind.Client, spec, by);

    /// <summary>Сохраняет спецификацию и пишет в историю, что изменилось.</summary>
    public void Save(ModuleKind kind, AssemblySpec spec, string by)
    {
        spec.Normalize();
        lock (_lock)
        {
            var current = _current[kind];
            var changes = Describe(current, spec);
            if (changes.Count == 0)
                return;
            spec.UpdatedAt = DateTimeOffset.UtcNow;
            spec.UpdatedBy = by;
            spec.History = changes.Select(c => new AssemblySpecChange { At = spec.UpdatedAt.Value, By = by, Text = c })
                .Concat(current.History).Take(HistoryLimit).ToList();
            Write(kind, spec);
            _current[kind] = spec.Clone();
        }
    }

    private void Write(ModuleKind kind, AssemblySpec spec)
    {
        var path = PathFor(kind);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(spec, Json));
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>Отличия старой спецификации от новой - строки истории.</summary>
    internal static List<string> Describe(AssemblySpec before, AssemblySpec after)
    {
        var list = new List<string>();
        if (before.Mode != after.Mode)
            list.Add($"режим: {AssemblySpec.ModeTitle(after.Mode)}");
        if (before.SharedModule != after.SharedModule && after.Mode == AssemblySpecMode.SharedModule)
            list.Add($"общий модуль: {after.SharedModule}");
        foreach (var e in after.Entries)
        {
            var old = before.Find(e.Name);
            if (old is null)
                list.Add($"+ {Line(e)}");
            else if (Line(old) != Line(e) || old.Comment != e.Comment || old.Package != e.Package)
                list.Add($"~ {Line(old)} / {Line(e)}");
        }
        foreach (var e in before.Entries.Where(e => after.Find(e.Name) is null))
            list.Add($"− {e.Name}");
        return list;

        static string Line(AssemblySpecEntry e) =>
            $"{e.Name} [{AssemblySpec.RuleTitle(e.Rule)}]" +
            (e.AssemblyVersion.Length > 0 ? $" {e.AssemblyVersion}" : "") +
            (e.FileVersion.Length > 0 ? $" файл {e.FileVersion}" : "") +
            (e.Modules.Count > 0 ? $" только {string.Join(", ", e.Modules)}" : "") +
            (e.Package.Length > 0 ? $" (NuGet {e.Package})" : "");
    }

    private AssemblySpec Load(ModuleKind kind)
    {
        var path = PathFor(kind);
        if (!File.Exists(path))
        {
            var seed = Seed(kind);
            try { Write(kind, seed); }
            catch (Exception ex) { _logger.LogError(ex, "Не удалось записать {Path}", path); }
            return seed;
        }
        try
        {
            var spec = JsonSerializer.Deserialize<AssemblySpec>(File.ReadAllText(path), Json) ?? Seed(kind);
            spec.Normalize();
            return spec;
        }
        catch (Exception ex)
        {
            // Без файла - только предупреждения: сломанный файл не должен запрещать выкладку.
            _logger.LogError(ex, "Не удалось прочитать {Path} - до исправления файла библиотеки только предупреждают", path);
            return new AssemblySpec { Mode = AssemblySpecMode.WarnList };
        }
    }

    internal static AssemblySpec Seed(ModuleKind kind) => kind == ModuleKind.Server
        ? new AssemblySpec { Mode = AssemblySpecMode.WarnList, SharedModule = "", UpdatedAt = DateTimeOffset.UtcNow, UpdatedBy = "начальная спецификация" }
        : Seed();

    /// <summary>Заготовка: сборки клиента Pilot (их копии в модулях только мешают) и общий модуль Pilot.ClientSdk.</summary>
    internal static AssemblySpec Seed()
    {
        var spec = new AssemblySpec
        {
            Mode = AssemblySpecMode.WarnList,
            UpdatedAt = DateTimeOffset.UtcNow,
            UpdatedBy = "начальная спецификация",
            Entries =
            {
                new() { Name = "Humanizer", AssemblyVersion = "2.14.*", Package = "Humanizer.Core 2.14.1", Comment = "общая библиотека, лежит в Pilot.ClientSdk (с 2.1.0)" },
                new() { Name = "Pilot.ClientSdk", Rule = AssemblyRule.Forbidden, Comment = "общий модуль 113Pilot.ClientSdk.ext2 - подключать NuGet с ExcludeAssets=runtime" },
            },
        };
        PilotClientAssemblies.AddTo(spec);
        spec.Normalize();
        return spec;
    }
}
