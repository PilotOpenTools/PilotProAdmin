using System.Text;
using System.Text.RegularExpressions;

namespace ProAdmin.GitLab;

/// <summary>
/// Имена проектов GitLab. Автоматизация и отчёт сопоставляются с репозиторием по <b>имени</b> проекта
/// (Project name), а не по пути (slug). Имя может быть русским и с пробелами; путь генерируется транслитом.
/// </summary>
public static partial class ProjectNames
{
    public const int MaxLength = 255;

    /// <summary>
    /// Правило имени проекта GitLab: начинается с буквы, цифры или «_»; дальше буквы (в т.ч. русские),
    /// цифры, пробел, «_», «-», «.», «+».
    /// </summary>
    public static string? NameError(string? name, string what = "автоматизации")
    {
        if (string.IsNullOrWhiteSpace(name))
            return $"Укажите имя {what}.";
        if (name != name.Trim())
            return "Имя не должно начинаться или заканчиваться пробелом.";
        if (name.Length > MaxLength)
            return $"Имя длиннее {MaxLength} символов.";
        if (!IsFirstChar(name[0]))
            return "Имя должно начинаться с буквы, цифры или «_» (так требует GitLab).";
        var bad = name.Where(c => !IsNameChar(c)).Distinct().ToArray();
        if (bad.Length > 0)
            return $"Недопустимые для имени проекта GitLab символы: {string.Join(" ", bad.Select(c => $"«{c}»"))}. " +
                   "Можно: буквы, цифры, пробел, «_», «-», «.», «+».";
        if (name.EndsWith(".git", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".atom", StringComparison.OrdinalIgnoreCase))
            return "Имя не может заканчиваться на .git или .atom.";
        return null;
    }

    /// <summary>
    /// Допустимое имя, максимально близкое к исходному: запрещённые символы / пробел, повторные пробелы схлопываются.
    /// «actualFor attribute management (Server and Client)» / «actualFor attribute management Server and Client».
    /// </summary>
    public static string Suggest(string? name, string fallback = "Automation")
    {
        var sb = new StringBuilder();
        foreach (var c in name ?? "")
            sb.Append(IsNameChar(c) ? c : ' ');
        var result = Spaces().Replace(sb.ToString(), " ").Trim();
        while (result.Length > 0 && !IsFirstChar(result[0]))
            result = result[1..].TrimStart();
        while (result.EndsWith(".git", StringComparison.OrdinalIgnoreCase) || result.EndsWith(".atom", StringComparison.OrdinalIgnoreCase))
            result = result[..result.LastIndexOf('.')].TrimEnd();
        if (result.Length == 0)
            result = fallback;
        return result.Length > MaxLength ? result[..MaxLength].TrimEnd() : result;
    }

    /// <summary>
    /// Ключ для нестрогого сравнения имён (регистр, запрещённые символы и лишние пробелы не важны):
    /// «Отчёт (вторая форма) #» и «Отчёт вторая форма» совпадают.
    /// </summary>
    public static string Key(string? name) => Suggest(name, "").ToLowerInvariant().Replace('ё', 'е');

    /// <summary>Путь (slug) проекта: транслит, латиница в нижнем регистре, цифры, «-», «_», «.».</summary>
    public static string Slug(string? name, string fallback = "project")
    {
        var slug = NonSlug().Replace(Transliterate(name ?? "").ToLowerInvariant(), "-");
        slug = Dashes().Replace(slug, "-").Trim('-', '.', '_');
        if (slug.EndsWith(".git") || slug.EndsWith(".atom"))
            slug = slug.Replace('.', '-');
        if (slug.Length > 100)
            slug = slug[..100].Trim('-', '.', '_');
        return slug.Length == 0 ? fallback : slug;
    }

    public static string Transliterate(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            var lower = char.ToLowerInvariant(c);
            if (Translit.TryGetValue(lower, out var t))
                sb.Append(c != lower && t.Length > 0 ? char.ToUpperInvariant(t[0]) + t[1..] : t);
            else
                sb.Append(c);
        }
        return sb.ToString();
    }

    private static bool IsFirstChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    private static bool IsNameChar(char c) => char.IsLetterOrDigit(c) || c is '_' or '-' or '.' or ' ' or '+';

    private static readonly Dictionary<char, string> Translit = new()
    {
        ['а'] = "a", ['б'] = "b", ['в'] = "v", ['г'] = "g", ['д'] = "d", ['е'] = "e", ['ё'] = "e", ['ж'] = "zh",
        ['з'] = "z", ['и'] = "i", ['й'] = "y", ['к'] = "k", ['л'] = "l", ['м'] = "m", ['н'] = "n", ['о'] = "o",
        ['п'] = "p", ['р'] = "r", ['с'] = "s", ['т'] = "t", ['у'] = "u", ['ф'] = "f", ['х'] = "h", ['ц'] = "ts",
        ['ч'] = "ch", ['ш'] = "sh", ['щ'] = "sch", ['ъ'] = "", ['ы'] = "y", ['ь'] = "", ['э'] = "e", ['ю'] = "yu", ['я'] = "ya",
    };

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    [GeneratedRegex(@"[^a-z0-9._-]+")]
    private static partial Regex NonSlug();

    [GeneratedRegex(@"-{2,}")]
    private static partial Regex Dashes();
}
