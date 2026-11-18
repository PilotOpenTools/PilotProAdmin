using ProAdmin.Migration;

namespace ProAdmin.Pages.Migration;

/// <summary>Поля «откуда, куда, что переносить» - общие для ручного запуска и расписания.</summary>
public sealed class RequestForm
{
    /// <summary>Контур-источник либо <see cref="FileSourceValue"/> - присланная файловая база.</summary>
    public const string FileSourceValue = "file";

    public string Source { get; set; } = "prod";
    public string Target { get; set; } = "test";

    /// <summary>Файлы присланной базы - пути в каталоге импорта. Чаты и журнал пустые - не переносятся.</summary>
    public string? ImportMainFile { get; set; }

    public string? ImportChatsFile { get; set; }
    public string? ImportJournalFile { get; set; }
    public bool ImportFileArchive { get; set; } = true;

    /// <summary>Выгрузка в файлы (приёмник <see cref="FileSourceValue"/>): имя папки и что выгружать.</summary>
    public string? ExportName { get; set; }

    public bool ExportChats { get; set; } = true;
    public bool ExportJournal { get; set; }
    public bool ExportFileArchive { get; set; } = true;

    /// <summary>Где лежат тела файлов присланной базы: папка внутри каталога импорта…</summary>
    public string? ArchiveFolder { get; set; }

    /// <summary>…либо S3.</summary>
    public string? ArchiveS3Url { get; set; }

    public string? ArchiveS3Bucket { get; set; }
    public string? ArchiveAccessKey { get; set; }
    public string? ArchiveSecretKey { get; set; }

    public string Mode { get; set; } = "Full";
    public bool Tree { get; set; }
    public bool TreeAll { get; set; } = true;
    public string? TreeIds { get; set; }
    public bool Users { get; set; }
    public bool ClientModules { get; set; }
    public bool WebModules { get; set; }
    public bool ServerModules { get; set; }
    public bool Automations { get; set; }
    public bool Reports { get; set; }
    public bool FileArchive { get; set; } = true;
    public bool CopyServerSettings { get; set; }

    public bool IsFileSource => Source == FileSourceValue;

    public bool IsFileTarget => Target == FileSourceValue;

    public MigrationRequestDto ToDto() => new()
    {
        Source = Source,
        Target = Target,
        SourceKind = IsFileSource ? "FileDatabase" : "Contour",
        FileSource = new FileSourceDto
        {
            MainFile = (ImportMainFile ?? "").Trim(),
            ChatsFile = (ImportChatsFile ?? "").Trim(),
            JournalFile = (ImportJournalFile ?? "").Trim(),
            FileArchive = ImportFileArchive,
            Archives = Archives(),
        },
        TargetKind = IsFileTarget ? "FileDatabase" : "Contour",
        FileTarget = new FileTargetDto
        {
            Name = (ExportName ?? "").Trim(),
            Chats = ExportChats,
            Journal = ExportJournal,
            FileArchive = ExportFileArchive,
        },
        // Файловая база переносится и выгружается только целиком.
        Mode = IsFileSource || IsFileTarget || Mode != "Selective" ? "Full" : "Selective",
        CopyServerSettings = !IsFileSource && !IsFileTarget && CopyServerSettings,
        Options = new SelectiveOptionsDto
        {
            Tree = Tree,
            TreeAll = TreeAll,
            TreeIds = (TreeIds ?? "")
                .Split(new[] { '\n', '\r', ',', ';', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList(),
            Users = Users,
            ClientModules = ClientModules,
            WebModules = WebModules,
            ServerModules = ServerModules,
            Automations = Automations,
            Reports = Reports,
            FileArchive = FileArchive,
        },
    };

    /// <summary>Один архив на всю присланную базу (Id пустой - для всех её архивов).</summary>
    private List<FileSourceArchiveDto> Archives()
    {
        if (!IsFileSource)
            return new();
        var archive = new FileSourceArchiveDto
        {
            Folder = (ArchiveFolder ?? "").Trim(),
            S3Url = (ArchiveS3Url ?? "").Trim(),
            S3Bucket = (ArchiveS3Bucket ?? "").Trim(),
            AccessKey = (ArchiveAccessKey ?? "").Trim(),
            SecretKey = ArchiveSecretKey ?? "",
        };
        return archive.Folder.Length > 0 || archive.S3Url.Length > 0 ? new() { archive } : new();
    }

    public static RequestForm From(MigrationRequestDto r) => new()
    {
        Source = r.IsFileImport ? FileSourceValue : r.Source,
        Target = r.IsFileExport ? FileSourceValue : r.Target,
        ExportName = r.FileTarget.Name,
        ExportChats = r.FileTarget.Chats,
        ExportJournal = r.FileTarget.Journal,
        ExportFileArchive = r.FileTarget.FileArchive,
        ImportMainFile = r.FileSource.MainFile,
        ImportChatsFile = r.FileSource.ChatsFile,
        ImportJournalFile = r.FileSource.JournalFile,
        ImportFileArchive = r.FileSource.FileArchive,
        ArchiveFolder = r.FileSource.Archives.FirstOrDefault()?.Folder,
        ArchiveS3Url = r.FileSource.Archives.FirstOrDefault()?.S3Url,
        ArchiveS3Bucket = r.FileSource.Archives.FirstOrDefault()?.S3Bucket,
        ArchiveAccessKey = r.FileSource.Archives.FirstOrDefault()?.AccessKey,
        Mode = r.Mode,
        CopyServerSettings = r.CopyServerSettings,
        Tree = r.Options.Tree,
        TreeAll = r.Options.TreeAll,
        TreeIds = string.Join("\n", r.Options.TreeIds),
        Users = r.Options.Users,
        ClientModules = r.Options.ClientModules,
        WebModules = r.Options.WebModules,
        ServerModules = r.Options.ServerModules,
        Automations = r.Options.Automations,
        Reports = r.Options.Reports,
        FileArchive = r.Options.FileArchive,
    };
}
