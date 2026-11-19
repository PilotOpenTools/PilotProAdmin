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
    private readonly ITimeLimitedDataProtector _grant =
        protection.CreateProtector("ProAdmin.FirstRun").ToTimeLimitedDataProtector();

    [BindProperty] public string? SetupKey { get; set; }
    [BindProperty] public string? GitLabUrl { get; set; }
    [BindProperty] public string? Token { get; set; }
    [BindProperty] public SetupInput Input { get; set; } = new();
    [BindProperty] public List<DeployAccountInput> Deploy { get; set; } = [];
    public string? GitLabUser { get; private set; }
    public string? Error { get; private set; }

    private bool Enabled => options.Value.FirstRunSetup && !options.Value.GitHubTypes.IsConfigured &&
                            !store.Current.FirstRunCompleted;
    private string? Address => HttpContext.Connection.RemoteIpAddress?.ToString();
    private CookieOptions SetupCookie => new()
    {
        HttpOnly = true, Secure = Request.IsHttps, SameSite = SameSiteMode.Strict,
        Path = Request.PathBase.Add("/Setup").Value,
    };

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
        if (PilotConnectionModel.SetupKeyError(options.Value.SetupKey, SetupKey) is { } keyError)
        {
            limiter.Fail(AuthFailureLimiter.LoginScope, Address);
            Error = keyError;
            return Page();
        }
        if (!TryWebUrl(GitLabUrl, out var url) || new Uri(url).AbsolutePath != "/" ||
            Token?.Trim() is not { Length: > 0 and <= 1024 } token)
        {
            Error = "Укажите HTTP(S)-адрес GitLab и Personal Access Token с правом api.";
            return Page();
        }
        try
        {
            var user = await gitLab.Create(new GitLabToken { AccessToken = token, BaseUrl = url }).GetCurrentUserAsync(ct);
            var cookie = SetupCookie;
            cookie.MaxAge = GrantLifetime;
            Response.Cookies.Append(Cookie, _grant.Protect(JsonSerializer.Serialize(new Grant(url, token, user.Username)),
                GrantLifetime), cookie);
            return RedirectToPage();
        }
        catch (GitLabApiException ex)
        {
            Error = ex.Message;
            return Page();
        }
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
        if (await accounts.ApplyAsync(current, Deploy, pilot, ct) is { } error)
            return Invalid(error);

        current.UpdatedBy = grant.User;
        current.UpdatedAt = DateTimeOffset.UtcNow;
        var token = new GitLabToken { AccessToken = grant.Token, BaseUrl = grant.Url };
        GitLabUser user;
        try { user = await signIn.VerifyAccessAsync(token, current, ct); }
        catch (GitLabApiException ex) { return Invalid(ex.Message); }
        if (!store.CompleteFirstRun(current))
            return RedirectToPage("/Login");
        Response.Cookies.Delete(Cookie, SetupCookie);
        await signIn.SignInAsync(HttpContext, token, user);
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

    private sealed record Grant(string Url, string Token, string User);

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
