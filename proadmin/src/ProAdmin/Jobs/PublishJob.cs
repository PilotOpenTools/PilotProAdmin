using System.Text.Json.Serialization;
using ProAdmin.GitLab;
using ProAdmin.Pilot;
using ProAdmin.Settings;

namespace ProAdmin.Jobs;

public enum JobStatus
{
    Running,
    WaitingPipeline,
    Succeeded,
    Failed,
}

public sealed class JobLogEntry
{
    public DateTimeOffset Time { get; set; }
    public string Level { get; set; } = "info";
    public string Text { get; set; } = "";
    public string? Url { get; set; }
    public string? Details { get; set; }
}

/// <summary>Операция «отправить изменения автоматизации» и её журнал.</summary>
public sealed class PublishJob
{
    [JsonIgnore]
    private readonly object _sync = new();

    public Guid Id { get; set; } = Guid.NewGuid();
    public bool IsDelete { get; set; }

    /// <summary>Название операции для журнала (переименование, удаление, копирование…). null - отправка изменений.</summary>
    public string? Operation { get; set; }
    public string Env { get; set; } = "";
    public string EnvTitle { get; set; } = "";
    public string Branch { get; set; } = "";
    public string AutomationName { get; set; } = "";
    public string CommitMessage { get; set; } = "";

    /// <summary>Что изменилось - пункты, записанные в CHANGELOG.md.</summary>
    public List<string> ChangeNotes { get; set; } = new();
    public string PilotUser { get; set; } = "";
    public string? GitLabUser { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? FinishedAt { get; set; }
    public JobStatus Status { get; set; } = JobStatus.Running;
    public string? Error { get; set; }
    public string? CommitUrl { get; set; }
    public string? MergeRequestUrl { get; set; }
    public string? PipelineUrl { get; set; }
    public List<JobLogEntry> Log { get; set; } = new();

    /// <summary>Путь проекта для поиска операции независимо от отображаемого имени модуля.</summary>
    internal string? ModuleProjectPath { get; init; }
    /// <summary>Ветка, которую эта операция сама выложит на стейдж после сборки в CI.</summary>
    internal string? ManagedStageRelease { get; init; }

    [JsonIgnore]
    public bool IsFinished => Status is JobStatus.Succeeded or JobStatus.Failed;

    public void Add(string level, string text, string? url = null, string? details = null)
    {
        lock (_sync)
            Log.Add(new JobLogEntry { Time = DateTimeOffset.UtcNow, Level = level, Text = text, Url = url, Details = details });
    }

    public void Update(Action<PublishJob> change)
    {
        lock (_sync)
            change(this);
    }

    /// <summary>Согласованная копия для отображения (фоновая задача продолжает писать в оригинал).</summary>
    public PublishJob Snapshot()
    {
        lock (_sync)
        {
            var copy = (PublishJob)MemberwiseClone();
            copy.ChangeNotes = ChangeNotes.ToList();
            copy.Log = Log.Select(e => new JobLogEntry { Time = e.Time, Level = e.Level, Text = e.Text, Url = e.Url, Details = e.Details }).ToList();
            return copy;
        }
    }
}

/// <summary>Что отправляет пользователь из редактора.</summary>
public sealed class PublishRequest
{
    public required string Env { get; init; }
    public required string Name { get; init; }
    public bool Delete { get; init; }
    public string Triggers { get; init; } = "";
    public string CSharpCode { get; init; } = "";
    public bool IsPaused { get; init; }
    public required string CommitMessage { get; init; }

    /// <summary>Что изменилось - пункты для CHANGELOG.md (пусто - берётся сообщение коммита).</summary>
    public IReadOnlyList<string> ChangeNotes { get; init; } = Array.Empty<string>();

    /// <summary>Задача (TASK-123) - в заголовок раздела CHANGELOG.md при попадании в main.</summary>
    public string? TaskReference { get; init; }

    /// <summary>Хеш автоматизации на сервере при открытии редактора (null - не проверять).</summary>
    public string? BasePilotHash { get; init; }

    /// <summary>blob_id файлов в ветке при открытии редактора ("" - файла не было, null - не проверять).</summary>
    public string? BaseGitJsonBlob { get; init; }
    public string? BaseGitCsBlob { get; init; }

    /// <summary>Ветка задачи / hotfix, из которой взято содержимое: после успешной отправки она удаляется (работа влита).</summary>
    public string? SourceBranch { get; init; }

    /// <summary>Ветка, из которой переносится содержимое в main (release/…, hotfix/…, dev): по ней ищутся связанные изменения типов.</summary>
    public string? ContentRef { get; init; }
}

/// <summary>Всё, что нужно фоновой операции от сессии пользователя (сессия может закончиться раньше).</summary>
public sealed class PublishContext
{
    public required PilotLogin Pilot { get; init; }

    /// <summary>Учётка стейджа: задана - после вливания в dev то же записывается на стейдж (режим «тест и стейдж вместе»).</summary>
    public PilotLogin? StagePilot { get; init; }

    /// <summary>Пользователь базы для ServerHost (список действий серверных модулей); null - <see cref="Pilot"/>.</summary>
    public PilotLogin? ModulesLogin { get; init; }
    public required GitLabToken GitLabToken { get; init; }
    public required AdminSettings Settings { get; init; }
    public required string ProjectPath { get; init; }

    /// <summary>ci_config_path проекта (пусто - .gitlab-ci.yml).</summary>
    public string? CiConfigPath { get; init; }

    /// <summary>Репозиторий типов: связанные изменения уходят на стейдж и прод вместе с автоматизацией; null - без типов.</summary>
    public Types.TypesFollowDeps? Types { get; init; }
}
