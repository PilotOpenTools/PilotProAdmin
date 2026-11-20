using System.Text.Json;

namespace ProAdmin.Settings;

/// <summary>Общие настройки, одинаковые для всех пользователей (data/settings.json).</summary>
public sealed class AdminSettings
{
    public const string Test = "test";
    public const string Prod = "prod";

    public List<EnvironmentSettings> Environments { get; set; } = new();
    [Microsoft.AspNetCore.Mvc.ModelBinding.BindNever]
    public bool FirstRunCompleted { get; set; }
    [Microsoft.AspNetCore.Mvc.ModelBinding.BindNever]
    public Dictionary<string, StoredDeployLogin> DeployLogins { get; set; } = new();
    public string GrafanaUrl { get; set; } = "";

    /// <summary>Шаблоны «Новый модуль»: источники NuGet, версии SDK, клиент для отладки.</summary>
    public ModuleTemplateSettings ModuleTemplates { get; set; } = new();

    /// <summary>
    /// Группа GitLab с автоматизациями, например http://localhost:8080/automation.
    /// Каждая автоматизация - отдельный проект (репозиторий) в этой группе или её подгруппах-папках.
    /// </summary>
    public string GitLabGroupUrl { get; set; } = "http://localhost:8080/automation";

    /// <summary>
    /// Группа GitLab с отчётами, например http://localhost:8080/reports. Каждый отчёт - отдельный проект
    /// в этой группе или её подгруппах-папках; имя проекта = имя отчёта в Pilot. Пусто - вкладка «Отчёты» выключена.
    /// Сервер GitLab должен совпадать с сервером группы автоматизаций (вход в GitLab один).
    /// </summary>
    public string ReportsGitLabGroupUrl { get; set; } = "";

    /// <summary>
    /// Код лицензии для входа в базу Pilot при работе с отчётами (отчёты - объекты базы, админский API их не видит).
    /// </summary>
    public int ReportsLicenseType { get; set; } = 100;

    /// <summary>
    /// Группа GitLab с клиентскими модулями Pilot, например http://localhost:8080/modules. Каждый модуль - проект
    /// в группе или подгруппе. Ветки: dev - тестовый сервер, release/X.Y.Z - стейдж, main - прод. Пусто - вкладка «Модули» выключена.
    /// </summary>
    public string ModulesGitLabGroupUrl { get; set; } = "";

    /// <summary>
    /// Группа GitLab с серверными модулями (.servext), например http://localhost:8080/server-modules. Нужна только
    /// для таблицы «Активные работы» на вкладке «Серверные модули». Пусто - таблица не показывается.
    /// </summary>
    public string ServerModulesGitLabGroupUrl { get; set; } = "";

    /// <summary>
    /// Репозиторий GitLab с типами, атрибутами, состояниями и группами состояний (конфигурация базы как код),
    /// например http://localhost:8080/pilot-config/types. Ветки: dev - тест (правки только здесь), release - стейдж,
    /// main - прод; в release и main изменения попадают только переносом коммитов. Пусто - вкладка «Типы» только сравнивает серверы.
    /// </summary>
    public string TypesGitLabProjectUrl { get; set; } = "";

    /// <summary>Сервер Pilot стейджа (http://host:5549/база) - на него выкладывается ветка release/X.Y.Z (только для модулей).</summary>
    public string StagePilotUrl { get; set; } = "";

    /// <summary>Имя job в .gitlab-ci.yml модуля, артефакт которого - zip модуля для загрузки в Pilot.</summary>
    public string ModulesBuildJob { get; set; } = "build_module";

    /// <summary>
    /// «Влить / тест» только после Code Review: MR в dev одобрен (Approve в GitLab) кем-то кроме автора.
    /// Администратор GitLab может влить без ревью, указав причину (пишется в журнал).
    /// Для одного разработчика выключается: одобрить MR некому, а pipeline обязателен и без ревью.
    /// </summary>
    public bool RequireDevReview { get; set; } = true;

    /// <summary>Номер задачи Kaiten в имени ветки и коммита (регулярное выражение).</summary>
    public string TaskPattern { get; set; } = @"KTN-\d+";

    /// <summary>Ссылка на карточку Kaiten: {task} - номер целиком (KTN-1234), {number} - только цифры. Пусто - без ссылок.</summary>
    public string TaskUrlTemplate { get; set; } = "";

    /// <summary>
    /// Режим CI/CD автоматизаций при заданном сервере стейджа: false - как у модулей (тест / «Релиз / стейдж» / прод),
    /// true - вливание на тест сразу записывает то же на стейдж, на прод переносится dev (без release-веток).
    /// </summary>
    public bool AutomationsAutoStage { get; set; }

    /// <summary>
    /// Отчёты - два контура: правка (ветка dev) и прод (main). Где правят отчёты: false - на тестовом сервере,
    /// true - на сервере стейджа (тогда dev / стейдж, тестовый сервер для отчётов не используется).
    /// </summary>
    public bool ReportsEditOnStage { get; set; }

    /// <summary>Требовать «Version: X.Y.Z» в JSON и её повышение при изменении.</summary>
    public bool RequireVersion { get; set; } = true;

    /// <summary>
    /// Аварийный обход: если у репозитория нет CI - отправлять без ожидания pipeline (с предупреждением в журнале).
    /// </summary>
    public bool AllowPublishWithoutCi { get; set; }

    /// <summary>Конфигурация CI автоматизаций (ci_config_path проекта). Пусто - к новым репозиториям не подключается.</summary>
    public string? AutomationsCiConfigPath { get; set; } = "automation.yml@pilot-ci/checks";

    /// <summary>Конфигурация CI отчётов (ci_config_path проекта). Пусто - к новым репозиториям не подключается.</summary>
    public string? ReportsCiConfigPath { get; set; } = "report.yml@pilot-ci/checks";

    /// <summary>Не отправлять, если проверка сценария по справочнику и базе дала предупреждения (ошибки блокируют всегда).</summary>
    public bool BlockPublishOnLintWarnings { get; set; }

    public int PipelineStartTimeoutSeconds { get; set; } = 180;

    public int PipelineTimeoutMinutes { get; set; } = 30;

    public DateTimeOffset? UpdatedAt { get; set; }

    public string? UpdatedBy { get; set; }

    /// <summary>Имя job в .gitlab-ci.yml: подставляется в ключ YAML, needs и текст команд, поэтому только безопасные символы.</summary>
    public static bool IsValidJobName(string? name) =>
        name is { Length: > 0 and <= 64 } && System.Text.RegularExpressions.Regex.IsMatch(name, "^[A-Za-z_][A-Za-z0-9_-]*$");

    /// <summary>Путь конфигурации CI проекта: «файл.yml» или «файл.yml@группа/проект»; пусто допустимо.</summary>
    public static bool IsValidCiConfigPath(string? path) =>
        string.IsNullOrWhiteSpace(path) ||
        System.Text.RegularExpressions.Regex.IsMatch(path.Trim(), @"^[A-Za-z0-9_./-]+(@[A-Za-z0-9_.-]+(/[A-Za-z0-9_.-]+)*)?$");

    public EnvironmentSettings? Find(string? key) =>
        Environments.FirstOrDefault(e => string.Equals(e.Key, key, StringComparison.OrdinalIgnoreCase));

    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasAnyServer => Environments.Any(e => e.HasServer);

    /// <summary>Полный путь группы (например «automation»), если адрес корректен.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? GitLabGroupPath =>
        GitLab.GitLabLocation.TryParse(GitLabGroupUrl, out var location, out _) ? location!.Path : null;

    /// <summary>Полный путь группы отчётов (например «reports»); null - не задана или адрес некорректен.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? ReportsGroupPath =>
        !string.IsNullOrWhiteSpace(ReportsGitLabGroupUrl) && GitLab.GitLabLocation.TryParse(ReportsGitLabGroupUrl, out var location, out _) ? location!.Path : null;

    /// <summary>Полный путь группы модулей (например «modules»); null - не задана или адрес некорректен.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? ModulesGroupPath =>
        !string.IsNullOrWhiteSpace(ModulesGitLabGroupUrl) && GitLab.GitLabLocation.TryParse(ModulesGitLabGroupUrl, out var location, out _) ? location!.Path : null;

    /// <summary>Полный путь группы серверных модулей; null - не задана или адрес некорректен.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? ServerModulesGroupPath =>
        !string.IsNullOrWhiteSpace(ServerModulesGitLabGroupUrl) && GitLab.GitLabLocation.TryParse(ServerModulesGitLabGroupUrl, out var location, out _) ? location!.Path : null;

    /// <summary>Полный путь репозитория типов (например «pilot-config/types»); null - не задан или адрес некорректен.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? TypesProjectPath =>
        !string.IsNullOrWhiteSpace(TypesGitLabProjectUrl) && GitLab.GitLabLocation.TryParse(TypesGitLabProjectUrl, out var location, out _) ? location!.Path : null;

    /// <summary>Гарантирует наличие двух контуров (тест / dev, прод / main) в фиксированном порядке; прочие (бывший стейдж) отбрасываются.</summary>
    public void Normalize()
    {
        DeployLogins ??= new();
        GrafanaUrl = (GrafanaUrl ?? "").Trim();
        (ModuleTemplates ??= new()).Normalize();
        var defaults = new[]
        {
            new EnvironmentSettings { Key = Test, Title = "Тест", Branch = "dev" },
            new EnvironmentSettings { Key = Prod, Title = "Прод", Branch = "main", RequireTaskReference = true },
        };

        var result = new List<EnvironmentSettings>();
        foreach (var def in defaults)
        {
            var existing = Find(def.Key) ?? def;
            existing.Key = def.Key;
            existing.Title = def.Title;
            existing.PilotUrl = (existing.PilotUrl ?? "").Trim();
            existing.Branch = (existing.Branch ?? "").Trim();
            result.Add(existing);
        }
        Environments = result;

        GitLabGroupUrl = (GitLabGroupUrl ?? "").Trim().TrimEnd('/');
        ReportsGitLabGroupUrl = (ReportsGitLabGroupUrl ?? "").Trim().TrimEnd('/');
        ModulesGitLabGroupUrl = (ModulesGitLabGroupUrl ?? "").Trim().TrimEnd('/');
        ServerModulesGitLabGroupUrl = (ServerModulesGitLabGroupUrl ?? "").Trim().TrimEnd('/');
        TypesGitLabProjectUrl = (TypesGitLabProjectUrl ?? "").Trim().TrimEnd('/');
        if (TypesGitLabProjectUrl.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            TypesGitLabProjectUrl = TypesGitLabProjectUrl[..^4];
        StagePilotUrl = (StagePilotUrl ?? "").Trim();
        ModulesBuildJob = string.IsNullOrWhiteSpace(ModulesBuildJob) ? "build_module" : ModulesBuildJob.Trim();
        TaskPattern = string.IsNullOrWhiteSpace(TaskPattern) ? @"KTN-\d+" : TaskPattern.Trim();
        TaskUrlTemplate = (TaskUrlTemplate ?? "").Trim();
        if (ReportsLicenseType <= 0)
            ReportsLicenseType = 100;
        PipelineStartTimeoutSeconds = Math.Clamp(PipelineStartTimeoutSeconds, 15, 3600);
        PipelineTimeoutMinutes = Math.Clamp(PipelineTimeoutMinutes, 1, 24 * 60);
    }

    public AdminSettings Clone() =>
        JsonSerializer.Deserialize<AdminSettings>(JsonSerializer.Serialize(this))!;
}

public sealed class EnvironmentSettings
{
    public string Key { get; set; } = "";
    public string Title { get; set; } = "";

    /// <summary>Адрес сервера Pilot с базой: http://host:5545/database.</summary>
    public string PilotUrl { get; set; } = "";

    /// <summary>Ветка GitLab, которой соответствует сервер. Пусто - контур только для просмотра.</summary>
    public string Branch { get; set; } = "";

    /// <summary>Требовать ссылку на задачу в сообщении коммита (для main).</summary>
    public bool RequireTaskReference { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasServer => !string.IsNullOrWhiteSpace(PilotUrl);

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsEditable => HasServer && !string.IsNullOrWhiteSpace(Branch);
}
