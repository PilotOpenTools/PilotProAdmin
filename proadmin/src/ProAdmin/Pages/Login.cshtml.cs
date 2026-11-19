using ProAdmin.GitLab;
using ProAdmin.Infrastructure;
using ProAdmin.Sessions;
using ProAdmin.Settings;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

namespace ProAdmin.Pages;

public class LoginModel(GitLabSignIn signIn, SessionVault vault, AuthFailureLimiter limiter,
    IOptions<AppOptions> options, ILogger<LoginModel> logger, SettingsStore settings) : PageModel
{
    [BindProperty] public string? AccessToken { get; set; }
    [BindProperty(SupportsGet = true)] public string? ReturnUrl { get; set; }

    public string? Error { get; private set; }
    public string? GitLabBaseUrl => signIn.Location?.BaseUrl;
    public bool GitLabOAuthEnabled => signIn.OAuthEnabled;

    private bool GitHubTypesMode => options.Value.GitHubTypes.IsConfigured;
    private bool SetupRequired => options.Value.FirstRunSetup && !GitHubTypesMode &&
                                  !settings.Current.FirstRunCompleted;
    private string ContinueUrl => Url.IsLocalUrl(ReturnUrl) &&
        !ReturnUrl!.StartsWith("/Login", StringComparison.OrdinalIgnoreCase) ? ReturnUrl : "/";

    public IActionResult OnGet()
    {
        if (SetupRequired)
            return RedirectToPage("/Setup");
        if (GitHubTypesMode)
            return RedirectToPage("/PilotConnection");
        return signIn.IsConnected(vault.Get(User.FindFirst(SessionVault.SidClaim)?.Value))
            ? LocalRedirect(ContinueUrl)
            : Page();
    }

    public async Task<IActionResult> OnPostGitLabAsync(CancellationToken ct)
    {
        if (SetupRequired)
            return RedirectToPage("/Setup");
        if (GitHubTypesMode)
            return NotFound();
        if (!GitLabOAuthEnabled || GitLabBaseUrl is not { } baseUrl)
        {
            Error = "Вход через GitLab OAuth недоступен.";
            return Page();
        }

        return Redirect(await signIn.StartOAuthAsync(HttpContext, baseUrl, ContinueUrl, ct));
    }

    public async Task<IActionResult> OnPostTokenAsync(CancellationToken ct)
    {
        if (SetupRequired)
            return RedirectToPage("/Setup");
        if (GitHubTypesMode)
            return NotFound();

        var address = HttpContext.Connection.RemoteIpAddress?.ToString();
        if (limiter.IsBlocked(AuthFailureLimiter.LoginScope, address))
            return StatusCode(StatusCodes.Status429TooManyRequests);
        if (GitLabBaseUrl is not { } baseUrl)
        {
            Error = "В настройках не задана группа GitLab.";
            return Page();
        }
        if (string.IsNullOrWhiteSpace(AccessToken))
        {
            Error = "Вставьте Personal Access Token.";
            return Page();
        }

        try
        {
            var session = await signIn.CompleteAsync(HttpContext, new GitLabToken
            {
                AccessToken = AccessToken.Trim(),
                BaseUrl = baseUrl,
            }, ct);
            TempData["Flash"] = session.LoginSummary();
            return LocalRedirect(ContinueUrl);
        }
        catch (GitLabApiException ex)
        {
            logger.LogWarning("Неудачный вход через GitLab: ответ {StatusCode}, адрес {Address}", ex.StatusCode, address);
            if (limiter.Fail(AuthFailureLimiter.LoginScope, address))
                logger.LogWarning("Вход: адрес {Address} заблокирован после неудачных попыток", address);
            Error = ex.Message;
            return Page();
        }
    }
}
