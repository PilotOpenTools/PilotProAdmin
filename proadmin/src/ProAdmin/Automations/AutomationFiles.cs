using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace ProAdmin.Automations;

/// <summary>
/// Раскладка автоматизации по файлам репозитория:
/// <c>AutomationName.json</c> - триггеры, <c>AutomationName/AutomationName.cs</c> - C#-вставка.
/// </summary>
public static partial class AutomationFiles
{
    private static readonly char[] InvalidChars = { '/', '\\', ':', '*', '?', '"', '<', '>', '|' };
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    public static string SafeName(string name) =>
        new(name.Trim().Select(c => InvalidChars.Contains(c) || char.IsControl(c) ? '_' : c).ToArray());

    public static string NormalizeFolder(string? folder) =>
        (folder ?? "").Replace('\\', '/').Trim().Trim('/');

    /// <summary>Файлы лежат в корне репозитория автоматизации.</summary>
    public static string JsonPath(string name) => SafeName(name) + ".json";

    public static string CsPath(string name)
    {
        var safe = SafeName(name);
        return $"{safe}/{safe}.cs";
    }

    public const string ReadmePath = "README.md";

    /// <summary>
    /// Имя автоматизации = имя проекта GitLab (Project name): по нему сервер сопоставляется с репозиторием.
    /// Допустимы русские буквы и пробелы; путь (slug) проекта генерируется транслитом и на сопоставление не влияет.
    /// </summary>
    public static string? NameError(string? name) => GitLab.ProjectNames.NameError(name);

    /// <summary>
    /// Предложение допустимого имени, максимально близкого к исходному: запрещённые GitLab символы убираются.
    /// «actualFor attribute management (Server and Client)» / «actualFor attribute management Server and Client».
    /// </summary>
    public static string SuggestName(string name) => GitLab.ProjectNames.Suggest(name);

    public static string NormalizeText(string? text) =>
        (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n').TrimStart('﻿');

    /// <summary>Текст в том виде, в котором он пишется и в Pilot, и в Git: LF, один перевод строки в конце.</summary>
    public static string PrepareForStorage(string? text)
    {
        var t = NormalizeText(text).TrimEnd();
        return t.Length == 0 ? "" : t + "\n";
    }

    public static bool SameContent(string? a, string? b) =>
        NormalizeText(a).TrimEnd() == NormalizeText(b).TrimEnd();

    /// <summary>SHA-1 git-блоба (как <c>git hash-object</c>).</summary>
    public static string GitBlobSha(string content)
    {
        var body = Utf8NoBom.GetBytes(content);
        var header = Encoding.ASCII.GetBytes($"blob {body.Length}\0");
        var buffer = new byte[header.Length + body.Length];
        header.CopyTo(buffer, 0);
        body.CopyTo(buffer, header.Length);
        return Convert.ToHexString(SHA1.HashData(buffer)).ToLowerInvariant();
    }

    /// <summary>
    /// Совпадает ли текст с блобом из дерева репозитория с точностью до переводов строк
    /// и хвостовых пробелов. Позволяет строить список без скачивания каждого файла.
    /// </summary>
    public static bool MatchesBlob(string? content, string? blobSha)
    {
        if (string.IsNullOrEmpty(blobSha))
            return NormalizeText(content).Trim().Length == 0;

        var raw = content ?? "";
        var lf = NormalizeText(raw);
        var trimmed = lf.TrimEnd();
        var candidates = new[]
        {
            raw, lf, trimmed, trimmed + "\n",
            lf.Replace("\n", "\r\n"), trimmed.Replace("\n", "\r\n"), trimmed.Replace("\n", "\r\n") + "\r\n",
        };
        return candidates.Distinct().Any(c => GitBlobSha(c) == blobSha);
    }

    public static string ContentHash(string? triggers, string? cSharpCode, bool isPaused)
    {
        var text = $"{NormalizeText(triggers).TrimEnd()}\0{NormalizeText(cSharpCode).TrimEnd()}\0{isPaused}";
        return Convert.ToHexString(SHA256.HashData(Utf8NoBom.GetBytes(text))).ToLowerInvariant();
    }

    /// <summary>Часть имени временной ветки: только латиница, цифры, точка, дефис (русское имя - транслитом).</summary>
    public static string BranchSlug(string name)
    {
        var slug = NonSlugChars().Replace(GitLab.ProjectNames.Transliterate(name), "-").Trim('-', '.');
        if (slug.Length > 40)
            slug = slug[..40].Trim('-', '.');
        return slug.Length == 0 ? "automation" : slug.ToLowerInvariant();
    }

    [GeneratedRegex(@"[^A-Za-z0-9._-]+")]
    private static partial Regex NonSlugChars();
}
