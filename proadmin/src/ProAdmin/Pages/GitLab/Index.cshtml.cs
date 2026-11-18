using System.Security.Cryptography;
using ProAdmin.GitLab;
using Microsoft.AspNetCore.Mvc;

namespace ProAdmin.Pages.GitLab;

/// <summary>Шаг 2 входа: подключение GitLab пользователя (OAuth или Personal Access Token).</summary>
public class IndexModel : AppPageModel
{
    private readonly GitLabAuthService _auth;
    private readonly GitLabClientFactory _factory;

    public IndexModel(GitLabAuthService auth, GitLabClientFactory factory)
    {
        _auth = auth;
        _factory = factory;
    }

    [BindProperty(SupportsGet = true)] public string? ReturnUrl { get; set; }
    [BindProperty] public string? AccessToken { get; set; }

    public string? Error { get; set; }
    public bool OAuthEnabled => _auth.OAuthEnabled;
    public GitLabLocation? Location { get; private set; }
    public string RedirectUri => _auth.ResolveRedirectUri(Request);

    public void OnGet() => Init();

    public async Task<IActionResult> OnPostOAuthAsync(CancellationToken ct)
    {
        if (!Init() || !OAuthEnabled)
            return Page();

        var state = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        CurrentSession.PendingOAuth = (state, RedirectUri, Location!.BaseUrl, IsLocalUrl(ReturnUrl) ? ReturnUrl : null);
        var publicBase = await _auth.ResolvePublicBaseUrlAsync(Location.BaseUrl, ct);
        return Redirect(_auth.BuildAuthorizeUrl(publicBase, RedirectUri, state));
    }

    public async Task<IActionResult> OnPostTokenAsync(CancellationToken ct)
    {
        if (!Init())
            return Page();

        if (string.IsNullOrWhiteSpace(AccessToken))
        {
            Error = "Вставьте Personal Access Token.";
            return Page();
        }

        var token = new GitLabToken { AccessToken = AccessToken.Trim(), BaseUrl = Location!.BaseUrl };
        try
        {
            var user = await _factory.Create(token).GetCurrentUserAsync(ct);
            CurrentSession.GitLabToken = token;
            CurrentSession.GitLabUser = user;
            TempData["Flash"] = CurrentSession.LoginSummary();
        }
        catch (GitLabApiException ex)
        {
            Error = ex.Message;
            return Page();
        }

        return Continue();
    }

    public IActionResult OnPostDisconnect()
    {
        CurrentSession.GitLabToken = null;
        CurrentSession.GitLabUser = null;
        return RedirectToPage();
    }

    private IActionResult Continue() =>
        IsLocalUrl(ReturnUrl) ? LocalRedirect(ReturnUrl!) : RedirectToPage("/Index");

    private bool Init()
    {
        ViewData["Section"] = "gitlab";
        if (!GitLabLocation.TryParse(AppSettings.GitLabGroupUrl, out var location, out var error))
        {
            Error = $"{error} Сначала укажите группу GitLab в настройках.";
            return false;
        }
        Location = location;
        return true;
    }
}
