using System.Text.RegularExpressions;

namespace ProAdmin.Automations;

/// <summary>Версия автоматизации из поля <c>_comment_</c>: «Version: X.Y.Z».</summary>
public readonly partial record struct AutomationVersion(int Major, int Minor, int Patch) : IComparable<AutomationVersion>
{
    /// <summary>Первое вхождение «Version: X.Y.Z» в тексте JSON.</summary>
    public static AutomationVersion? Find(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return null;

        var m = VersionRegex().Match(text);
        if (!m.Success || !int.TryParse(m.Groups[1].Value, out var major) ||
            !int.TryParse(m.Groups[2].Value, out var minor) || !int.TryParse(m.Groups[3].Value, out var patch))
            return null;

        return new AutomationVersion(major, minor, patch);
    }

    /// <summary>
    /// JSON с заменённой версией (первое «Version: X.Y.Z»); если версии нет - <c>_comment_</c> добавляется
    /// первым полем первого триггера. Отдельная запись создаётся, только если триггеров ещё нет.
    /// </summary>
    public static string WithVersion(string json, AutomationVersion version)
    {
        json = ToStorageLayout(json ?? "");
        var m = VersionRegex().Match(json);
        if (m.Success)
            return json[..m.Index] + $"Version: {version}" + json[(m.Index + m.Length)..];

        // У первого триггера уже есть свой _comment_ (описание) - версия дописывается в его начало,
        // иначе в объекте окажется второй ключ _comment_.
        var at = FirstTriggerCommentValue(json);
        if (at >= 0)
            return json[..at] + $"Version: {version}" + (json[at] == '"' ? "" : ". ") + json[at..];

        var comment = $"\"_comment_\": \"Version: {version}\"";
        var first = FirstTriggerRegex().Match(json);
        if (first.Success)
        {
            var ws = first.Groups["ws"].Value;
            return json[..first.Index] + first.Groups["pre"].Value + "{" + ws + comment + "," + ws + json[(first.Index + first.Length)..];
        }

        // Триггеров нет - заготовка; при отправке без триггеров линтер это не пропустит.
        var bracket = json.IndexOf('[');
        return bracket >= 0
            ? json[..(bracket + 1)] + $"\n  {{ {comment} }}" + (json[(bracket + 1)..].TrimStart().StartsWith(']') ? "\n" : ",") + json[(bracket + 1)..]
            : $"[\n  {{ {comment} }}\n]\n";
    }

    /// <summary>
    /// Приводит сценарий к виду, который понимает клиент Pilot:
    /// корневой объект оборачивается в массив, а отдельная запись <c>{ "_comment_": … }</c> в начале массива
    /// переносится первым полем в следующий триггер. Элемент без <c>"when"</c> клиент Pilot не переносит:
    /// <c>AutomationEventHandler.RebuildSnapshot</c> падает с ArgumentNullException (key).
    /// </summary>
    public static string ToStorageLayout(string json)
    {
        if (json.TrimStart().StartsWith('{'))
            json = "[\n" + json.Trim() + "\n]\n";

        var m = StandaloneCommentRegex().Match(json);
        if (!m.Success)
            return json;

        var ws = m.Groups["ws"].Value;
        var head = json[..m.Index] + m.Groups["pre"].Value + "{" + ws;
        var tail = json[(m.Index + m.Length)..];

        // У триггера уже есть свой _comment_ - текст отдельной записи дописывается в его начало.
        var at = FirstTriggerCommentValue(head + tail);
        if (at >= 0)
        {
            var merged = head + tail;
            var text = m.Groups["c"].Value[1..^1];
            return text.Length == 0 ? merged : merged[..at] + text + (merged[at] == '"' ? "" : ". ") + merged[at..];
        }

        return head + "\"_comment_\": " + m.Groups["c"].Value + "," + ws + tail;
    }

    /// <summary>
    /// Позиция начала значения поля <c>_comment_</c> первого триггера (сразу после открывающей кавычки)
    /// или -1, если такого поля нет. Вложенные объекты (params, then) не просматриваются.
    /// </summary>
    private static int FirstTriggerCommentValue(string json)
    {
        var first = FirstTriggerRegex().Match(json);
        if (!first.Success)
            return -1;

        var depth = 0;
        for (var i = first.Index + first.Groups["pre"].Length; i < json.Length; i++)
        {
            var c = json[i];
            if (c == '"')
            {
                var end = StringEnd(json, i);
                if (end < 0)
                    return -1;
                if (depth == 1 && json.AsSpan(i, end - i + 1).SequenceEqual("\"_comment_\""))
                {
                    var j = SkipWhitespace(json, end + 1);
                    if (j < json.Length && json[j] == ':')
                    {
                        j = SkipWhitespace(json, j + 1);
                        return j < json.Length && json[j] == '"' ? j + 1 : -1;
                    }
                }
                i = end;
            }
            else if (c is '{' or '[') depth++;
            else if (c is '}' or ']' && --depth == 0) return -1;
        }
        return -1;
    }

    private static int StringEnd(string s, int openQuote)
    {
        for (var i = openQuote + 1; i < s.Length; i++)
        {
            if (s[i] == '\\') i++;
            else if (s[i] == '"') return i;
        }
        return -1;
    }

    private static int SkipWhitespace(string s, int i)
    {
        while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
        return i;
    }

    /// <summary>Есть ли в начале массива отдельная запись, состоящая только из <c>_comment_</c>.</summary>
    public static bool HasStandaloneComment(string? json) => StandaloneCommentOnlyRegex().IsMatch(json ?? "");

    // «[ { "_comment_": "…" }, {<ws>"» - отдельная запись версии перед первым триггером.
    [GeneratedRegex("""^(?<pre>\s*\[\s*)\{\s*"_comment_"\s*:\s*(?<c>"(?:[^"\\]|\\.)*")\s*\}\s*,\s*\{(?<ws>\s*)(?=")""")]
    private static partial Regex StandaloneCommentRegex();

    [GeneratedRegex("""^\s*\[\s*\{\s*"_comment_"\s*:\s*"(?:[^"\\]|\\.)*"\s*\}""")]
    private static partial Regex StandaloneCommentOnlyRegex();

    // «[ {<ws>"» - начало первого триггера.
    [GeneratedRegex("""^(?<pre>\s*\[\s*)\{(?<ws>\s*)(?=")""")]
    private static partial Regex FirstTriggerRegex();

    public AutomationVersion NextPatch() => new(Major, Minor, Patch + 1);

    public static bool TryParseTag(string? tag, out AutomationVersion version)
    {
        version = default;
        var m = TagRegex().Match(tag ?? "");
        if (!m.Success)
            return false;
        version = new AutomationVersion(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value));
        return true;
    }

    [GeneratedRegex(@"^v(\d+)\.(\d+)\.(\d+)$")]
    private static partial Regex TagRegex();

    public int CompareTo(AutomationVersion other)
    {
        var c = Major.CompareTo(other.Major);
        if (c != 0) return c;
        c = Minor.CompareTo(other.Minor);
        return c != 0 ? c : Patch.CompareTo(other.Patch);
    }

    public static bool operator >(AutomationVersion a, AutomationVersion b) => a.CompareTo(b) > 0;
    public static bool operator <(AutomationVersion a, AutomationVersion b) => a.CompareTo(b) < 0;
    public static bool operator >=(AutomationVersion a, AutomationVersion b) => a.CompareTo(b) >= 0;
    public static bool operator <=(AutomationVersion a, AutomationVersion b) => a.CompareTo(b) <= 0;

    public override string ToString() => $"{Major}.{Minor}.{Patch}";

    [GeneratedRegex(@"Version\s*:\s*v?(\d+)\.(\d+)\.(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex VersionRegex();
}
