using System.Text.Json.Serialization;
using PilotMigration.Config;
using PilotMigration.Objects;

namespace PilotMigration.Engine;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MigrationSourceKind
{
    /// <summary>Источник - контур: прод, тест или стейдж.</summary>
    Contour,

    /// <summary>Источник - присланная файловая база Pilot (.dbp) из каталога импорта.</summary>
    FileDatabase,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MigrationTargetKind
{
    /// <summary>Приёмник - контур: тест или стейдж.</summary>
    Contour,

    /// <summary>Приёмник - выгрузка в файлы (.dbp + тела файлов) в каталоге выгрузок.</summary>
    FileDatabase,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MigrationMode
{
    /// <summary>Приёмник полностью заменяется копией источника.</summary>
    Full,

    /// <summary>Переносится только отмеченное.</summary>
    Selective,
}

/// <summary>Что переносить. Одинаково для ручного запуска и расписания.</summary>
public sealed class MigrationRequest
{
    public string Source { get; set; } = "prod";
    public string Target { get; set; } = "test";

    /// <summary>Откуда берутся данные: контур или присланная файловая база.</summary>
    public MigrationSourceKind SourceKind { get; set; } = MigrationSourceKind.Contour;

    /// <summary>Заполняется при <see cref="MigrationSourceKind.FileDatabase"/>.</summary>
    public FileSourceOptions FileSource { get; set; } = new();

    /// <summary>Куда переносятся данные: контур или выгрузка в файлы.</summary>
    public MigrationTargetKind TargetKind { get; set; } = MigrationTargetKind.Contour;

    /// <summary>Заполняется при <see cref="MigrationTargetKind.FileDatabase"/>.</summary>
    public FileTargetOptions FileTarget { get; set; } = new();

    public MigrationMode Mode { get; set; } = MigrationMode.Full;
    public SelectiveOptions Options { get; set; } = new();

    /// <summary>
    /// Скопировать в приёмник параметры поведения сервера Pilot (settings.xml, применяются при перезапуске контейнера).
    /// Логирование и отладка свои у каждого контура и не копируются никогда. См. <see cref="Pilot.ServerSettingsCopy"/>.
    /// </summary>
    public bool CopyServerSettings { get; set; }

    /// <summary>Файловая база переносится только целиком: выборочный перенос работает по SQL источника-PostgreSQL.</summary>
    [JsonIgnore]
    public bool IsFileImport => SourceKind == MigrationSourceKind.FileDatabase;

    /// <summary>Выгрузка всегда целиком: источник не меняется, база не останавливается.</summary>
    [JsonIgnore]
    public bool IsFileExport => TargetKind == MigrationTargetKind.FileDatabase;

    public MigrationRequest Normalize()
    {
        Source = (Source ?? "").Trim().ToLowerInvariant();
        Target = (Target ?? "").Trim().ToLowerInvariant();
        FileSource ??= new FileSourceOptions();
        FileSource.Normalize();
        if (IsFileImport)
        {
            Source = FileSourceKey;
            Mode = MigrationMode.Full;
        }
        FileTarget ??= new FileTargetOptions();
        if (IsFileExport)
        {
            FileTarget.Normalize(Source);
            Target = FileTargetKey;
            Mode = MigrationMode.Full;
            CopyServerSettings = false;
        }
        Options ??= new SelectiveOptions();
        Options.TreeIds = (Options.TreeIds ?? new()).Select(s => s.Trim()).Where(s => s.Length > 0).Distinct().ToList();
        return this;
    }

    /// <summary>Значение <see cref="Source"/> для файлового источника: он не контур, но попадает в журналы и расписания.</summary>
    public const string FileSourceKey = "file";

    /// <summary>Значение <see cref="Target"/> для выгрузки в файлы.</summary>
    public const string FileTargetKey = "file";

    /// <summary>
    /// Копия без паролей - для журналов запусков и для API: ключ S3 присланного архива приходит в запросе,
    /// но в <c>data/runs</c> и наружу попадать не должен.
    /// </summary>
    public MigrationRequest WithoutSecrets()
    {
        var copy = System.Text.Json.JsonSerializer.Deserialize<MigrationRequest>(
            System.Text.Json.JsonSerializer.Serialize(this))!;
        foreach (var archive in copy.FileSource.Archives.Where(a => a.SecretKey.Length > 0))
            archive.SecretKey = MigrationConfig.SecretMask;
        return copy;
    }

    public string Describe() =>
        DescribeData() + (CopyServerSettings ? " + настройки сервера" : "");

    private string DescribeData()
    {
        if (IsFileExport)
        {
            var exported = new List<string> { $"выгрузка в файлы «{FileTarget.Name}»" };
            if (FileTarget.Chats) exported.Add("чаты");
            if (FileTarget.Journal) exported.Add("журнал действий");
            if (FileTarget.FileArchive) exported.Add("файлы");
            return string.Join(", ", exported);
        }
        if (IsFileImport)
        {
            var imported = new List<string> { "импорт файловой базы" };
            if (FileSource.MainFile.Length > 0)
                imported.Add($"«{FileSource.MainFile}»");
            if (FileSource.HasChats) imported.Add("чаты");
            if (FileSource.HasJournal) imported.Add("журнал действий");
            if (FileSource.FileArchive) imported.Add("файлы");
            return string.Join(", ", imported);
        }
        if (Mode == MigrationMode.Full)
            return "полная копия";
        var o = Options;
        var parts = new List<string>();
        if (o.Tree)
            parts.Add(o.TreeAll ? "дерево объектов целиком" : $"объекты по Id ({o.TreeIds.Count})");
        if (o.Users) parts.Add("пользователи");
        if (o.ClientModules) parts.Add("клиентские модули");
        if (o.WebModules) parts.Add("веб-модули");
        if (o.ServerModules) parts.Add("серверные модули");
        if (o.Automations) parts.Add("автоматизации");
        if (o.Reports) parts.Add("отчёты");
        if (o.FileArchive) parts.Add("файлы");
        return "выборочно: " + (parts.Count > 0 ? string.Join(", ", parts) : "ничего не выбрано");
    }
}

public sealed class SelectiveOptions
{
    /// <summary>Дерево объектов (IDataObject).</summary>
    public bool Tree { get; set; }

    /// <summary>true - всё дерево; false - только <see cref="TreeIds"/> со всеми родителями и потомками.</summary>
    public bool TreeAll { get; set; } = true;

    public List<string> TreeIds { get; set; } = new();

    public bool Users { get; set; }
    public bool ClientModules { get; set; }
    public bool WebModules { get; set; }
    public bool ServerModules { get; set; }
    public bool Automations { get; set; }
    public bool Reports { get; set; }

    /// <summary>Копировать тела файлов переносимых объектов в архив приёмника (если архив не общий).</summary>
    public bool FileArchive { get; set; } = true;

    [JsonIgnore]
    public IReadOnlyList<ObjectCategory> Categories
    {
        get
        {
            var list = new List<ObjectCategory>();
            if (Reports) list.Add(ObjectCategory.Reports);
            if (ClientModules) list.Add(ObjectCategory.ClientModules);
            if (WebModules) list.Add(ObjectCategory.WebModules);
            if (ServerModules) list.Add(ObjectCategory.ServerModules);
            return list;
        }
    }

    /// <summary>Нужна ли работа с таблицами базы (остановка приёмника).</summary>
    [JsonIgnore]
    public bool NeedsDatabase => Tree || Users || Categories.Count > 0;

    [JsonIgnore]
    public bool HasObjects => Tree || Categories.Count > 0;

    [JsonIgnore]
    public bool IsEmpty => !NeedsDatabase && !Automations;
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RunStatus
{
    Queued,
    Running,
    Succeeded,
    Failed,
    Blocked,
    Canceled,
}

public sealed record RunLogEntry(DateTimeOffset Time, string Level, string Text);

/// <summary>Один запуск миграции (ручной или по расписанию).</summary>
public sealed class MigrationRun
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public MigrationRequest Request { get; set; } = new();
    public string RequestedBy { get; set; } = "";

    /// <summary>«вручную» или «расписание: имя».</summary>
    public string Trigger { get; set; } = "вручную";

    public Guid? ScheduleId { get; set; }
    public RunStatus Status { get; set; } = RunStatus.Queued;
    public string? Phase { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public string? Error { get; set; }
    public List<CheckItem> Checks { get; set; } = new();
    public List<RunLogEntry> Log { get; set; } = new();
    public Dictionary<string, long> Stats { get; set; } = new();

    [JsonIgnore]
    public bool IsFinished => Status is RunStatus.Succeeded or RunStatus.Failed or RunStatus.Blocked or RunStatus.Canceled;

    public MigrationRun Snapshot()
    {
        lock (this)
        {
            var copy = (MigrationRun)MemberwiseClone();
            copy.Request = Request.WithoutSecrets();
            copy.Checks = Checks.ToList();
            copy.Log = Log.ToList();
            copy.Stats = new Dictionary<string, long>(Stats);
            return copy;
        }
    }
}

/// <summary>Итог проверки без переноса.</summary>
public sealed class MigrationPlan
{
    public required MigrationRequest Request { get; init; }
    public List<CheckItem> Checks { get; init; } = new();
    public bool CanRun => Checks.All(c => c.Level != CheckLevel.Blocker);

    /// <summary>Строки «что будет сделано».</summary>
    public List<string> Summary { get; init; } = new();

    /// <summary>Имя базы приёмника - его вводят для подтверждения полной копии.</summary>
    public string? TargetDatabase { get; init; }
}
