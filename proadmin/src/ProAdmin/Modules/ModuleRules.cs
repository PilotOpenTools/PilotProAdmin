using System.Text.RegularExpressions;
using ProAdmin.GitLab;

namespace ProAdmin.Modules;

/// <summary>
/// Схема веток модулей (Pilot-BIM, схема ветвления CI/CD):
/// <list type="bullet">
/// <item><c>feature/KTN-1234</c>, <c>bugfix/KTN-1234</c> - задача, от <c>dev</c>, в <c>dev</c> через MR со squash;</item>
/// <item><c>dev</c> - тестовый сервер;</item>
/// <item><c>release/X.Y.Z</c> - стейдж, от <c>main</c>, в него - MR из <c>dev</c> (или выборочно) и мелкие багфиксы;</item>
/// <item><c>main</c> - прод, тег <c>vX.Y.Z</c> на каждый релиз;</item>
/// <item><c>hotfix/KTN-1234</c> - от <c>main</c>, в <c>main</c>, затем синхронизация в <c>dev</c> и активный release.</item>
/// </list>
/// </summary>
public static partial class ModuleBranches
{
    public const string Dev = "dev";
    public const string Main = "main";
    public const string ReleasePrefix = "release/";
    public const string ReleasePrepPrefix = "release-prep/";
    public const string FeaturePrefix = "feature/";
    public const string BugfixPrefix = "bugfix/";
    public const string HotfixPrefix = "hotfix/";

    public static string Release(ModuleVersion version) => ReleasePrefix + version;
    public static string ReleasePrep(ModuleVersion version) => ReleasePrepPrefix + version;

    public static bool IsRelease(string? branch) => branch?.StartsWith(ReleasePrefix, StringComparison.Ordinal) == true;
    public static bool IsHotfix(string? branch) => branch?.StartsWith(HotfixPrefix, StringComparison.Ordinal) == true;
    /// <summary>Ветка по схеме: main, dev, release/X.Y.Z, release-prep/*, hotfix/*, feature/*, bugfix/*, ci/*.</summary>
    public static bool InScheme(string branch) =>
        branch is Dev or Main or "master" || ReleaseVersion(branch) is not null || IsHotfix(branch) || IsTaskBranch(branch) ||
        branch.StartsWith(ReleasePrepPrefix, StringComparison.Ordinal) || branch.StartsWith("ci/", StringComparison.Ordinal);

    public static bool IsTaskBranch(string? branch) =>
        branch is not null && (branch.StartsWith(FeaturePrefix, StringComparison.Ordinal) || branch.StartsWith(BugfixPrefix, StringComparison.Ordinal));

    /// <summary>release/1.4.0 / 1.4.0.</summary>
    public static ModuleVersion? ReleaseVersion(string? branch) =>
        IsRelease(branch) && ModuleVersion.TryParse(branch![ReleasePrefix.Length..], out var v) ? v : null;

    /// <summary>Имя ветки задачи: prefix + номер задачи и/или короткое описание латиницей (хотя бы одно из двух).</summary>
    public static string TaskBranch(string prefix, string? task, string? slug = null)
    {
        var parts = new[]
        {
            string.IsNullOrWhiteSpace(task) ? "" : task.Trim().ToUpperInvariant(),
            string.IsNullOrWhiteSpace(slug) ? "" : ProjectNames.Slug(slug, "").Trim('-'),
        };
        return prefix + string.Join("-", parts.Where(p => p.Length > 0));
    }

    private const string RuKeys = "йцукенгшщзхъфывапролджэячсмитьбю";
    private const string EnKeys = "qwertyuiop[]asdfghjkl;'zxcvbnm,.";

    /// <summary>
    /// Номер задачи из ввода: «KTN-1234», «ktn 1234» / KTN-1234; набранное в русской раскладке «ЛЕТ-1234» / KTN-1234;
    /// одни цифры «1234» / префикс шаблона (KTN-\d+) + цифры. null - номер не распознан.
    /// </summary>
    public static string? ParseTask(string? input, string pattern)
    {
        if (string.IsNullOrWhiteSpace(input))
            return null;
        var text = new string(input.Trim().Select(c =>
        {
            var i = RuKeys.IndexOf(char.ToLowerInvariant(c));
            return i < 0 ? c : EnKeys[i];
        }).ToArray());
        if (Task(text, pattern) is { } task)
            return task;
        if (Task(Regex.Replace(text, @"\s+", "-"), pattern) is { } spaced)
            return spaced;
        var prefix = Regex.Match(pattern, @"^([A-Za-z]+-?)\\d[+*]$");
        return prefix.Success && Regex.IsMatch(text, @"^\d+$") ? Task(prefix.Groups[1].Value + text, pattern) : null;
    }

    /// <summary>Номер задачи в строке (имя ветки, заголовок коммита/MR); null - нет.</summary>
    public static string? Task(string? text, string pattern)
    {
        if (string.IsNullOrEmpty(text))
            return null;
        try
        {
            var m = Regex.Match(text, pattern, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
            return m.Success ? m.Value.ToUpperInvariant() : null;
        }
        catch (Exception ex) when (ex is ArgumentException or RegexMatchTimeoutException)
        {
            return null;
        }
    }

    /// <summary>Ссылка на карточку задачи по шаблону настроек; null - шаблон не задан или даёт не http/https-адрес (javascript: и т. п.).</summary>
    public static string? TaskUrl(string? task, string template)
    {
        if (string.IsNullOrEmpty(task) || string.IsNullOrWhiteSpace(template))
            return null;
        var number = new string(task.Where(char.IsDigit).ToArray());
        return ProAdmin.Infrastructure.SafeUrl.Http(template.Replace("{task}", Uri.EscapeDataString(task)).Replace("{number}", number));
    }

    /// <summary>Тег прода: v1.4.0.</summary>
    public static string ProdTag(ModuleVersion v) => "v" + v;

    /// <summary>Тег выкладки на стейдж: v1.4.0-rc.2.</summary>
    public static string StageTag(ModuleVersion v, int n) => $"v{v}-rc.{n}";

    /// <summary>Номер следующего rc для версии по уже существующим тегам.</summary>
    public static int NextRc(ModuleVersion v, IEnumerable<string> tags)
    {
        var prefix = $"v{v}-rc.";
        var max = tags.Where(t => t.StartsWith(prefix, StringComparison.Ordinal))
            .Select(t => int.TryParse(t[prefix.Length..], out var n) ? n : 0)
            .DefaultIfEmpty(0).Max();
        return max + 1;
    }

    /// <summary>Версии релизов на проде по тегам vX.Y.Z (без rc), новые первыми.</summary>
    public static List<(ModuleVersion Version, GitLabTag Tag)> ProdReleases(IEnumerable<GitLabTag> tags) =>
        tags.Select(t => (ok: ModuleVersion.TryParse(t.Name.TrimStart('v'), out var v) && !t.Name.Contains('-'), v, t))
            .Where(x => x.ok)
            .Select(x => (x.v!, x.t))
            .OrderByDescending(x => x.Item1)
            .ToList();
}

/// <summary>Версия модуля X.Y.Z (четвёртая часть сборки - всегда 0).</summary>
public sealed partial record ModuleVersion(int Major, int Minor, int Patch) : IComparable<ModuleVersion>
{
    public static bool TryParse(string? text, out ModuleVersion? version)
    {
        version = null;
        var m = text is null ? null : VersionRegex().Match(text.Trim());
        if (m is not { Success: true })
            return false;
        version = new ModuleVersion(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value),
            m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : 0);
        return true;
    }

    public ModuleVersion NextMinor() => new(Major, Minor + 1, 0);
    public ModuleVersion NextPatch() => new(Major, Minor, Patch + 1);

    /// <summary>Версия для AssemblyVersion/FileVersion - те же три числа (компилятор сам допишет «.0»).</summary>
    public string Assembly => ToString();

    /// <summary>
    /// Версия модуля - три числа X.Y.Z: «2.1.0.0» (AssemblyVersion .NET) / «2.1.0».
    /// Ненулевая четвёртая часть остаётся видна - это другая сборка, а не та же версия.
    /// </summary>
    public static string? Display(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
            return version;
        var v = version.Trim();
        return v.Count(c => c == '.') == 3 && v.EndsWith(".0", StringComparison.Ordinal) ? v[..^2] : v;
    }

    public override string ToString() => $"{Major}.{Minor}.{Patch}";

    public int CompareTo(ModuleVersion? other) =>
        other is null ? 1 : (Major, Minor, Patch).CompareTo((other.Major, other.Minor, other.Patch));

    public static bool operator <(ModuleVersion a, ModuleVersion b) => a.CompareTo(b) < 0;
    public static bool operator >(ModuleVersion a, ModuleVersion b) => a.CompareTo(b) > 0;
    public static bool operator <=(ModuleVersion a, ModuleVersion b) => a.CompareTo(b) <= 0;
    public static bool operator >=(ModuleVersion a, ModuleVersion b) => a.CompareTo(b) >= 0;

    [GeneratedRegex(@"^v?(\d+)\.(\d+)(?:\.(\d+))?(?:\.\d+)?$")]
    private static partial Regex VersionRegex();
}

/// <summary>Исходники модуля в репозитории: где проект, какая версия, как её поднять.</summary>
public static partial class ModuleSources
{
    /// <summary>Главный .csproj: в корне, иначе самый короткий путь не из тестов.</summary>
    public static string? FindProject(IEnumerable<GitLabTreeItem> tree) =>
        tree.Where(i => i.Type == "blob" && i.Path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) && !IsTestPath(i.Path))
            .OrderBy(i => i.Path.Count(c => c == '/'))
            .ThenBy(i => i.Path.Length)
            .Select(i => i.Path)
            .FirstOrDefault();

    /// <summary>AssemblyInfo.cs рядом с проектом (корень или Properties/).</summary>
    public static string? FindAssemblyInfo(IEnumerable<GitLabTreeItem> tree, string? projectPath)
    {
        var dir = projectPath is null || !projectPath.Contains('/') ? "" : projectPath[..(projectPath.LastIndexOf('/') + 1)];
        return tree.Where(i => i.Type == "blob" && i.Path.EndsWith("AssemblyInfo.cs", StringComparison.OrdinalIgnoreCase) && !IsTestPath(i.Path))
            .OrderByDescending(i => i.Path.StartsWith(dir, StringComparison.Ordinal))
            .ThenBy(i => i.Path.Length)
            .Select(i => i.Path)
            .FirstOrDefault();
    }

    public static bool HasCi(IEnumerable<GitLabTreeItem> tree, string? ciConfigPath)
    {
        var path = string.IsNullOrWhiteSpace(ciConfigPath) ? ".gitlab-ci.yml" : ciConfigPath!;
        return path.Contains('@') || path.Contains(':') || tree.Any(i => i.Type == "blob" && i.Path == path);
    }

    // Тестовые - папки tests/, X.Tests/, samples/ и проекты X.Tests.csproj; «TestModule.csproj» в корне - не тест.
    private static bool IsTestPath(string path)
    {
        var parts = path.Split('/');
        var file = Path.GetFileNameWithoutExtension(parts[^1]);
        return parts[..^1].Any(p => p.Contains("test", StringComparison.OrdinalIgnoreCase) || p.Equals("samples", StringComparison.OrdinalIgnoreCase)) ||
               file.EndsWith(".Tests", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".Test", StringComparison.OrdinalIgnoreCase) ||
               file.EndsWith("Tests", StringComparison.Ordinal);
    }

    /// <summary>AssemblyName из csproj (или имя файла проекта).</summary>
    public static string AssemblyName(string projectPath, string? csproj)
    {
        var m = csproj is null ? null : AssemblyNameTag().Match(csproj);
        return m is { Success: true } ? m.Groups[1].Value.Trim() : Path.GetFileNameWithoutExtension(projectPath);
    }

    /// <summary>Версия из csproj (&lt;Version&gt;) или AssemblyInfo (AssemblyVersion).</summary>
    public static ModuleVersion? Version(string? csproj, string? assemblyInfo)
    {
        if (csproj is not null && VersionTag().Match(csproj) is { Success: true } m && ModuleVersion.TryParse(m.Groups[1].Value, out var v))
            return v;
        if (assemblyInfo is not null && AssemblyVersionAttr().Match(assemblyInfo) is { Success: true } a && ModuleVersion.TryParse(a.Groups[2].Value, out var av))
            return av;
        return null;
    }

    /// <summary>AssemblyProduct - так модуль называется в Pilot (атрибут «Наименование» объекта Extension).</summary>
    public static string? Product(string? csproj, string? assemblyInfo)
    {
        if (assemblyInfo is not null && ProductAttr().Match(assemblyInfo) is { Success: true } a)
            return System.Text.RegularExpressions.Regex.Unescape(a.Groups[1].Value).Trim();
        if (csproj is not null && ProductTag().Match(csproj) is { Success: true } m)
            return System.Net.WebUtility.HtmlDecode(m.Groups[1].Value).Trim();
        return null;
    }

    /// <summary>Поднимает версию в csproj: Version, FileVersion/AssemblyVersion (если теги есть) = X.Y.Z.</summary>
    public static string BumpProject(string csproj, ModuleVersion v)
    {
        var result = VersionTag().Replace(csproj, m => m.Value.Replace(m.Groups[1].Value, v.ToString()), 1);
        result = FileVersionTag().Replace(result, m => m.Value.Replace(m.Groups[2].Value, v.Assembly));
        return result;
    }

    /// <summary>Поднимает AssemblyVersion/AssemblyFileVersion в AssemblyInfo.cs до X.Y.Z.</summary>
    public static string BumpAssemblyInfo(string assemblyInfo, ModuleVersion v) =>
        AssemblyVersionAttr().Replace(assemblyInfo, m => m.Value.Replace("\"" + m.Groups[2].Value + "\"", "\"" + v.Assembly + "\""));

    /// <summary>
    /// Версия только в &lt;Version&gt; csproj: убрать &lt;FileVersion&gt;/&lt;AssemblyVersion&gt;; при GenerateAssemblyInfo=false
    /// включить генерацию одних атрибутов версии (остальные - Product, Title… - остаются в AssemblyInfo.cs).
    /// Нет &lt;Version&gt; - берётся из AssemblyInfo. Уже по схеме - текст не меняется.
    /// </summary>
    public static string SingleVersionProject(string csproj, string? assemblyInfo)
    {
        var result = Regex.Replace(csproj, @"[ \t]*<(FileVersion|AssemblyVersion)>[^<]*</\1>[ \t]*\r?\n", "");
        if (!VersionTag().IsMatch(result) && Version(null, assemblyInfo) is { } v)
            result = Regex.Replace(result, @"([ \t]*)(<TargetFrameworks?>[^<]*</TargetFrameworks?>)(\r?\n)", m =>
                $"{m.Groups[1].Value}{m.Groups[2].Value}{m.Groups[3].Value}{m.Groups[1].Value}<Version>{v}</Version>{m.Groups[3].Value}", RegexOptions.None, TimeSpan.FromSeconds(1));
        var off = GenerateOffTag().Match(result);
        if (off.Success)
        {
            var indent = off.Groups[1].Value;
            var nl = result.Contains("\r\n") ? "\r\n" : "\n";
            string Line(string text) => indent + text + nl;
            var block =
                Line("<!-- Версия модуля - только <Version>: AssemblyVersion/FileVersion генерирует SDK. Остальные атрибуты - в AssemblyInfo.cs. -->") +
                Line("<GenerateAssemblyInfo>true</GenerateAssemblyInfo>") +
                string.Concat(new[] { "Title", "Description", "Company", "Copyright", "Product", "Configuration", "Trademark" }
                    .Select(a => Line($"<GenerateAssembly{a}Attribute>false</GenerateAssembly{a}Attribute>"))) +
                Line("<GenerateNeutralResourcesLanguageAttribute>false</GenerateNeutralResourcesLanguageAttribute>") +
                Line("<IncludeSourceRevisionInInformationalVersion>false</IncludeSourceRevisionInInformationalVersion>");
            result = result.Remove(off.Index, off.Length).Insert(off.Index, block);
        }
        return result;
    }

    /// <summary>Убрать из AssemblyInfo.cs атрибуты версии - она берётся из &lt;Version&gt; csproj.</summary>
    public static string SingleVersionAssemblyInfo(string assemblyInfo) =>
        Regex.Replace(assemblyInfo, @"[ \t]*\[\s*assembly\s*:\s*(AssemblyVersion|AssemblyFileVersion|AssemblyInformationalVersion)\s*\([^\]]*\]\s*\r?\n?", "");

    [GeneratedRegex(@"([ \t]*)<GenerateAssemblyInfo>\s*false\s*</GenerateAssemblyInfo>[ \t]*\r?\n")]
    private static partial Regex GenerateOffTag();

    [GeneratedRegex(@"<AssemblyName>\s*([^<]+?)\s*</AssemblyName>")]
    private static partial Regex AssemblyNameTag();

    [GeneratedRegex(@"<Version>\s*([^<]+?)\s*</Version>")]
    private static partial Regex VersionTag();

    [GeneratedRegex(@"<(FileVersion|AssemblyVersion)>\s*([^<]+?)\s*</\1>")]
    private static partial Regex FileVersionTag();

    [GeneratedRegex(@"\[\s*assembly\s*:\s*(AssemblyVersion|AssemblyFileVersion)\s*\(\s*""([^""]*)""\s*\)\s*\]")]
    private static partial Regex AssemblyVersionAttr();

    [GeneratedRegex(@"\[\s*assembly\s*:\s*AssemblyProduct\s*\(\s*""((?:[^""\\]|\\.)*)""\s*\)\s*\]")]
    private static partial Regex ProductAttr();

    [GeneratedRegex(@"<(?:Product|AssemblyProduct)>\s*([^<]+?)\s*</(?:Product|AssemblyProduct)>")]
    private static partial Regex ProductTag();
}

/// <summary>Причина версии модуля в Pilot: по ней видно, что и откуда выложено.</summary>
public static partial class ModuleDeploy
{
    /// <summary>«1.4.0.0 · release/1.4.0 @ 1a2b3c4d - KTN-101; KTN-103».</summary>
    public static string Reason(string version, string reference, string sha, IEnumerable<string>? notes)
    {
        var text = $"{version} · {reference} @ {Short(sha)}";
        var joined = notes is null ? "" : string.Join("; ", notes.Where(n => !string.IsNullOrWhiteSpace(n)));
        if (joined.Length > 300)
            joined = joined[..300].TrimEnd() + "…";
        return joined.Length > 0 ? $"{text} - {joined}" : text;
    }

    public static string Short(string sha) => sha.Length > 8 ? sha[..8] : sha;

    /// <summary>Коммит из причины версии; null - версия выложена не админкой.</summary>
    public static string? ShaFromReason(string? reason) =>
        reason is not null && ShaRegex().Match(reason) is { Success: true } m ? m.Groups[1].Value.ToLowerInvariant() : null;

    /// <summary>Выложенный коммит - это <paramref name="sha"/> (сравнение по префиксу короткого хеша).</summary>
    public static bool SameCommit(string? deployed, string? sha) =>
        !string.IsNullOrEmpty(deployed) && !string.IsNullOrEmpty(sha) &&
        (sha.StartsWith(deployed, StringComparison.OrdinalIgnoreCase) || deployed.StartsWith(sha, StringComparison.OrdinalIgnoreCase));

    [GeneratedRegex(@"@\s*([0-9a-fA-F]{7,40})\b")]
    private static partial Regex ShaRegex();
}
