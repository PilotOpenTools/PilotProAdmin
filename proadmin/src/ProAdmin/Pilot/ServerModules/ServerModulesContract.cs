// Копия контракта из «Серверные расширения/ServerHost/ServerHost/Contract/IServerModulesApi.cs».
// Транспорт Pilot сопоставляет интерфейс по имени (IServerModulesApi), а JSON - по именам полей:
// при изменении контракта правьте оба файла (сверяет RepoCopiesTests).
namespace ProAdmin.Pilot.ServerModules;

/// <summary>
/// API центрального модуля для веб-админки. Клиент получает его через HttpPilotClient.GetCustomApi&lt;IServerModulesApi&gt;()
/// после входа в базу; транспорт Pilot сопоставляет интерфейс по ИМЕНИ, поэтому в админке объявлен такой же интерфейс.
/// Запросы и ответы - JSON (protobuf транспорта не любит null и значения по умолчанию).
/// Доступ - только администраторам базы.
/// </summary>
public interface IServerModulesApi
{
    /// <summary>Запрос <see cref="HostRequest"/> / ответ <see cref="HostResponse"/>.</summary>
    Task<string> InvokeAsync(string requestJson);

    /// <summary>Очередной кусок архива для загрузки, начатой операцией beginUpload.</summary>
    Task<string> UploadChunkAsync(string uploadId, byte[] data);
}

public static class HostOps
{
    public const string State = "state";
    public const string BeginUpload = "beginUpload";
    public const string Validate = "validate";
    public const string Install = "install";
    public const string SetEnabled = "setEnabled";
    public const string Reload = "reload";
    public const string Rollback = "rollback";
    public const string Remove = "remove";
}

public sealed class HostRequest
{
    public string Op { get; set; } = "";
    public string? Name { get; set; }
    public string? UploadId { get; set; }
    public string? FileName { get; set; }
    public long Size { get; set; }
    public bool Enabled { get; set; }
    /// <summary>Выбросить из архива сборки, которые уже есть на сервере (иначе это ошибка проверки).</summary>
    public bool StripServerAssemblies { get; set; }
    public string? Reason { get; set; }
    /// <summary>Кто выполняет операцию в админке (для журнала; пользователь базы - отдельно).</summary>
    public string? Actor { get; set; }
}

public sealed class HostResponse
{
    public bool Ok { get; set; }
    public string? Error { get; set; }
    public string? Message { get; set; }
    public string? UploadId { get; set; }
    public HostState? State { get; set; }
    public ValidationReport? Report { get; set; }
    public ModuleState? Module { get; set; }
    /// <summary>
    /// Появились новые имена активностей: движок автоматизаций привяжет их после изменения метаданных базы
    /// (админка делает это сама). Обновление уже известных активностей работает сразу.
    /// </summary>
    public bool RebindRequired { get; set; }
}

public sealed class HostState
{
    public string HostVersion { get; set; } = "";
    public DateTime StartedAt { get; set; }
    public string StorageDir { get; set; } = "";
    public bool StoragePersistent { get; set; }
    public int SlotsTotal { get; set; }
    public int SlotsUsed { get; set; }
    public List<ModuleState> Modules { get; set; } = new();
    /// <summary>Серверные расширения, загруженные Pilot штатно (кроме самого хоста).</summary>
    public List<AssemblyRef> NativeExtensions { get; set; } = new();
    public List<JournalEntry> Journal { get; set; } = new();
}

public sealed class ModuleState
{
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public bool Enabled { get; set; }
    public bool Loaded { get; set; }
    public string? Error { get; set; }
    public List<ActivityState> Activities { get; set; } = new();
    public List<string> Files { get; set; } = new();
    /// <summary>dll в папке модуля с AssemblyVersion (пусто - native).</summary>
    public List<AssemblyRef> Assemblies { get; set; } = new();
    public DateTime InstalledAt { get; set; }
    public string? InstalledBy { get; set; }
    public string? Reason { get; set; }
    public string Sha256 { get; set; } = "";
    public string? PreviousVersion { get; set; }
    public DateTime? LoadedAt { get; set; }
}

public sealed class ActivityState
{
    public string Name { get; set; } = "";
    public string TypeName { get; set; } = "";
    public long Calls { get; set; }
    public long Errors { get; set; }
    public string? LastError { get; set; }
    public DateTime? LastCallAt { get; set; }
}

public sealed class AssemblyRef
{
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
}

public sealed class JournalEntry
{
    public DateTime At { get; set; }
    public string Level { get; set; } = "info";
    public string Text { get; set; } = "";
}

public static class FileStatus
{
    /// <summary>Своя сборка модуля - будет загружена в его контекст.</summary>
    public const string Private = "private";
    /// <summary>Такая же сборка (имя и версия) уже есть на сервере - лишняя.</summary>
    public const string Duplicate = "duplicate";
    /// <summary>Сборка с этим именем есть на сервере в другой версии - модуль получит серверную.</summary>
    public const string VersionMismatch = "versionMismatch";
    /// <summary>Главная сборка модуля (*.servext.dll).</summary>
    public const string Main = "main";
    public const string Native = "native";
    public const string Other = "other";
}

public sealed class ArchiveFile
{
    public string Path { get; set; } = "";
    public long Size { get; set; }
    public string Status { get; set; } = FileStatus.Other;
    public string? AssemblyName { get; set; }
    public string? Version { get; set; }
    public string? ServerVersion { get; set; }
    /// <summary>Будет выброшен при установке (лишняя сборка).</summary>
    public bool Stripped { get; set; }
}

public sealed class ValidationReport
{
    public string UploadId { get; set; } = "";
    public string FileName { get; set; } = "";
    public string ModuleName { get; set; } = "";
    public string Version { get; set; } = "";
    public string? CurrentVersion { get; set; }
    public string Sha256 { get; set; } = "";
    public bool CanInstall { get; set; }
    public List<string> Errors { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
    public List<ArchiveFile> Files { get; set; } = new();
    public List<string> Activities { get; set; } = new();
}
