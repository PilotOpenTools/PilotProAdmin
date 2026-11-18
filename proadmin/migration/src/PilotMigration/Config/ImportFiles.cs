using PilotMigration.Database;

namespace PilotMigration.Config;

/// <summary>Чем файл оказался по набору таблиц, а не по имени: присылают их как придётся.</summary>
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter))]
public enum DbpFileKind
{
    Unknown,
    Main,
    Chats,
    Journal,
}

/// <summary>Файл присланной базы в каталоге импорта.</summary>
public sealed record ImportFileInfo(string Path, long Bytes, DbpFileKind Kind, Guid? DatabaseId, string? Problem);

/// <summary>
/// Файлы <c>.dbp</c> в каталоге импорта. Импортировать можно только из него - путь за его пределы отвергается.
/// </summary>
public static class ImportFiles
{
    /// <summary>Сколько файлов показывать: каталог импорта - не файловый менеджер.</summary>
    private const int Limit = 500;

    /// <summary>Полный путь к файлу базы. Ошибка, если путь выходит за каталог импорта.</summary>
    public static string Resolve(string importDirectory, string path)
    {
        if (string.IsNullOrWhiteSpace(importDirectory))
            throw new MigrationException("В сервисе миграции не задан каталог импорта (Migration__ImportDirectory).");
        if (string.IsNullOrWhiteSpace(path))
            throw new MigrationException("Не указан файл базы.");

        var root = System.IO.Path.GetFullPath(importDirectory);
        var full = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, path));
        var prefix = root.EndsWith(System.IO.Path.DirectorySeparatorChar) ? root : root + System.IO.Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.Ordinal) && full != root)
            throw new MigrationException($"Путь «{path}» вне каталога импорта {root}.");
        return full;
    }

    /// <summary>Все файлы .dbp каталога импорта с их видом - для выбора в админке.</summary>
    public static List<ImportFileInfo> List(string importDirectory)
    {
        var result = new List<ImportFileInfo>();
        if (string.IsNullOrWhiteSpace(importDirectory) || !Directory.Exists(importDirectory))
            return result;

        var root = Path.GetFullPath(importDirectory);
        var files = Directory
            .EnumerateFiles(root, "*.dbp", SearchOption.AllDirectories)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .Take(Limit);
        foreach (var path in files)
            result.Add(Describe(root, path));
        return result;
    }

    private static ImportFileInfo Describe(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
        var bytes = new FileInfo(path).Length;
        try
        {
            using var file = DbpSqliteFile.Open(path);
            return new ImportFileInfo(relative, bytes, file.Kind, file.DatabaseId, null);
        }
        catch (MigrationException ex)
        {
            return new ImportFileInfo(relative, bytes, DbpFileKind.Unknown, null, ex.Message);
        }
    }
}
