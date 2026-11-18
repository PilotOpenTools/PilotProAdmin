namespace ProAdmin.Infrastructure;

/// <summary>Параметры запуска (appsettings.json / переменные окружения App__*).</summary>
public sealed class AppOptions
{
    public Types.GitHubTypesOptions GitHubTypes { get; set; } = new();

    /// <summary>Каталог для settings.json, истории операций и ключей DataProtection.</summary>
    public string DataDirectory { get; set; } = "data";

    /// <summary>
    /// Хост, на который подменяются localhost / 127.0.0.1 в адресах Pilot и GitLab
    /// при запросах со стороны сервера. В Docker - host.docker.internal.
    /// В браузер уходят исходные адреса.
    /// </summary>
    public string? LocalhostAlias { get; set; }

    /// <summary>
    /// Адрес, по которому админку открывают пользователи (http://host:5300), без завершающего «/». От него строится
    /// redirect_uri OAuth (<c>{PublicUrl}/GitLab/Callback</c>). Пусто - из адреса запроса. Для https cookie всегда Secure.
    /// </summary>
    public string? PublicUrl { get; set; }

    /// <summary>
    /// Доверять X-Forwarded-For/Proto/Host. Включайте, только если админка стоит за собственным обратным прокси:
    /// иначе любой клиент подделает адрес (и обойдёт ограничение частоты неверных входов).
    /// </summary>
    public bool TrustForwardedHeaders { get; set; }

    /// <summary>
    /// Ключ установки. Пока серверы Pilot не настроены, адрес сервера вводится на странице входа; без этого ключа
    /// такой вход закрыт, иначе первый посетитель вошёл бы через свой сервер и получил права на настройки.
    /// </summary>
    public string SetupKey { get; set; } = "";

    public GitLabOAuthOptions GitLab { get; set; } = new();

    /// <summary>Сервис миграции данных между контурами (раздел «Миграция»).</summary>
    public MigrationServiceOptions Migration { get; set; } = new();

    /// <summary>Начальные общие настройки - применяются, пока data/settings.json ещё не создан.</summary>
    public DefaultSettingsOptions Defaults { get; set; } = new();

    /// <summary>Настройки серверов Pilot (раздел «Серверы»).</summary>
    public ServerSettingsOptions ServerSettings { get; set; } = new();

    /// <summary>Выкладка клиентских модулей из GitLab CI (POST /api/ci/modules/deploy).</summary>
    public CiDeployOptions CiDeploy { get; set; } = new();
}

public sealed class CiDeployOptions
{
    /// <summary>
    /// Прежний общий ключ (MODULES_DEPLOY_KEY). Принимается только для контура test и только если не задан Keys:test.
    /// Для stage и prod не принимается никогда.
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>
    /// Ключи выкладки по контурам: Keys:test / stage / prod. Job deploy_* передаёт свой в X-Deploy-Key
    /// (переменные DEPLOY_KEY_TEST / _STAGE / _PROD в GitLab). Нет ключа - выкладка на этот контур выключена.
    /// </summary>
    public Dictionary<string, string?> Keys { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Служебные учётки Pilot по контурам: Logins:test / stage / prod / Login, Password.</summary>
    public Dictionary<string, CiDeployLogin> Logins { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Ключ контура; null - не задан (для test подходит прежний общий ключ, если Keys:test пуст).</summary>
    public string? KeyFor(string env)
    {
        if (Keys.TryGetValue(env, out var key) && !string.IsNullOrWhiteSpace(key))
            return key;
        return env.Equals("test", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(ApiKey) ? ApiKey : null;
    }

    /// <summary>Что задать, чтобы включить выкладку на контур (для ответа 503).</summary>
    public static string KeyHint(string env) =>
        $"задайте App__CiDeploy__Keys__{env} (MODULES_DEPLOY_KEY_{env.ToUpperInvariant()}); прежний общий MODULES_DEPLOY_KEY для этого контура не принимается";

    public ProAdmin.Pilot.PilotLogin? Login(string env) =>
        Logins.TryGetValue(env, out var l) && !string.IsNullOrWhiteSpace(l.Login) ? new(l.Login!, l.Password ?? "") : null;
}

public sealed class CiDeployLogin
{
    public string? Login { get; set; }
    public string? Password { get; set; }
}

public sealed class ServerSettingsOptions
{
    /// <summary>
    /// Каталог с подкаталогами контуров (test, stage, prod) - это &lt;корень данных&gt;/&lt;контур&gt;/server-settings,
    /// смонтированные в контейнер админки. Пусто - раздел «Серверы» показывает, как их подключить.
    /// </summary>
    public string? Root { get; set; }
}

public sealed class DefaultSettingsOptions
{
    public string? TestPilotUrl { get; set; }
    public string? ProdPilotUrl { get; set; }
    public string? GitLabGroupUrl { get; set; }
    public string? ReportsGitLabGroupUrl { get; set; }
    public string? ModulesGitLabGroupUrl { get; set; }
    public string? ServerModulesGitLabGroupUrl { get; set; }
    public string? StagePilotUrl { get; set; }
    public string? TypesGitLabProjectUrl { get; set; }
}

public sealed class GitLabOAuthOptions
{
    /// <summary>Application ID приложения GitLab (Admin / Applications или User Settings / Applications).</summary>
    public string? ClientId { get; set; }

    public string? ClientSecret { get; set; }

    /// <summary>Callback URL. Если пусто - {App:PublicUrl}/GitLab/Callback, а без него - из адреса запроса: {scheme}://{host}/GitLab/Callback.</summary>
    public string? RedirectUri { get; set; }

    public string Scopes { get; set; } = "api";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret);
}

public sealed class MigrationServiceOptions
{
    /// <summary>Адрес сервиса pilot-migration, например http://localhost:5320. Пусто - раздел показывает, как его подключить.</summary>
    public string? Url { get; set; }

    /// <summary>Ключ API - тот же, что MIGRATION_API_KEY у сервиса.</summary>
    public string? ApiKey { get; set; }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Url) && !string.IsNullOrWhiteSpace(ApiKey);
}
