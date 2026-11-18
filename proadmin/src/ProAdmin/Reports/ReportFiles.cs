using System.Security.Cryptography;
using System.Text;
using ProAdmin.Automations;
using ProAdmin.GitLab;

namespace ProAdmin.Reports;

/// <summary>
/// Файл отчёта в репозитории: <c>Имя отчёта.repx</c> в корне. Для репозиториев, созданных раньше,
/// подходит и любой единственный .repx или .repx с именем отчёта в любой папке.
/// </summary>
public static class ReportFiles
{
    public const string Extension = ".repx";

    public static string DefaultPath(string reportName) => AutomationFiles.SafeName(reportName) + Extension;

    /// <summary>Путь .repx отчёта в дереве ветки; null - файла нет.</summary>
    public static string? FindPath(IEnumerable<GitLabTreeItem>? tree, string reportName)
    {
        var repx = (tree ?? Enumerable.Empty<GitLabTreeItem>())
            .Where(i => i.Type == "blob" && i.Path.EndsWith(Extension, StringComparison.OrdinalIgnoreCase))
            .Select(i => i.Path)
            .ToList();
        if (repx.Count == 0)
            return null;

        var preferred = DefaultPath(reportName);
        var exact = repx.FirstOrDefault(p => string.Equals(p, preferred, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
            return exact;

        var key = ProjectNames.Key(reportName);
        var byName = repx.Where(p => ProjectNames.Key(Path.GetFileNameWithoutExtension(p)) == key).ToList();
        if (byName.Count == 1)
            return byName[0];

        return repx.Count == 1 ? repx[0] : null;
    }

    /// <summary>Blob id файла отчёта в дереве ветки.</summary>
    public static string? FindBlob(IEnumerable<GitLabTreeItem>? tree, string reportName)
    {
        var path = FindPath(tree, reportName);
        return path is null ? null : tree!.First(i => i.Type == "blob" && i.Path == path).Id;
    }

    /// <summary>SHA-1 git-блоба (как <c>git hash-object</c>) для точных байтов файла.</summary>
    public static string BlobSha(byte[] content)
    {
        var header = Encoding.ASCII.GetBytes($"blob {content.Length}\0");
        var buffer = new byte[header.Length + content.Length];
        header.CopyTo(buffer, 0);
        content.CopyTo(buffer, header.Length);
        return Convert.ToHexString(SHA1.HashData(buffer)).ToLowerInvariant();
    }

    /// <summary>Текст без BOM, с LF и без хвостовых пробелов - чтобы сравнивать с точностью до переводов строк.</summary>
    public static byte[] Normalize(byte[] content)
    {
        var text = Encoding.UTF8.GetString(content);
        if (text.Length > 0 && text[0] == '﻿')
            text = text[1..];
        text = text.Replace("\r\n", "\n").Replace('\r', '\n').TrimEnd();
        return Encoding.UTF8.GetBytes(text);
    }

    public static bool SameContent(byte[]? a, byte[]? b) =>
        a is not null && b is not null && (a.AsSpan().SequenceEqual(b) || Normalize(a).AsSpan().SequenceEqual(Normalize(b)));
}
