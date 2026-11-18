using System.Text.Json;
using PilotMigration.Database;

namespace PilotMigration.Config;

/// <summary>Что лежит в выгрузке: <c>manifest.json</c>. По нему видно, с какой базы и когда она снята.</summary>
public sealed class ExportManifest
{
    public const string FormatName = "pilot-migration-export";

    public string Format { get; set; } = FormatName;
    public int FormatVersion { get; set; } = 1;
    public DateTimeOffset CreatedAt { get; set; }
    public string CreatedBy { get; set; } = "";

    /// <summary>Контур-источник: prod, test, stage.</summary>
    public string Source { get; set; } = "";

    public string SourceTitle { get; set; } = "";
    public string Database { get; set; } = "";
    public Guid DatabaseId { get; set; }

    /// <summary>Версия сервера Pilot и схема PostgreSQL, с которых снята выгрузка: импорт сверяет структуру, а не номер.</summary>
    public string ServerVersion { get; set; } = "";

    public string Schema { get; set; } = "";

    /// <summary>Файлы выгрузки: роль / имя файла (Main, Messages, Journal).</summary>
    public Dictionary<string, string> Files { get; set; } = new();

    /// <summary>Папка с телами файлов; пусто - тела не выгружались.</summary>
    public string? FileArchive { get; set; }

    public Dictionary<string, long> Rows { get; set; } = new();
    public long FileBodies { get; set; }
    public long FileBodiesCopied { get; set; }
    public long FileBodiesMissing { get; set; }
    public long FileBytes { get; set; }
}

/// <summary>Готовая выгрузка в каталоге выгрузок.</summary>
public sealed record ExportInfo(string Name, long Bytes, DateTimeOffset Modified, ExportManifest? Manifest, string? Problem);

/// <summary>
/// Каталог выгрузок (<c>EXPORT_ROOT</c>): папка на выгрузку. Пока выгрузка идёт, папка называется
/// <c>&lt;имя&gt;.partial</c> - недописанную выгрузку не спутать с готовой.
/// </summary>
public static class ExportFiles
{
    public const string PartialSuffix = ".partial";
    public const string ManifestName = "manifest.json";
    public const string ArchiveFolder = "FileArchive";

    public static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static string FileName(DbpFile file) => file switch
    {
        DbpFile.Main => "base.dbp",
        DbpFile.Messages => "chats.dbp",
        DbpFile.Journal => "journal.dbp",
        _ => throw new ArgumentOutOfRangeException(nameof(file)),
    };

    public static List<ExportInfo> List(string exportDirectory)
    {
        var result = new List<ExportInfo>();
        if (string.IsNullOrWhiteSpace(exportDirectory) || !Directory.Exists(exportDirectory))
            return result;
        foreach (var dir in new DirectoryInfo(exportDirectory).EnumerateDirectories())
        {
            if (dir.Name.EndsWith(PartialSuffix, StringComparison.OrdinalIgnoreCase))
                continue;
            long bytes = 0;
            try
            {
                bytes = dir.EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
            }
            catch (IOException) { }
            ExportManifest? manifest = null;
            string? problem = null;
            var path = Path.Combine(dir.FullName, ManifestName);
            try
            {
                manifest = File.Exists(path) ? JsonSerializer.Deserialize<ExportManifest>(File.ReadAllText(path)) : null;
                if (manifest is null)
                    problem = $"нет {ManifestName}";
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                problem = $"{ManifestName} не читается: {ex.Message}";
            }
            result.Add(new ExportInfo(dir.Name, bytes, dir.LastWriteTime, manifest, problem));
        }
        return result.OrderByDescending(e => e.Modified).ToList();
    }
}
