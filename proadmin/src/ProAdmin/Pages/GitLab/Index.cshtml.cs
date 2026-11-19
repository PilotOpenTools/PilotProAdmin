using ProAdmin.GitLab;
using Microsoft.AspNetCore.Mvc;

namespace ProAdmin.Pages.GitLab;

/// <summary>Текущая учётная запись GitLab и параметры входа.</summary>
public class IndexModel : AppPageModel
{
    private readonly GitLabAuthService _auth;

    public IndexModel(GitLabAuthService auth)
    {
        _auth = auth;
    }

    [BindProperty(SupportsGet = true)] public string? ReturnUrl { get; set; }

    public string? Error { get; private set; }
    public bool OAuthEnabled => _auth.OAuthEnabled;
    public GitLabLocation? Location { get; private set; }
    public string RedirectUri => _auth.ResolveRedirectUri(Request);

    public string ContinueUrl => Url.IsLocalUrl(ReturnUrl) ? ReturnUrl! : "/";

    public void OnGet()
    {
        ViewData["Section"] = "gitlab";
        if (!GitLabLocation.TryParse(AppSettings.GitLabGroupUrl, out var location, out var error))
        {
            Error = $"{error} Сначала укажите группу GitLab в настройках.";
            return;
        }
        Location = location;
    }
}
