namespace ProAdmin.Migration;

// Модели API сервиса pilot-migration (proadmin/migration). Имена полей совпадают с сервисом.

public sealed class MigrationStatus
{
    public string? Version { get; set; }
    public MigrationRunDto? Active { get; set; }
    public List<MigrationEnvDto> Environments { get; set; } = new();
}

public sealed class MigrationEnvDto
{
    public string Key { get; set; } = "";
    public string Title { get; set; } = "";
    public bool Configured { get; set; }
    public string PilotUrl { get; set; } = "";
}

public sealed class MigrationRequestDto
{
    public string Source { get; set; } = "prod";
    public string Target { get; set; } = "test";

    /// <summary>«Contour» - перенос между контурами, «FileDatabase» - импорт присланной файловой базы.</summary>
    public string SourceKind { get; set; } = "Contour";

    public FileSourceDto FileSource { get; set; } = new();

    /// <summary>«Contour» - в контур, «FileDatabase» - выгрузка в файлы для импорта на другом компьютере.</summary>
    public string TargetKind { get; set; } = "Contour";

    public FileTargetDto FileTarget { get; set; } = new();

    public string Mode { get; set; } = "Full";
    public SelectiveOptionsDto Options { get; set; } = new();

    /// <summary>Скопировать параметры поведения сервера (не логирование) - применяются при перезапуске контейнера приёмника.</summary>
    public bool CopyServerSettings { get; set; }

    public bool IsFileImport => SourceKind == "FileDatabase";

    public bool IsFileExport => TargetKind == "FileDatabase";

    public bool IsFull => Mode == "Full";
}

/// <summary>
/// Присланная файловая база: какие файлы брать (каждый выбирается отдельно - лежать они могут
/// в разных папках и называться как угодно) и где лежат тела её файлов.
/// </summary>
public sealed class FileSourceDto
{
    public string MainFile { get; set; } = "";

    /// <summary>Пусто - чаты не переносятся.</summary>
    public string ChatsFile { get; set; } = "";

    /// <summary>Пусто - журнал действий не переносится.</summary>
    public string JournalFile { get; set; } = "";

    public bool FileArchive { get; set; } = true;
    public List<FileSourceArchiveDto> Archives { get; set; } = new();

    public bool HasChats => ChatsFile.Length > 0;
    public bool HasJournal => JournalFile.Length > 0;
}

/// <summary>Выгрузка в файлы: base.dbp, chats.dbp, journal.dbp, FileArchive/ и manifest.json в каталоге выгрузок.</summary>
public sealed class FileTargetDto
{
    /// <summary>Имя папки выгрузки; пусто - «контур-дата-время».</summary>
    public string Name { get; set; } = "";

    public bool Chats { get; set; } = true;
    public bool Journal { get; set; }
    public bool FileArchive { get; set; } = true;
}

/// <summary>Готовые выгрузки в каталоге выгрузок сервиса.</summary>
public sealed class ExportListDto
{
    public string Directory { get; set; } = "";
    public List<ExportInfoDto> Exports { get; set; } = new();
}

public sealed class ExportInfoDto
{
    public string Name { get; set; } = "";
    public long Bytes { get; set; }
    public DateTimeOffset Modified { get; set; }
    public ExportManifestDto? Manifest { get; set; }
    public string? Problem { get; set; }

    public string Size => Bytes switch
    {
        >= 1L << 30 => $"{Bytes / 1024.0 / 1024 / 1024:N1} ГБ",
        >= 1L << 20 => $"{Bytes / 1024.0 / 1024:N0} МБ",
        _ => $"{Bytes / 1024.0:N0} КБ",
    };
}

public sealed class ExportManifestDto
{
    public DateTimeOffset CreatedAt { get; set; }
    public string CreatedBy { get; set; } = "";
    public string Source { get; set; } = "";
    public string SourceTitle { get; set; } = "";
    public string Database { get; set; } = "";
    public string ServerVersion { get; set; } = "";
    public Dictionary<string, string> Files { get; set; } = new();
    public string? FileArchive { get; set; }
    public Dictionary<string, long> Rows { get; set; } = new();
    public long FileBodies { get; set; }
    public long FileBodiesCopied { get; set; }
    public long FileBodiesMissing { get; set; }
}

public sealed class FileSourceArchiveDto
{
    /// <summary>Пустой - для всех архивов присланной базы (у неё их обычно один).</summary>
    public Guid Id { get; set; }

    public string Folder { get; set; } = "";
    public string S3Url { get; set; } = "";
    public string S3Bucket { get; set; } = "";
    public string AccessKey { get; set; } = "";
    public string SecretKey { get; set; } = "";
}

/// <summary>Файлы .dbp, которые сервис видит в каталоге импорта.</summary>
public sealed class ImportSourcesDto
{
    public string Directory { get; set; } = "";
    public List<ImportFileDto> Files { get; set; } = new();

    /// <summary>Файлы, подходящие на роль: сначала те, чей вид совпал, затем остальные.</summary>
    public IEnumerable<ImportFileDto> OfKind(string kind) => Files.Where(f => f.Kind == kind);

    public IEnumerable<ImportFileDto> NotOfKind(string kind) => Files.Where(f => f.Kind != kind);
}

public sealed class ImportFileDto
{
    /// <summary>Путь относительно каталога импорта.</summary>
    public string Path { get; set; } = "";

    public long Bytes { get; set; }

    /// <summary>Чем файл оказался по набору таблиц: Main, Chats, Journal или Unknown.</summary>
    public string Kind { get; set; } = "Unknown";

    public Guid? DatabaseId { get; set; }
    public string? Problem { get; set; }

    public string KindTitle => Kind switch
    {
        "Main" => "основная база",
        "Chats" => "чаты",
        "Journal" => "журнал действий",
        _ => "не распознан",
    };

    public string Describe()
    {
        var parts = new List<string> { KindTitle, Size() };
        if (DatabaseId is { } id)
            parts.Add("база " + id.ToString()[..8]);
        if (Problem is not null)
            parts.Add(Problem);
        return string.Join(", ", parts);
    }

    private string Size() => Bytes switch
    {
        >= 1L << 30 => $"{Bytes / 1024.0 / 1024 / 1024:N1} ГБ",
        >= 1L << 20 => $"{Bytes / 1024.0 / 1024:N0} МБ",
        _ => $"{Bytes} Б",
    };
}

public sealed class SelectiveOptionsDto
{
    public bool Tree { get; set; }
    public bool TreeAll { get; set; } = true;
    public List<string> TreeIds { get; set; } = new();
    public bool Users { get; set; }
    public bool ClientModules { get; set; }
    public bool WebModules { get; set; }
    public bool ServerModules { get; set; }
    public bool Automations { get; set; }
    public bool Reports { get; set; }
    public bool FileArchive { get; set; } = true;
}

public sealed class CheckItemDto
{
    public string Level { get; set; } = "Info";
    public string Text { get; set; } = "";

    public string Css => Level switch { "Blocker" => "error", "Warning" => "warn", _ => "info" };
}

public sealed class MigrationPlanDto
{
    public MigrationRequestDto Request { get; set; } = new();
    public List<CheckItemDto> Checks { get; set; } = new();
    public bool CanRun { get; set; }
    public List<string> Summary { get; set; } = new();
    public string? TargetDatabase { get; set; }
}

public sealed class RunLogEntryDto
{
    public DateTimeOffset Time { get; set; }
    public string Level { get; set; } = "info";
    public string Text { get; set; } = "";
}

public sealed class MigrationRunDto
{
    public Guid Id { get; set; }
    public MigrationRequestDto Request { get; set; } = new();
    public string RequestedBy { get; set; } = "";
    public string Trigger { get; set; } = "";
    public Guid? ScheduleId { get; set; }
    public string Status { get; set; } = "";
    public string? Phase { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public string? Error { get; set; }
    public List<CheckItemDto> Checks { get; set; } = new();
    public List<RunLogEntryDto> Log { get; set; } = new();
    public Dictionary<string, long> Stats { get; set; } = new();

    public bool IsFinished => Status is "Succeeded" or "Failed" or "Blocked" or "Canceled";

    public TimeSpan? Duration => StartedAt is { } s ? (FinishedAt ?? DateTimeOffset.Now) - s : null;
}

public sealed class ScheduleDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public MigrationRequestDto Request { get; set; } = new();
    public string Kind { get; set; } = "Weekly";
    public int IntervalMinutes { get; set; } = 1440;
    public string TimeOfDay { get; set; } = "03:00";
    public List<int> Days { get; set; } = new();
    public string? CreatedBy { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
    public DateTimeOffset? LastRunAt { get; set; }
    public Guid? LastRunId { get; set; }
    public string? LastResult { get; set; }
    public DateTimeOffset? NextRunAt { get; set; }
    public string? Description { get; set; }
    public string? RequestDescription { get; set; }
}

public sealed class MigrationConfigDto
{
    public List<MigrationEnvConfigDto> Environments { get; set; } = new();
    public int KeepRuns { get; set; } = 200;
    public int DatabaseStateTimeoutMinutes { get; set; } = 10;
    public DateTimeOffset? UpdatedAt { get; set; }
    public string? UpdatedBy { get; set; }
}

public sealed class MigrationEnvConfigDto
{
    public string Key { get; set; } = "";
    public string Title { get; set; } = "";
    public string PilotUrl { get; set; } = "";
    public string PilotAdminLogin { get; set; } = "";
    public string PilotAdminPassword { get; set; } = "";
    public string SettingsXmlPath { get; set; } = "";
    public List<PathMappingDto> PathMappings { get; set; } = new();
    public List<S3EndpointDto> S3 { get; set; } = new();
}

public sealed class PathMappingDto
{
    public string ServerPrefix { get; set; } = "";
    public string LocalPath { get; set; } = "";
}

public sealed class S3EndpointDto
{
    public string ServerUrl { get; set; } = "";
    public string Url { get; set; } = "";
    public string AccessKey { get; set; } = "";
    public string SecretKey { get; set; } = "";
}

public sealed class ConnectionCheckDto
{
    public string Env { get; set; } = "";
    public string Title { get; set; } = "";
    public string Level { get; set; } = "";
    public string Text { get; set; } = "";
}

public static class MigrationTexts
{
    public static string Status(string status) => status switch
    {
        "Succeeded" => "успешно",
        "Failed" => "ошибка",
        "Blocked" => "не начат",
        "Canceled" => "отменён",
        "Running" => "выполняется",
        _ => "в очереди",
    };

    public static string StatusCss(string status) => status switch
    {
        "Succeeded" => "badge-synced",
        "Failed" => "badge-failed",
        "Blocked" or "Canceled" => "badge-differs",
        _ => "badge-running",
    };

    public static string Describe(MigrationRequestDto r) =>
        DescribeData(r) + (r.CopyServerSettings ? " + настройки сервера" : "");

    private static string DescribeData(MigrationRequestDto r)
    {
        if (r.IsFileExport)
        {
            var exported = new List<string> { $"Выгрузка в файлы «{r.FileTarget.Name}»" };
            if (r.FileTarget.Chats) exported.Add("чаты");
            if (r.FileTarget.Journal) exported.Add("журнал действий");
            if (r.FileTarget.FileArchive) exported.Add("файлы");
            return string.Join(", ", exported);
        }
        if (r.IsFileImport)
        {
            var imported = new List<string> { "Импорт файловой базы" };
            if (r.FileSource.MainFile.Length > 0) imported.Add($"«{r.FileSource.MainFile}»");
            if (r.FileSource.HasChats) imported.Add("чаты");
            if (r.FileSource.HasJournal) imported.Add("журнал действий");
            if (r.FileSource.FileArchive) imported.Add("файлы");
            return string.Join(", ", imported);
        }
        if (r.IsFull)
            return "Полная копия";
        var o = r.Options;
        var parts = new List<string>();
        if (o.Tree) parts.Add(o.TreeAll ? "дерево объектов" : $"объекты по Id ({o.TreeIds.Count})");
        if (o.Users) parts.Add("пользователи");
        if (o.ClientModules) parts.Add("клиентские модули");
        if (o.WebModules) parts.Add("веб-модули");
        if (o.ServerModules) parts.Add("серверные модули");
        if (o.Automations) parts.Add("автоматизации");
        if (o.Reports) parts.Add("отчёты");
        if (o.FileArchive && (o.Tree || o.ClientModules || o.WebModules || o.ServerModules || o.Reports)) parts.Add("файлы");
        return "Выборочно: " + (parts.Count > 0 ? string.Join(", ", parts) : "-");
    }

    public static string EnvTitle(string key) =>
        key switch { "prod" => "Прод", "test" => "Тест", "stage" => "Стейдж", "file" => "Файловая база", _ => key };
}
