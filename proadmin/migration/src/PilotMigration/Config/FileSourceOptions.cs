namespace PilotMigration.Config;

/// <summary>
/// Источник переноса - присланная файловая база Pilot: какие файлы брать (каждый выбирается отдельно,
/// имена и папки у присланных выгрузок бывают любые) и где лежат тела её файлов -
/// в самих <c>.dbp</c> их нет.
/// </summary>
public sealed class FileSourceOptions
{
    /// <summary>Основная база (объекты, типы, пользователи) - путь относительно каталога импорта.</summary>
    public string MainFile { get; set; } = "";

    /// <summary>Чаты и сообщения; пусто - не переносить.</summary>
    public string ChatsFile { get; set; } = "";

    /// <summary>
    /// Журнал действий пользователей; пусто - не переносить. Обычно он в разы больше самой базы
    /// и на работу Pilot не влияет.
    /// </summary>
    public string JournalFile { get; set; } = "";

    /// <summary>Копировать тела файлов в архив приёмника. Без этого перенесутся только записи о файлах.</summary>
    public bool FileArchive { get; set; } = true;

    /// <summary>
    /// Где лежат тела файлов. Архив с пустым Id - для всех архивов базы, которые не указаны отдельно
    /// (у присланной базы архив обычно один).
    /// </summary>
    public List<FileSourceArchive> Archives { get; set; } = new();

    public bool HasChats => ChatsFile.Length > 0;

    public bool HasJournal => JournalFile.Length > 0;

    public void Normalize()
    {
        MainFile = Clean(MainFile);
        ChatsFile = Clean(ChatsFile);
        JournalFile = Clean(JournalFile);
        Archives = (Archives ?? new()).Select(a => a.Normalized()).Where(a => !a.IsEmpty).ToList();
    }

    private static string Clean(string? path) => (path ?? "").Trim().Replace('\\', '/').Trim('/');

    /// <summary>Архив для Id из faslist присланной базы: указанный отдельно либо общий.</summary>
    public FileSourceArchive? ArchiveFor(Guid id) =>
        Archives.FirstOrDefault(a => a.Id == id) ?? Archives.FirstOrDefault(a => a.Id == Guid.Empty);
}

public sealed class FileSourceArchive
{
    /// <summary>Id архива из таблицы faslist присланной базы. Пустой - любой архив, не указанный отдельно.</summary>
    public Guid Id { get; set; }

    /// <summary>Папка архива, как её видит сервис миграции (внутри каталога импорта).</summary>
    public string Folder { get; set; } = "";

    public string S3Url { get; set; } = "";
    public string S3Bucket { get; set; } = "";
    public string AccessKey { get; set; } = "";
    public string SecretKey { get; set; } = "";

    public bool IsS3 => !string.IsNullOrWhiteSpace(S3Url);

    public bool IsEmpty => string.IsNullOrWhiteSpace(Folder) && !IsS3;

    public FileSourceArchive Normalized() => new()
    {
        Id = Id,
        Folder = (Folder ?? "").Trim(),
        S3Url = (S3Url ?? "").Trim().TrimEnd('/'),
        S3Bucket = (S3Bucket ?? "").Trim(),
        AccessKey = (AccessKey ?? "").Trim(),
        // Маска приходит обратно, если запрос собран из журнала прошлого запуска: ключ надо ввести заново.
        SecretKey = SecretKey == MigrationConfig.SecretMask ? "" : SecretKey ?? "",
    };
}
