using System.Collections.Concurrent;
using System.Security.Cryptography;
using ProAdmin.GitLab;
using ProAdmin.Pilot;
using ProAdmin.Settings;
using Microsoft.AspNetCore.DataProtection;

namespace ProAdmin.Sessions;

/// <summary>
/// Серверное хранилище сессий. В cookie лежит только идентификатор сессии,
/// пароли Pilot и токены GitLab остаются в памяти процесса (пароль - зашифрованным).
/// После перезапуска контейнера нужно войти заново.
/// </summary>
public sealed class SessionVault
{
    public const string SidClaim = "pwa:sid";

    private readonly ConcurrentDictionary<string, UserSession> _sessions = new();
    private readonly IDataProtector _protector;

    public SessionVault(IDataProtectionProvider dataProtection) =>
        _protector = dataProtection.CreateProtector("ProAdmin.PilotPassword");

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
        if (sid is not null)
            _sessions.TryRemove(sid, out _);
    }

    public void RemoveIdle(TimeSpan idle)
    {
        var border = DateTimeOffset.UtcNow - idle;
        foreach (var (sid, session) in _sessions)
            if (session.LastSeen < border)
                _sessions.TryRemove(sid, out _);
    }

    public void SetEnvironmentCredential(UserSession session, string env, PilotLogin login) =>
        session.EnvironmentCredentials[env] = new StoredCredential(login.Login, _protector.Protect(login.Password));

    public const string ReportsPrefix = "reports:";

    /// <summary>
    /// Учётка для работы с отчётами в контуре (вход в базу пользователем): своя для отчётов, если вводилась,
    /// иначе учётка контура, иначе та, с которой выполнен вход.
    /// </summary>
    public PilotLogin GetReportLogin(UserSession session, string env) =>
        session.EnvironmentCredentials.ContainsKey(ReportsPrefix + env)
            ? GetPilotLogin(session, ReportsPrefix + env)
            : GetPilotLogin(session, env);

    public const string ModulesPrefix = "modules:";

    /// <summary>
    /// Учётка для модулей в контуре (пользователь базы с правами на папку расширений): своя для модулей, если вводилась,
    /// иначе учётка контура, иначе та, с которой выполнен вход. Стейдж - отдельный контур «stage».
    /// </summary>
    public PilotLogin GetModuleLogin(UserSession session, string env) =>
        session.EnvironmentCredentials.ContainsKey(ModulesPrefix + env)
            ? GetPilotLogin(session, ModulesPrefix + env)
            : GetPilotLogin(session, env);

    /// <summary>Учётка для контура: своя, если вводилась, иначе та, с которой выполнен вход.</summary>
    public PilotLogin GetPilotLogin(UserSession session, string env)
    {
        var stored = session.EnvironmentCredentials.TryGetValue(env, out var c) ? c : session.LoginCredential;
        if (stored is null)
            throw new PilotCredentialsRequiredException();
        return new PilotLogin(stored.Login, _protector.Unprotect(stored.ProtectedPassword));
    }
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
    /// Вход выполнен администратором Pilot контура «Прод». Если прод в настройках не задан, защищать нечем: хватает входа
    /// хотя бы на один настроенный сервер, а до первой настройки (серверов нет вовсе) - любого входа.
    /// </summary>
    public bool IsProdAdmin(AdminSettings settings) =>
        PilotConnection is not null && (settings.Find(AdminSettings.Prod) is { HasServer: true }
            ? LoginAccess.Any(a => a.Ok && a.Key == AdminSettings.Prod)
            : !settings.HasAnyServer && settings.StagePilotUrl.Length == 0 || LoginAccess.Any(a => a.Ok));

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

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(15));
        while (await timer.WaitForNextTickAsync(stoppingToken))
            _vault.RemoveIdle(TimeSpan.FromHours(12));
    }
}
