using System.Security.Claims;
using ProAdmin.Infrastructure;
using ProAdmin.Pilot;
using ProAdmin.Sessions;
using ProAdmin.Settings;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace ProAdmin.Pages;

public class PilotConnectionModel : PageModel
{
    private const string LastServerCookie = "pwa.server";

    private readonly PilotGateway _pilot;
    private readonly SessionVault _vault;
    private readonly SettingsStore _settings;
    private readonly AuthFailureLimiter _limiter;
    private readonly ILogger<PilotConnectionModel> _logger;
    private readonly string _setupKey;

    public PilotConnectionModel(PilotGateway pilot, SessionVault vault, SettingsStore settings,
        AuthFailureLimiter limiter, ILogger<PilotConnectionModel> logger,
        Microsoft.Extensions.Options.IOptions<AppOptions> options)
    {
        _setupKey = options.Value.SetupKey;
        GitHubTypesMode = options.Value.GitHubTypes.IsConfigured;
        _limiter = limiter;
        _logger = logger;
        _pilot = pilot;
        _vault = vault;
        _settings = settings;
    }

    [BindProperty] public string ServerUrl { get; set; } = "";
    [BindProperty] public string Login { get; set; } = "";
    [BindProperty] public string Password { get; set; } = "";
    [BindProperty] public string? SetupKey { get; set; }
    [BindProperty(SupportsGet = true)] public string? ReturnUrl { get; set; }

    public string? Error { get; set; }

    /// <summary>Серверы контуров из настроек (только с адресом).</summary>
    public IReadOnlyList<EnvironmentSettings> Servers { get; private set; } = Array.Empty<EnvironmentSettings>();

    /// <summary>Результат проверки Pilot по контурам.</summary>
    public IReadOnlyList<EnvironmentAccess>? Access { get; private set; }

    private UserSession? Session { get; set; }

    public bool GitHubTypesMode { get; }

    public bool UseServers => Servers.Count > 0;

    /// <summary>Ключ установки задан: первая настройка (вход с адресом сервера вручную) возможна.</summary>
    public bool SetupKeyConfigured => _setupKey.Length > 0;

    /// <summary>Почему вход с адресом сервера вручную не разрешён; null - ключ установки верный.</summary>
    public static string? SetupKeyError(string? configured, string? given) =>
        string.IsNullOrEmpty(configured)
            ? "Первая настройка закрыта: задайте ключ установки (ADMIN_SETUP_KEY в .env, параметр App__SetupKey) и перезапустите админку."
            : ProAdmin.Modules.CiAuth.KeyMatches(given, configured) ? null : "Неверный ключ установки.";

    public IActionResult OnGet()
    {
        Load();
        if (RequireConnection() is { } redirect)
            return redirect;
        if (GitHubTypesMode && Session is not null)
            return RedirectToPage("/Types/GitHub");
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
        if (RequireConnection() is { } redirect)
            return redirect;
        if (GitHubTypesMode && Session is not null)
            return RedirectToPage("/Types/GitHub");
        ServerUrl = ServerUrl?.Trim() ?? "";
        Login = Login?.Trim() ?? "";

        if (Login.Length == 0 || string.IsNullOrEmpty(Password) || (!UseServers && ServerUrl.Length == 0))
        {
            Error = UseServers ? "Заполните логин и пароль." : "Заполните адрес сервера, логин и пароль.";
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
                // Для введённого вручную адреса не раскрываем сетевые подробности.
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

        var session = Session;
        if (session is null)
            session = await SignInAsync(pilotUrl, credentials, access);
        else
            _vault.SetPilotConnection(session, pilotUrl, credentials, access);
        _logger.LogInformation("Подключение к Pilot: логин {Login}, адрес {Address}, серверы: {Servers}", Safe(Login), Address,
            session.SucceededServers.Length > 0 ? session.SucceededServers : pilotUrl);
        TempData["Flash"] = session.LoginSummary();
        if (GitHubTypesMode)
            return RedirectToPage("/Types/GitHub");
        return Url.IsLocalUrl(ReturnUrl) ? LocalRedirect(ReturnUrl!) : RedirectToPage("/Index");
    }

    private IActionResult? RequireConnection() =>
        GitHubTypesMode || Session?.IsGitLabConnected(_settings.Current) == true
            ? null
            : RedirectToPage("/Login", new { returnUrl = "/PilotConnection" });

    private void Load()
    {
        var settings = _settings.Current;
        var servers = settings.Environments.Where(e => e.HasServer).ToList();
        // Стейдж - не контур автоматизаций, а отдельный адрес (для модулей): логин проверяем и на нём.
        if (settings.StagePilotUrl.Length > 0)
            servers.Insert(Math.Min(1, servers.Count), new EnvironmentSettings { Key = "stage", Title = "Стейдж", PilotUrl = settings.StagePilotUrl });
        Servers = servers;
        Session = _vault.Get(User.FindFirst(SessionVault.SidClaim)?.Value);
        Access = Session?.LoginAccess;
    }

    private async Task<UserSession> SignInAsync(string pilotUrl, PilotLogin credentials, IReadOnlyList<EnvironmentAccess> access)
    {
        var session = _vault.Create(pilotUrl, credentials, access);
        var identity = new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.Name, credentials.Login),
            new Claim(SessionVault.SidClaim, session.Id),
        }, CookieAuthenticationDefaults.AuthenticationScheme);

        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
        return session;
    }
}
