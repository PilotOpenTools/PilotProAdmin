using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using ProAdmin.GitLab;
using ProAdmin.Infrastructure;
using ProAdmin.Pilot;
using ProAdmin.Settings;

namespace ProAdmin.Pages;

public sealed class SetupModel(SettingsStore store, IOptions<AppOptions> options, AuthFailureLimiter limiter,
    IDataProtectionProvider protection, GitLabClientFactory gitLab, GitLabSignIn signIn, DeployAccounts accounts,
    PilotGateway pilot) : PageModel
{
    private const string Cookie = "pwa.setup";
    private static readonly TimeSpan GrantLifetime = TimeSpan.FromMinutes(20);
    private readonly ITimeLimitedDataProtector _grant = GrantProtector(protection);

    [BindProperty] public string? SetupKey { get; set; }
    [BindProperty] public string? GitLabUrl { get; set; }
    [BindProperty] public string? Token { get; set; }
    [BindProperty] public SetupInput Input { get; set; } = new();
    [BindProperty] public List<DeployAccountInput> Deploy { get; set; } = [];
    public string? GitLabUser { get; private set; }
    public string? Error { get; private set; }
    /// <summary>Вход через GitLab в браузере: в админке задано OAuth-приложение GitLab.</summary>
    public bool OAuthEnabled => signIn.OAuthEnabled;

    private bool Enabled => IsOpen(options.Value, store);
    private string? Address => HttpContext.Connection.RemoteIpAddress?.ToString();
    private CookieOptions SetupCookie => SetupCookieFor(Request);

    internal static bool IsOpen(AppOptions options, SettingsStore store) =>
        options.FirstRunSetup && !options.GitHubTypes.IsConfigured && !store.Current.FirstRunCompleted;

    private static ITimeLimitedDataProtector GrantProtector(IDataProtectionProvider protection) =>
        protection.CreateProtector("ProAdmin.FirstRun").ToTimeLimitedDataProtector();

    // Lax, не Strict: после входа в браузере пропуск ставит /GitLab/Callback и перенаправляет на /Setup. Эта цепочка начата
    // на сайте GitLab (другой хост, например localhost:8080 против 127.0.0.1:5300), и Strict-cookie браузер на /Setup не отправит:
    // мастер снова покажет первый шаг. Формы мастера защищает antiforgery.
    private static CookieOptions SetupCookieFor(HttpRequest request) => new()
    {
        HttpOnly = true, Secure = request.IsHttps, SameSite = SameSiteMode.Lax,
        Path = request.PathBase.Add("/Setup").Value,
    };

    /// <summary>Пропуск во второй шаг мастера: GitLab подключён (токеном или входом в браузере).</summary>
    internal static void IssueGrant(HttpContext context, IDataProtectionProvider protection, GitLabToken token, string user)
    {
        var cookie = SetupCookieFor(context.Request);
        cookie.MaxAge = GrantLifetime;
        var grant = new Grant(token.BaseUrl, token.AccessToken, user, token.RefreshToken, token.ExpiresAt, token.RedirectUri, token.IsOAuth);
        context.Response.Cookies.Append(Cookie, GrantProtector(protection).Protect(JsonSerializer.Serialize(grant), GrantLifetime), cookie);
    }

    public IActionResult OnGet()
    {
        if (!Enabled)
            return RedirectToPage("/Login");
        var current = store.Current;
        if (ReadGrant() is { } grant)
        {
            GitLabUser = grant.User;
            Input = new SetupInput
            {
                Test = current.Find("test")?.PilotUrl, Stage = current.StagePilotUrl, Prod = current.Find("prod")?.PilotUrl,
                Grafana = current.GrafanaUrl,
                Automation = MoveGroup(current.GitLabGroupUrl, grant.Url) ?? grant.Url + "/automation",
                Reports = MoveGroup(current.ReportsGitLabGroupUrl, grant.Url),
                Modules = MoveGroup(current.ModulesGitLabGroupUrl, grant.Url),
                ServerModules = MoveGroup(current.ServerModulesGitLabGroupUrl, grant.Url),
                Types = MoveGroup(current.TypesGitLabProjectUrl, grant.Url),
            };
            Deploy = accounts.Prefill(current);
        }
        else if (GitLabLocation.TryParse(current.GitLabGroupUrl, out var location, out _))
            GitLabUrl = location!.BaseUrl;
        return Page();
    }

    public async Task<IActionResult> OnPostConnectAsync(CancellationToken ct)
    {
        if (!Enabled)
            return RedirectToPage("/Login");
        if (limiter.IsBlocked(AuthFailureLimiter.LoginScope, Address))
            return StatusCode(429);
        if (CheckKeyAndUrl() is not { } url)
            return Page();
        if (Token?.Trim() is not { Length: > 0 and <= 1024 } token)
        {
            Error = "Вставьте Personal Access Token с правом api или войдите через GitLab в браузере.";
            return Page();
        }
        try
        {
            var gitLabToken = new GitLabToken { AccessToken = token, BaseUrl = url };
            var user = await gitLab.Create(gitLabToken).GetCurrentUserAsync(ct);
            IssueGrant(HttpContext, protection, gitLabToken, user.Username);
            return RedirectToPage();
        }
        catch (GitLabApiException ex)
        {
            Error = ex.Message;
            return Page();
        }
    }

    /// <summary>Вход через GitLab в браузере (OAuth): после возврата из GitLab мастер продолжается, как после токена.</summary>
    public async Task<IActionResult> OnPostBrowserAsync(CancellationToken ct)
    {
        // Enter в поле токена отправляет форму первой кнопкой (вход в браузере): токен важнее.
        if (!string.IsNullOrWhiteSpace(Token))
            return await OnPostConnectAsync(ct);
        if (!Enabled)
            return RedirectToPage("/Login");
        if (limiter.IsBlocked(AuthFailureLimiter.LoginScope, Address))
            return StatusCode(429);
        if (CheckKeyAndUrl() is not { } url)
            return Page();
        if (!OAuthEnabled)
        {
            Error = "Вход через GitLab в браузере не настроен: задайте GITLAB_OAUTH_CLIENT_ID и GITLAB_OAUTH_CLIENT_SECRET в .env админки или войдите с токеном.";
            return Page();
        }
        return Redirect(await signIn.StartOAuthAsync(HttpContext, url, "/Setup", ct, setup: true));
    }

    /// <summary>Ключ установки и адрес GitLab; при ошибке заполняет Error и возвращает null.</summary>
    private string? CheckKeyAndUrl()
    {
        if (PilotConnectionModel.SetupKeyError(options.Value.SetupKey, SetupKey) is { } keyError)
        {
            limiter.Fail(AuthFailureLimiter.LoginScope, Address);
            Error = keyError;
            return null;
        }
        if (!TryWebUrl(GitLabUrl, out var url) || new Uri(url).AbsolutePath != "/")
        {
            Error = "Укажите HTTP(S)-адрес GitLab без пути, например http://localhost:8080.";
            return null;
        }
        return url;
    }

    public async Task<IActionResult> OnPostFinishAsync(CancellationToken ct)
    {
        if (!Enabled || ReadGrant() is not { } grant)
            return RedirectToPage();
        GitLabUser = grant.User;
        var current = store.Current;
        foreach (var (contour, value) in new[] { ("test", Input.Test), ("stage", Input.Stage), ("prod", Input.Prod) })
        {
            var url = value?.Trim() ?? "";
            if (url.Length > 0 && !PilotAddress.TryParse(url, out _, out _))
                return Invalid($"Проверьте адрес Pilot для контура {contour}.");
            if (contour == "stage")
                current.StagePilotUrl = url;
            else
                current.Find(contour)!.PilotUrl = url;
        }
        if (!string.IsNullOrWhiteSpace(Input.Grafana) && !TryWebUrl(Input.Grafana, out _))
            return Invalid("Проверьте адрес Grafana.");
        current.GrafanaUrl = Input.Grafana?.Trim() ?? "";
        var automation = Input.Automation?.Trim() ?? "";
        if (automation.Length == 0)
            automation = grant.Url + "/automation";
        var paths = new[] { automation, Input.Reports, Input.Modules, Input.ServerModules, Input.Types };
        foreach (var path in paths.Where(path => !string.IsNullOrWhiteSpace(path)))
            if (!GitLabLocation.TryParse(path, out var location, out _) ||
                !string.Equals(location!.BaseUrl, grant.Url, StringComparison.OrdinalIgnoreCase))
                return Invalid("Укажите полные адреса групп и проектов на подключённом сервере GitLab.");
        current.GitLabGroupUrl = automation;
        current.ReportsGitLabGroupUrl = Input.Reports ?? "";
        current.ModulesGitLabGroupUrl = Input.Modules ?? "";
        current.ServerModulesGitLabGroupUrl = Input.ServerModules ?? "";
        current.TypesGitLabProjectUrl = Input.Types ?? "";
        var notes = new List<string>();
        if (await accounts.ApplyAsync(current, Deploy, pilot, ct, notes) is { } error)
            return Invalid(error);

        current.UpdatedBy = grant.User;
        current.UpdatedAt = DateTimeOffset.UtcNow;
        var token = grant.ToToken();
        GitLabUser user;
        try { user = await signIn.VerifyAccessAsync(token, current, ct); }
        catch (GitLabApiException ex) { return Invalid(ex.Message); }
        if (!store.CompleteFirstRun(current))
            return RedirectToPage("/Login");
        Response.Cookies.Delete(Cookie, SetupCookie);
        await signIn.SignInAsync(HttpContext, token, user);
        if (notes.Count > 0)
            TempData["Flash"] = string.Join(" ", notes);
        return RedirectToPage("/Index");
    }

    public IActionResult OnPostRestart()
    {
        Response.Cookies.Delete(Cookie, SetupCookie);
        return RedirectToPage();
    }

    private IActionResult Invalid(string error)
    {
        Error = error;
        ModelState.Clear();
        foreach (var account in Deploy)
            account.Password = null;
        return Page();
    }

    private Grant? ReadGrant()
    {
        if (!Request.Cookies.TryGetValue(Cookie, out var cookie))
            return null;
        try { return JsonSerializer.Deserialize<Grant>(_grant.Unprotect(cookie)); }
        catch (CryptographicException) { return null; }
    }

    public static bool TryWebUrl(string? input, out string url)
    {
        url = "";
        if (!Uri.TryCreate(input?.Trim(), UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") || uri.Host.Length == 0 ||
            uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
            return false;
        url = uri.AbsoluteUri.TrimEnd('/');
        return true;
    }

    private static string? MoveGroup(string url, string newBase) =>
        GitLabLocation.TryParse(url, out var location, out _) ? newBase + "/" + location!.Path : null;

    // Для входа в браузере хранится и refresh-токен: иначе сеанс после мастера оборвётся, когда истечёт OAuth-токен.
    private sealed record Grant(string Url, string Token, string User, string? RefreshToken = null,
        DateTimeOffset? ExpiresAt = null, string? RedirectUri = null, bool OAuth = false)
    {
        public GitLabToken ToToken() => new()
        {
            AccessToken = Token, BaseUrl = Url, RefreshToken = RefreshToken, ExpiresAt = ExpiresAt,
            RedirectUri = RedirectUri, IsOAuth = OAuth,
        };
    }

    public sealed class SetupInput
    {
        public string? Test { get; set; }
        public string? Stage { get; set; }
        public string? Prod { get; set; }
        public string? Grafana { get; set; }
        public string? Automation { get; set; }
        public string? Reports { get; set; }
        public string? Modules { get; set; }
        public string? ServerModules { get; set; }
        public string? Types { get; set; }
    }
}
