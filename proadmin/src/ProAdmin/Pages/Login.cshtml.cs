using System.Security.Claims;
using System.Security.Cryptography;
using ProAdmin.GitLab;
using ProAdmin.Infrastructure;
using ProAdmin.Pilot;
using ProAdmin.Sessions;
using ProAdmin.Settings;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace ProAdmin.Pages;

/// <summary>
/// Единый вход: логин/пароль Pilot + GitLab на одной карточке.
/// Если в общих настройках заданы серверы контуров - вход в Pilot проверяется сразу на всех серверах.
/// После проверки Pilot сразу уходит в GitLab (OAuth) или подключает введённый Personal Access Token.
/// Пока GitLab не подключён, страницы с GitLab (и любые изменения в нём) не работают: проверка в AppPageModel и страницах.
/// Адрес Pilot вручную вводится только при первом запуске, пока серверы не настроены: настроенные серверы выбрать
/// самому нельзя, иначе вход проверял бы любой Pilot-совместимый сервер по указанному адресу.
/// После 10 неудачных входов с одного адреса за 5 минут - ответ 429 на 5 минут.
/// </summary>
public class LoginModel : PageModel
{
    private const string LastServerCookie = "pwa.server";

    private readonly PilotGateway _pilot;
    private readonly SessionVault _vault;
    private readonly SettingsStore _settings;
    private readonly GitLabAuthService _gitLabAuth;
    private readonly GitLabClientFactory _gitLabFactory;
    private readonly AuthFailureLimiter _limiter;
    private readonly ILogger<LoginModel> _logger;
    private readonly string _setupKey;

    public LoginModel(PilotGateway pilot, SessionVault vault, SettingsStore settings,
        GitLabAuthService gitLabAuth, GitLabClientFactory gitLabFactory, AuthFailureLimiter limiter, ILogger<LoginModel> logger,
        Microsoft.Extensions.Options.IOptions<AppOptions> options)
    {
        _setupKey = options.Value.SetupKey;
        GitHubTypesMode = options.Value.GitHubTypes.IsConfigured;
        _limiter = limiter;
        _logger = logger;
        _pilot = pilot;
        _vault = vault;
        _settings = settings;
        _gitLabAuth = gitLabAuth;
        _gitLabFactory = gitLabFactory;
    }

    [BindProperty] public string ServerUrl { get; set; } = "";
    [BindProperty] public string Login { get; set; } = "";
    [BindProperty] public string Password { get; set; } = "";
    [BindProperty] public string? AccessToken { get; set; }
    [BindProperty] public string? SetupKey { get; set; }
    [BindProperty(SupportsGet = true)] public string? ReturnUrl { get; set; }

    public string? Error { get; set; }

    /// <summary>Серверы контуров из настроек (только с адресом).</summary>
    public IReadOnlyList<EnvironmentSettings> Servers { get; private set; } = Array.Empty<EnvironmentSettings>();

    /// <summary>Результат проверки Pilot по контурам.</summary>
    public IReadOnlyList<EnvironmentAccess>? Access { get; private set; }

    /// <summary>В Pilot уже вошли, осталось подключить GitLab.</summary>
    public UserSession? PilotSession { get; private set; }

    /// <summary>Сервер GitLab из настроек; null - группа не задана.</summary>
    public string? GitLabBaseUrl { get; private set; }

    public bool GitLabOAuthEnabled => _gitLabAuth.OAuthEnabled;
    public bool GitHubTypesMode { get; }

    public bool UseServers => Servers.Count > 0;

    /// <summary>Ключ установки задан: первая настройка (вход с адресом сервера вручную) возможна.</summary>
    public bool SetupKeyConfigured => _setupKey.Length > 0;

    /// <summary>Почему вход с адресом сервера вручную не разрешён; null - ключ установки верный.</summary>
    public static string? SetupKeyError(string? configured, string? given) =>
        string.IsNullOrEmpty(configured)
            ? "Первая настройка закрыта: задайте ключ установки (ADMIN_SETUP_KEY в .env, параметр App__SetupKey) и перезапустите админку."
            : ProAdmin.Modules.CiAuth.KeyMatches(given, configured) ? null : "Неверный ключ установки.";

    public string ContinueUrl =>
        Url.IsLocalUrl(ReturnUrl) && ReturnUrl != "/" && !ReturnUrl!.StartsWith("/Login", StringComparison.OrdinalIgnoreCase)
            ? ReturnUrl
            : "/";

    public IActionResult OnGet()
    {
        Load();
        if (GitHubTypesMode && PilotSession is not null)
            return RedirectToPage("/Types/GitHub");
        if (PilotSession is not null && PilotSession.IsGitLabConnected(_settings.Current))
            return LocalRedirect(ContinueUrl);
        ServerUrl = Request.Cookies[LastServerCookie] ?? "";
        return Page();
    }

    private string? Address => HttpContext.Connection.RemoteIpAddress?.ToString();

    /// <summary>Значение из запроса - в журнал без переводов строк.</summary>
    private static string Safe(string? value) => new string((value ?? "").Where(c => !char.IsControl(c)).Take(64).ToArray());

    private IActionResult? Blocked() =>
        _limiter.IsBlocked(AuthFailureLimiter.LoginScope, Address) ? StatusCode(StatusCodes.Status429TooManyRequests) : null;

    /// <summary>Неудача входа: в журнал и в счётчик перебора по адресу.</summary>
    private void Failed(string reason)
    {
        _logger.LogWarning("Неудачный вход: логин {Login}, адрес {Address}: {Reason}", Safe(Login), Address, reason);
        if (_limiter.Fail(AuthFailureLimiter.LoginScope, Address))
            _logger.LogWarning("Вход: адрес {Address} заблокирован на 5 минут после 10 неудач", Address);
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        if (Blocked() is { } blocked)
            return blocked;
        Load();
        ServerUrl = ServerUrl.Trim();
        Login = Login.Trim();

        if (Login.Length == 0 || Password.Length == 0 || (!UseServers && ServerUrl.Length == 0))
        {
            Error = UseServers ? "Заполните логин и пароль." : "Заполните адрес сервера, логин и пароль.";
            return Page();
        }
        if (!GitHubTypesMode && GitLabBaseUrl is null)
        {
            Error = "В настройках не задана группа GitLab - войти в GitLab невозможно.";
            return Page();
        }
        if (!GitHubTypesMode && !GitLabOAuthEnabled && string.IsNullOrWhiteSpace(AccessToken))
        {
            Error = "Вход через GitLab (OAuth) не настроен - вставьте Personal Access Token.";
            return Page();
        }

        var credentials = new PilotLogin(Login, Password);
        string pilotUrl;
        IReadOnlyList<EnvironmentAccess> access;

        if (UseServers)
        {
            access = await Task.WhenAll(Servers.Select(async env =>
            {
                try
                {
                    await _pilot.VerifyLoginAsync(env.PilotUrl, credentials, ct);
                    return new EnvironmentAccess(env.Key, env.Title, env.PilotUrl, null);
                }
                catch (PilotGatewayException ex)
                {
                    return new EnvironmentAccess(env.Key, env.Title, env.PilotUrl, ex.Message);
                }
            }));
            Access = access;

            var first = access.FirstOrDefault(a => a.Ok);
            if (first is null)
            {
                Failed("не принят ни одним сервером Pilot");
                Error = "Не удалось войти ни на один сервер Pilot с этими данными.";
                return Page();
            }
            pilotUrl = first.PilotUrl;
        }
        else
        {
            if (SetupKeyError(_setupKey, SetupKey) is { } keyError)
            {
                Failed("ключ установки");
                Error = keyError;
                return Page();
            }
            try
            {
                await _pilot.VerifyLoginAsync(ServerUrl, credentials, ct);
            }
            catch (PilotGatewayException ex)
            {
                // Адрес вводит анонимный пользователь: причину (сеть, порт, ответ чужого сервера) не раскрываем.
                Failed(ex.Message);
                Error = "Не удалось войти на сервер Pilot: проверьте адрес, логин и пароль.";
                return Page();
            }
            pilotUrl = ServerUrl;
            access = Array.Empty<EnvironmentAccess>();
            Response.Cookies.Append(LastServerCookie, ServerUrl, new CookieOptions
            {
                HttpOnly = true,
                SameSite = SameSiteMode.Lax,
                Expires = DateTimeOffset.UtcNow.AddYears(1),
            });
        }

        // Токен проверяется до входа: с неверным токеном в приложение не пускаем.
        (GitLabToken Token, GitLabUser User)? pat = null;
        if (!GitHubTypesMode && !string.IsNullOrWhiteSpace(AccessToken))
        {
            pat = await VerifyTokenAsync(AccessToken, ct);
            if (pat is null)
                return Page();
        }

        var session = await SignInAsync(pilotUrl, credentials, access);
        _logger.LogInformation("Вход в Pilot: логин {Login}, адрес {Address}, серверы: {Servers}", Safe(Login), Address,
            session.SucceededServers.Length > 0 ? session.SucceededServers : pilotUrl);
        if (GitHubTypesMode)
            return RedirectToPage("/Types/GitHub");
        return pat is { } p ? Connected(session, p.Token, p.User) : await StartOAuthAsync(session, ct);
    }

    /// <summary>В Pilot уже вошли, GitLab - через OAuth (повтор после отказа или ошибки).</summary>
    public async Task<IActionResult> OnPostGitLabAsync(CancellationToken ct)
    {
        if (GitHubTypesMode)
            return NotFound();
        Load();
        if (PilotSession is null)
            return RedirectToPage();
        if (GitLabBaseUrl is null || !GitLabOAuthEnabled)
        {
            Error = "Вход через GitLab (OAuth) недоступен - вставьте Personal Access Token.";
            return Page();
        }
        return await StartOAuthAsync(PilotSession, ct);
    }

    /// <summary>В Pilot уже вошли, GitLab - по Personal Access Token.</summary>
    public async Task<IActionResult> OnPostTokenAsync(CancellationToken ct)
    {
        if (GitHubTypesMode)
            return NotFound();
        if (Blocked() is { } blocked)
            return blocked;
        Load();
        if (PilotSession is null)
            return RedirectToPage();
        if (GitLabBaseUrl is null)
        {
            Error = "В настройках не задана группа GitLab.";
            return Page();
        }
        if (string.IsNullOrWhiteSpace(AccessToken))
        {
            Error = "Вставьте Personal Access Token.";
            return Page();
        }

        var pat = await VerifyTokenAsync(AccessToken, ct);
        return pat is { } p ? Connected(PilotSession, p.Token, p.User) : Page();
    }

    private void Load()
    {
        var settings = _settings.Current;
        var servers = settings.Environments.Where(e => e.HasServer).ToList();
        // Стейдж - не контур автоматизаций, а отдельный адрес (для модулей): логин проверяем и на нём.
        if (settings.StagePilotUrl.Length > 0)
            servers.Insert(Math.Min(1, servers.Count), new EnvironmentSettings { Key = "stage", Title = "Стейдж", PilotUrl = settings.StagePilotUrl });
        Servers = servers;
        GitLabBaseUrl = GitLabLocation.TryParse(settings.GitLabGroupUrl, out var location, out _) ? location!.BaseUrl : null;
        PilotSession = User.Identity?.IsAuthenticated == true ? _vault.Get(User.FindFirst(SessionVault.SidClaim)?.Value) : null;
        if (PilotSession is not null)
            Access = PilotSession.LoginAccess;
    }

    private async Task<(GitLabToken Token, GitLabUser User)?> VerifyTokenAsync(string accessToken, CancellationToken ct)
    {
        var token = new GitLabToken { AccessToken = accessToken.Trim(), BaseUrl = GitLabBaseUrl! };
        try
        {
            return (token, await _gitLabFactory.Create(token).GetCurrentUserAsync(ct));
        }
        catch (GitLabApiException ex)
        {
            Failed("GitLab не принял токен");
            Error = $"GitLab: {ex.Message}";
            return null;
        }
    }

    private IActionResult Connected(UserSession session, GitLabToken token, GitLabUser user)
    {
        session.GitLabToken = token;
        session.GitLabUser = user;
        _logger.LogInformation("Вход в GitLab по токену: @{GitLabUser}, Pilot {Login}, адрес {Address}", Safe(user.Username), Safe(session.PilotLogin), Address);
        TempData["Flash"] = session.LoginSummary();
        return LocalRedirect(ContinueUrl);
    }

    private async Task<IActionResult> StartOAuthAsync(UserSession session, CancellationToken ct)
    {
        var redirectUri = _gitLabAuth.ResolveRedirectUri(Request);
        var state = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        session.PendingOAuth = (state, redirectUri, GitLabBaseUrl!, ContinueUrl);
        var publicBase = await _gitLabAuth.ResolvePublicBaseUrlAsync(GitLabBaseUrl!, ct);
        return Redirect(_gitLabAuth.BuildAuthorizeUrl(publicBase, redirectUri, state));
    }

    private async Task<UserSession> SignInAsync(string pilotUrl, PilotLogin credentials, IReadOnlyList<EnvironmentAccess> access)
    {
        var session = _vault.Create(pilotUrl, credentials);
        session.LoginAccess = access;
        var identity = new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.Name, credentials.Login),
            new Claim(SessionVault.SidClaim, session.Id),
        }, CookieAuthenticationDefaults.AuthenticationScheme);

        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
        PilotSession = session;
        return session;
    }
}
