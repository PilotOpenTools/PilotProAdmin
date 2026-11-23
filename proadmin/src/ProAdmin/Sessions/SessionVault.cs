using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Options;
using ProAdmin.Infrastructure;
using ProAdmin.GitLab;
using ProAdmin.Pilot;
using ProAdmin.Settings;
using Microsoft.AspNetCore.DataProtection;

namespace ProAdmin.Sessions;

/// <summary>
/// Серверное хранилище сессий. В cookie лежит только идентификатор сессии, токены GitLab - на сервере.
/// В админку входят через GitLab, а с Pilot она работает служебными учётными записями контуров из общих настроек
/// («Настройки» / «Служебные учётные записи»). Вход в Pilot остался только в режиме типов из GitHub (там нет GitLab).
/// Сессии GitLab переживают перезапуск: они сохраняются в DataDirectory/sessions.dat, зашифрованными ключами
/// DataProtection (лежат там же). Сессии со входом в Pilot (режим GitHub, в них пароль) живут только в памяти.
/// </summary>
public sealed class SessionVault
{
    public const string SidClaim = "pwa:sid";

    private readonly ConcurrentDictionary<string, UserSession> _sessions = new();
    private readonly IDataProtector _protector;
    private readonly SettingsStore _settings;
    private readonly DeployAccounts _accounts;
    private readonly IDataProtector _store;
    private readonly string? _path;
    private readonly ILogger<SessionVault>? _logger;
    private readonly Lock _saveLock = new();

    /// <summary>Сессия без активности дольше этого удаляется (и при загрузке после перезапуска не возвращается).</summary>
    public static readonly TimeSpan IdleLimit = TimeSpan.FromHours(12);

    public SessionVault(IDataProtectionProvider dataProtection, SettingsStore settings, DeployAccounts accounts,
        IOptions<AppOptions>? options = null, ILogger<SessionVault>? logger = null)
    {
        _protector = dataProtection.CreateProtector("ProAdmin.PilotPassword");
        _store = dataProtection.CreateProtector("ProAdmin.Sessions");
        _settings = settings;
        _accounts = accounts;
        _logger = logger;
        if (options is not null)
        {
            _path = Path.Combine(Path.GetFullPath(options.Value.DataDirectory), "sessions.dat");
            Load();
        }
    }

    private sealed record SavedSession(string Id, GitLabToken Token, GitLabUser User, DateTimeOffset LastSeen);

    private void Load()
    {
        if (_path is null || !File.Exists(_path))
            return;
        try
        {
            var saved = JsonSerializer.Deserialize<List<SavedSession>>(_store.Unprotect(File.ReadAllText(_path))) ?? [];
            var border = DateTimeOffset.UtcNow - IdleLimit;
            foreach (var s in saved.Where(s => s.LastSeen >= border))
                _sessions[s.Id] = new UserSession(s.Id) { GitLabToken = s.Token, GitLabUser = s.User, LastSeen = s.LastSeen };
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or IOException)
        {
            // Ключи сменились или файл повреждён: все просто войдут заново.
            _logger?.LogWarning("Сохранённые сессии не прочитаны ({Error}): потребуется войти заново.", ex.Message);
        }
    }

    /// <summary>Записать сессии GitLab на диск (при входе, выходе, раз в минуту и при остановке: токены обновляются на месте).</summary>
    public void Save()
    {
        if (_path is null)
            return;
        var saved = _sessions.Values
            .Where(s => s.UsesServiceAccounts && s.GitLabToken is not null && s.GitLabUser is not null)
            .Select(s => new SavedSession(s.Id, s.GitLabToken!, s.GitLabUser!, s.LastSeen))
            .ToList();
        lock (_saveLock)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                var tmp = _path + ".tmp";
                File.WriteAllText(tmp, _store.Protect(JsonSerializer.Serialize(saved)));
                File.Move(tmp, _path, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger?.LogWarning("Сессии не сохранены ({Error}): после перезапуска потребуется войти заново.", ex.Message);
            }
        }
    }

    public UserSession Create(string pilotUrl, PilotLogin login, IReadOnlyList<EnvironmentAccess>? access = null)
    {
        var session = new UserSession(NewId());
        SetPilotConnection(session, pilotUrl, login, access ?? Array.Empty<EnvironmentAccess>());
        _sessions[session.Id] = session;
        return session;
    }

    public UserSession Create(GitLabToken token, GitLabUser user)
    {
        var session = new UserSession(NewId()) { GitLabToken = token, GitLabUser = user };
        _sessions[session.Id] = session;
        Save();
        return session;
    }

    private static string NewId() => Convert.ToHexString(RandomNumberGenerator.GetBytes(24));

    public void SetPilotConnection(UserSession session, string url, PilotLogin login, IReadOnlyList<EnvironmentAccess> access) =>
        session.PilotConnection = new(url, new StoredCredential(login.Login, _protector.Protect(login.Password)), access);

    public UserSession? Get(string? sid) =>
        sid is not null && _sessions.TryGetValue(sid, out var s) ? s : null;

    public bool Touch(string sid)
    {
        if (!_sessions.TryGetValue(sid, out var session))
            return false;
        session.LastSeen = DateTimeOffset.UtcNow;
        return true;
    }

    public void Remove(string? sid)
    {
        if (sid is not null && _sessions.TryRemove(sid, out _))
            Save();
    }

    public void RemoveIdle(TimeSpan idle)
    {
        var border = DateTimeOffset.UtcNow - idle;
        foreach (var (sid, session) in _sessions)
            if (session.LastSeen < border)
                _sessions.TryRemove(sid, out _);
        Save();
    }

    public void SetEnvironmentCredential(UserSession session, string env, PilotLogin login) =>
        session.EnvironmentCredentials[env] = new StoredCredential(login.Login, _protector.Protect(login.Password));

    public const string ReportsPrefix = "reports:";

    /// <summary>Учётка для отчётов в контуре: при входе через GitLab - служебная учётка контура из настроек.</summary>
    public PilotLogin GetReportLogin(UserSession session, string env) =>
        session.EnvironmentCredentials.ContainsKey(ReportsPrefix + env)
            ? GetPilotLogin(session, ReportsPrefix + env)
            : GetPilotLogin(session, env);

    public const string ModulesPrefix = "modules:";

    /// <summary>Учётка для модулей в контуре: при входе через GitLab - служебная учётка контура из настроек. Стейдж - контур «stage».</summary>
    public PilotLogin GetModuleLogin(UserSession session, string env) =>
        session.EnvironmentCredentials.ContainsKey(ModulesPrefix + env)
            ? GetPilotLogin(session, ModulesPrefix + env)
            : GetPilotLogin(session, env);

    /// <summary>
    /// Учётка для контура. Вход через GitLab: служебная учётная запись контура из общих настроек (нет её -
    /// <see cref="PilotCredentialsRequiredException"/>, страница отправит в «Настройки»). Вход в Pilot (режим GitHub):
    /// своя учётка контура, если вводилась, иначе та, с которой выполнен вход.
    /// </summary>
    public PilotLogin GetPilotLogin(UserSession session, string env)
    {
        if (session.UsesServiceAccounts)
            return _accounts.Get(_settings.Current, ContourOf(env)) ?? throw new PilotCredentialsRequiredException();
        var stored = session.EnvironmentCredentials.TryGetValue(env, out var c) ? c : session.LoginCredential;
        if (stored is null)
            throw new PilotCredentialsRequiredException();
        return new PilotLogin(stored.Login, _protector.Unprotect(stored.ProtectedPassword));
    }

    private static string ContourOf(string env) =>
        env.StartsWith(ReportsPrefix, StringComparison.Ordinal) ? env[ReportsPrefix.Length..] :
        env.StartsWith(ModulesPrefix, StringComparison.Ordinal) ? env[ModulesPrefix.Length..] : env;
}

public sealed record StoredCredential(string Login, string ProtectedPassword);
internal sealed record PilotConnection(string Url, StoredCredential Credential, IReadOnlyList<EnvironmentAccess> Access);

/// <summary>Удалось ли войти на сервер контура; Error - причина отказа.</summary>
public sealed record EnvironmentAccess(string Key, string Title, string PilotUrl, string? Error)
{
    public bool Ok => Error is null;
}

public sealed class UserSession(string id)
{
    public UserSession(string id, string pilotUrl, StoredCredential credential) : this(id) =>
        PilotConnection = new(pilotUrl, credential, Array.Empty<EnvironmentAccess>());

    public string Id { get; } = id;
    internal PilotConnection? PilotConnection { get; set; }
    public string PilotUrl => PilotConnection?.Url ?? "";
    public StoredCredential? LoginCredential => PilotConnection?.Credential;
    public string PilotLogin => LoginCredential?.Login ?? "";

    /// <summary>Сессия без входа в Pilot (вход через GitLab): с Pilot работаем служебными учётками контуров из настроек.</summary>
    public bool UsesServiceAccounts => PilotConnection is null;

    /// <summary>Кто действует - для журналов и операций: пользователь GitLab, иначе логин Pilot.</summary>
    public string Actor => GitLabUser?.Username is { Length: > 0 } u ? u : PilotLogin;
    public DateTimeOffset LastSeen { get; set; } = DateTimeOffset.UtcNow;

    public ConcurrentDictionary<string, StoredCredential> EnvironmentCredentials { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Результат проверки входа на серверы контуров при логине (ключ - контур).</summary>
    public IReadOnlyList<EnvironmentAccess> LoginAccess
    {
        get => PilotConnection?.Access ?? Array.Empty<EnvironmentAccess>();
        set
        {
            if (PilotConnection is { } connection)
                PilotConnection = connection with { Access = value };
        }
    }

    /// <summary>«Тест, Прод» - серверы, на которые удалось войти при логине; пусто - вход по адресу вручную.</summary>
    public string SucceededServers => string.Join(", ", LoginAccess.Where(a => a.Ok).Select(a => a.Title));

    /// <summary>Итог входа для сообщения после подключения GitLab.</summary>
    public string LoginSummary() =>
        PilotConnection is null ? $"Вход выполнен: GitLab - @{GitLabUser?.Username}." :
        $"Подключено: Pilot - {(SucceededServers.Length > 0 ? SucceededServers : PilotUrl)} ({PilotLogin})" +
        (GitLabUser is { } gu ? $", GitLab - @{gu.Username}." : ".") +
        (LoginAccess.Any(a => !a.Ok) ? $" Не удалось войти: {string.Join(", ", LoginAccess.Where(a => !a.Ok).Select(a => a.Title))}." : "");

    public GitLabToken? GitLabToken { get; set; }
    public GitLabUser? GitLabUser { get; set; }

    /// <summary>Уровни доступа к группам GitLab (ключ - «id пользователя:путь группы»), чтобы не спрашивать GitLab на каждой странице.</summary>
    public ConcurrentDictionary<string, (GitLabGroupAccess Access, DateTimeOffset At)> GroupAccessCache { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Администратор админки: меняет общие настройки, миграцию, настройки серверов, видит все операции. Пока это
    /// администратор GitLab из общих настроек (отдельные права - позже). В режиме GitHub (вход в Pilot) - вход
    /// администратором Pilot контура «Прод»; если прод не задан, хватает входа на любой настроенный сервер.
    /// </summary>
    public bool IsAdmin(AdminSettings settings) =>
        UsesServiceAccounts ? IsGitLabAdmin(settings) : IsProdAdmin(settings);

    private bool IsProdAdmin(AdminSettings settings) =>
        PilotConnection is not null && (settings.Find(AdminSettings.Prod) is { HasServer: true }
            ? LoginAccess.Any(a => a.Ok && a.Key == AdminSettings.Prod)
            : !settings.HasAnyServer && settings.StagePilotUrl.Length == 0 || LoginAccess.Any(a => a.Ok));

    /// <summary>Вход через GitLab администратором того сервера GitLab, что указан в общих настройках.</summary>
    public bool IsGitLabAdmin(AdminSettings settings) => GitLabUser?.IsAdmin == true && IsGitLabConnected(settings);

    /// <summary>Подключён ли GitLab именно к тому серверу, что указан в общих настройках.</summary>
    public bool IsGitLabConnected(AdminSettings settings) =>
        GitLabToken is not null &&
        GitLabLocation.TryParse(settings.GitLabGroupUrl, out var location, out _) &&
        string.Equals(location!.BaseUrl, GitLabToken.BaseUrl, StringComparison.OrdinalIgnoreCase);
}

public sealed class SessionCleanupService : BackgroundService
{
    private readonly SessionVault _vault;

    public SessionCleanupService(SessionVault vault) => _vault = vault;

    /// <summary>Раз в минуту: убрать простаивающие сессии и сохранить остальные (обновлённые токены, время активности).</summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
                _vault.RemoveIdle(SessionVault.IdleLimit);
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>Остановка контейнера: сохранить сессии, чтобы после запуска не входить заново.</summary>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        _vault.Save();
    }
}
