using ProAdmin.Modules;
using ProAdmin.Pilot;
using Microsoft.AspNetCore.Mvc;

namespace ProAdmin.Pages.Modules;

/// <summary>
/// Спецификация сборок клиентских модулей: какие сторонние dll и каких версий можно класть в zip модуля.
/// Здесь же - проверка zip и состав сборок всех модулей сервера контура (из него удобно заполнять спецификацию).
/// </summary>
public class AssembliesModel : AppPageModel
{
    private readonly AssemblySpecStore _store;
    private readonly AssemblyScanService _scan;

    public AssembliesModel(AssemblySpecStore store, AssemblyScanService scan)
    {
        _store = store;
        _scan = scan;
    }

    public sealed class EntryForm
    {
        public string? Name { get; set; }
        public AssemblyRule Rule { get; set; }
        public string? AssemblyVersion { get; set; }
        public string? FileVersion { get; set; }
        public string? Modules { get; set; }
        public string? Comment { get; set; }
        public string? Package { get; set; }
        public bool Delete { get; set; }
    }

    public AssemblySpec Spec { get; private set; } = new();
    public List<ModuleEnv> Envs { get; private set; } = new();
    public List<string> Errors { get; } = new();

    [BindProperty(SupportsGet = true)] public string? Env { get; set; }
    [BindProperty(SupportsGet = true)] public bool Fresh { get; set; }
    [BindProperty(SupportsGet = true)] public bool OnlyProblems { get; set; }

    [BindProperty] public List<EntryForm> Entries { get; set; } = new();
    [BindProperty] public IFormFile? Archive { get; set; }

    [BindProperty] public string? AddName { get; set; }
    [BindProperty] public AssemblyRule AddRule { get; set; }
    [BindProperty] public string? AddVersion { get; set; }

    public AssemblyScanResult? Scan { get; private set; }

    /// <summary>Режим общего модуля: общий модуль на каждом контуре (или ошибка чтения) - для таблицы «Библиотеки общего модуля».</summary>
    public Dictionary<string, (SharedModuleState? State, string? Error)> SharedByEnv { get; } = new();

    /// <summary>Общий модуль на просканированном сервере (режим общего модуля).</summary>
    public SharedModuleState? ScanShared => Scan is null ? null : AssemblyScanService.Shared(Spec, Scan);
    public string? ScanError { get; private set; }

    public ModulePackageInfo? Checked { get; private set; }
    public AssemblyCheckResult? CheckResult { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        Prepare();
        await LoadModulesAccessAsync(ct);
        await LoadScanAsync(ct);
        await LoadSharedAsync(ct);
        return Page();
    }

    private async Task LoadSharedAsync(CancellationToken ct)
    {
        if (!Spec.IsShared)
            return;
        foreach (var env in Envs)
        {
            try
            {
                SharedByEnv[env.Key] = (await _scan.SharedOnEnvAsync(Spec, env, ModuleLogin(env.Key), AppSettings.ReportsLicenseType, ct), null);
            }
            catch (PilotGatewayException ex)
            {
                SharedByEnv[env.Key] = (null, ex.Kind == PilotErrorKind.Authentication ? "нет входа в базу (вкладка «Модули»)" : ex.Message);
            }
        }
    }

    /// <summary>Строка таблицы библиотек общего модуля: строка спецификации и/или dll, найденная в общем модуле на контурах.</summary>
    public sealed record SharedLibraryRow(string Name, AssemblySpecEntry? Entry, Dictionary<string, PackageAssembly?> ByEnv);

    public List<SharedLibraryRow> SharedLibraries()
    {
        var found = SharedByEnv.Values.Where(v => v.State is not null)
            .SelectMany(v => v.State!.Assemblies.Where(a => !a.IsMain).Select(a => a.Name));
        return Spec.Entries.Where(e => e.IsSharedLibrary).Select(e => e.Name)
            .Concat(found)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .Select(n => new SharedLibraryRow(n, Spec.Find(n), Envs.ToDictionary(e => e.Key, e => OnEnv(e.Key, n))))
            .ToList();

        PackageAssembly? OnEnv(string env, string name) =>
            SharedByEnv.TryGetValue(env, out var v)
                ? v.State?.Assemblies.FirstOrDefault(a => !a.IsMain && string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase))
                : null;
    }

    public async Task<IActionResult> OnPostSaveAsync(CancellationToken ct)
    {
        Prepare();
        if (await DenyModulesChangeAsync(ct) is { } deny)
            return deny;

        var spec = _store.Current;
        spec.Entries = new();
        foreach (var (e, i) in Entries.Select((e, i) => (e, i)))
        {
            if (e.Delete || string.IsNullOrWhiteSpace(e.Name))
                continue;
            var name = AssemblySpec.NormalizeName(e.Name);
            foreach (var (pattern, title) in new[] { (e.AssemblyVersion, "AssemblyVersion"), (e.FileVersion, "версия файла") })
                if (VersionPattern.Validate(pattern) is { } error)
                    Errors.Add($"{name}, {title}: {error}");
            spec.Entries.Add(new AssemblySpecEntry
            {
                Name = name,
                Rule = e.Rule,
                AssemblyVersion = e.AssemblyVersion ?? "",
                FileVersion = e.FileVersion ?? "",
                Modules = SplitList(e.Modules),
                Comment = e.Comment ?? "",
                Package = e.Package ?? "",
            });
        }
        var duplicates = spec.Entries.GroupBy(e => e.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        if (duplicates.Count > 0)
            Errors.Add($"Сборки указаны дважды: {string.Join(", ", duplicates)}.");
        if (Errors.Count > 0)
        {
            Spec = spec;
            await LoadModulesAccessAsync(ct);
            await LoadScanAsync(ct);
            return Page();
        }

        _store.Save(spec, Who);
        TempData["Flash"] = "Список библиотек сохранён - проверяется в CI и при каждой следующей выкладке модуля.";
        return RedirectToPage(new { env = Env, onlyProblems = OnlyProblems });
    }

    /// <summary>Добавить сборку из состава модулей сервера (кнопки «разрешить» / «запретить»).</summary>
    public async Task<IActionResult> OnPostAddAsync(CancellationToken ct)
    {
        Prepare();
        if (await DenyModulesChangeAsync(ct) is { } deny)
            return deny;
        var name = AssemblySpec.NormalizeName(AddName);
        if (name.Length > 0)
        {
            var spec = _store.Current;
            var entry = spec.Find(name);
            if (entry is null)
                spec.Entries.Add(entry = new AssemblySpecEntry { Name = name });
            entry.Rule = AddRule;
            if (AddRule == AssemblyRule.Allowed)
                entry.AssemblyVersion = AddVersion?.Trim() ?? "";
            _store.Save(spec, Who);
            TempData["Flash"] = $"{name}: {AssemblySpec.RuleTitle(AddRule)}" + (AddRule == AssemblyRule.Allowed && entry.AssemblyVersion.Length > 0 ? $" ({entry.AssemblyVersion})" : "") + ".";
        }
        return RedirectToPage(null, null, new { env = Env, onlyProblems = OnlyProblems }, "scan");
    }

    /// <summary>Добавить сборки из папки клиента Pilot с правилом «есть в клиенте Pilot».</summary>
    public async Task<IActionResult> OnPostAddPilotAsync(CancellationToken ct)
    {
        Prepare();
        if (await DenyModulesChangeAsync(ct) is { } deny)
            return deny;
        var spec = _store.Current;
        var added = PilotClientAssemblies.AddTo(spec);
        _store.Save(spec, Who);
        TempData["Flash"] = added > 0
            ? $"Добавлено библиотек клиента Pilot: {added}."
            : "Все библиотеки клиента Pilot уже есть в спецификации.";
        return RedirectToPage(new { env = Env, onlyProblems = OnlyProblems });
    }

    /// <summary>Проверка zip модуля без выкладки.</summary>
    public async Task<IActionResult> OnPostCheckAsync(CancellationToken ct)
    {
        Prepare();
        await LoadModulesAccessAsync(ct);
        if (Archive is null || Archive.Length == 0)
            Errors.Add("Выберите zip модуля.");
        else
        {
            using var ms = new MemoryStream();
            await Archive.CopyToAsync(ms, ct);
            try
            {
                Checked = ModulePackage.Analyze(ms.ToArray(), Path.GetFileName(Archive.FileName));
                // Если выбран сервер - зависимости и Preload проверяются против общего модуля на нём.
                await LoadScanAsync(ct);
                CheckResult = AssemblyChecker.Check(Spec, Checked, null, ScanShared);
            }
            catch (InvalidDataException ex)
            {
                Errors.Add(ex.Message);
            }
        }
        if (Scan is null)
            await LoadScanAsync(ct);
        return Page();
    }

    private string Who => CurrentSession.Actor;

    private void Prepare()
    {
        ViewData["Section"] = "modules";
        Spec = _store.Current;
        Envs = ModuleEnv.All(AppSettings);
    }

    private async Task LoadScanAsync(CancellationToken ct)
    {
        var env = Env is null ? null : Envs.FirstOrDefault(e => e.Key == Env);
        if (env is null)
            return;
        try
        {
            Scan = await _scan.ScanAsync(env, ModuleLogin(env.Key), AppSettings.ReportsLicenseType, Fresh, ct);
        }
        catch (PilotGatewayException ex)
        {
            ScanError = ex.Kind == PilotErrorKind.Authentication
                ? $"{env.Title}: нет доступа к папке расширений - войдите в базу на вкладке «Модули» ({ex.Message})."
                : $"{env.Title}: {ex.Message}";
        }
    }

    private static List<string> SplitList(string? text) =>
        (text ?? "").Split(new[] { ',', ';', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    public static string VerdictCss(AssemblyVerdict v) => v switch
    {
        AssemblyVerdict.Main => "badge-onlypilot",
        AssemblyVerdict.Ok => "badge-synced",
        AssemblyVerdict.ProvidedByServer => "",
        AssemblyVerdict.Unknown or AssemblyVerdict.WrongVersion or AssemblyVerdict.NotForModule or AssemblyVerdict.ProvidedByPilot
            or AssemblyVerdict.SharedModuleSetup => "badge-differs",
        _ => "badge-failed",
    };

    public static string VerdictLabel(AssemblyVerdict v) => v switch
    {
        AssemblyVerdict.Main => "модуль",
        AssemblyVerdict.Ok => "ок",
        AssemblyVerdict.Unknown => "лишняя",
        AssemblyVerdict.Forbidden => "запрещена",
        AssemblyVerdict.WrongVersion => "не та версия",
        AssemblyVerdict.NotForModule => "не для модуля",
        AssemblyVerdict.OtherModule => "чужой модуль",
        AssemblyVerdict.ProvidedByPilot => "есть в Pilot",
        AssemblyVerdict.SharedOnly => "лишняя",
        AssemblyVerdict.MissingReference => "не разрешена",
        AssemblyVerdict.NotAllowedReference => "не разрешена",
        AssemblyVerdict.ProvidedByServer => "есть у сервера",
        AssemblyVerdict.DuplicateExport => "двойной экспорт",
        AssemblyVerdict.SharedModuleSetup => "общий модуль",
        _ => v.ToString(),
    };
}
