using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using ProAdmin.Sessions;
using ProAdmin.Settings;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;

namespace ProAdmin.GitLab;

public sealed class GitLabSignIn(
    GitLabAuthService auth, GitLabClientFactory factory, SettingsStore settings, SessionVault vault,
    IDataProtectionProvider dataProtection, ILogger<GitLabSignIn> logger)
{
    private const string StateCookie = "pwa.gitlab";
    private static readonly TimeSpan StateLifetime = TimeSpan.FromMinutes(10);
    private readonly ITimeLimitedDataProtector _protector =
        dataProtection.CreateProtector("ProAdmin.GitLabOAuth").ToTimeLimitedDataProtector();

    public GitLabLocation? Location =>
        GitLabLocation.TryParse(settings.Current.GitLabGroupUrl, out var location, out _) ? location : null;

    public bool OAuthEnabled => auth.OAuthEnabled;

    public bool IsConnected(UserSession? session) => session?.IsGitLabConnected(settings.Current) == true;

    public async Task<string> StartOAuthAsync(HttpContext context, string baseUrl, string returnUrl, CancellationToken ct)
    {
        var pending = new PendingSignIn(Convert.ToHexString(RandomNumberGenerator.GetBytes(32)),
            auth.ResolveRedirectUri(context.Request), baseUrl, returnUrl);
        var publicBase = await auth.ResolvePublicBaseUrlAsync(baseUrl, ct);
        var cookie = CookieOptions(context.Request);
        cookie.MaxAge = StateLifetime;
        context.Response.Cookies.Append(StateCookie, _protector.Protect(JsonSerializer.Serialize(pending), StateLifetime), cookie);
        return auth.BuildAuthorizeUrl(publicBase, pending.RedirectUri, pending.State);
    }

    public PendingSignIn? TakePending(HttpContext context, string? state)
    {
        if (string.IsNullOrEmpty(state) || !context.Request.Cookies.TryGetValue(StateCookie, out var value))
            return null;

        PendingSignIn? pending;
        try
        {
            pending = JsonSerializer.Deserialize<PendingSignIn>(_protector.Unprotect(value));
        }
        catch (CryptographicException)
        {
            return null;
        }

        if (pending is null || pending.State != state || !MatchesServer(pending.BaseUrl))
            return null;
        context.Response.Cookies.Delete(StateCookie, CookieOptions(context.Request));
        return pending;
    }

    private bool MatchesServer(string baseUrl) => string.Equals(Location?.BaseUrl, baseUrl, StringComparison.OrdinalIgnoreCase);

    private static CookieOptions CookieOptions(HttpRequest request) => new()
    {
        HttpOnly = true,
        Secure = request.IsHttps,
        SameSite = SameSiteMode.Lax,
        Path = request.PathBase.Add("/GitLab/Callback").Value,
    };

    public async Task<UserSession> CompleteAsync(HttpContext context, GitLabToken token, CancellationToken ct)
    {
        var user = await VerifyAccessAsync(token, settings.Current, ct);
        return await SignInAsync(context, token, user);
    }

    internal async Task<GitLabUser> VerifyAccessAsync(GitLabToken token, AdminSettings current, CancellationToken ct)
    {
        if (!GitLabLocation.TryParse(current.GitLabGroupUrl, out var location, out _) ||
            !string.Equals(location!.BaseUrl, token.BaseUrl, StringComparison.OrdinalIgnoreCase))
            throw new GitLabApiException(StatusCodes.Status400BadRequest, "Сервер GitLab изменился. Начните вход заново.");

        var git = factory.Create(token);
        var user = await git.GetCurrentUserAsync(ct);
        if (!user.IsAdmin)
        {
            var groups = new[] { location.Path, current.ReportsGroupPath, current.ModulesGroupPath,
                current.ServerModulesGroupPath, Types.TypesRepository.GroupOf(current.TypesProjectPath) }
                .OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase);

            var hasAccess = false;
            foreach (var group in groups)
            {
                var access = await GitLabGroupAccess.LoadAsync(git, group, user, ct);
                if (access.Level <= 0 || access.Error is not null)
                    continue;
                hasAccess = true;
                break;
            }

            if (!hasAccess)
                throw new GitLabApiException(StatusCodes.Status403Forbidden, "В GitLab не подтверждён доступ ни к одной группе админки.");
        }

        return user;
    }

    internal async Task<UserSession> SignInAsync(HttpContext context, GitLabToken token, GitLabUser user)
    {
        var previous = context.User.FindFirst(SessionVault.SidClaim)?.Value;
        var session = vault.Create(token, user);
        var identity = new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.Name, user.Username),
            new Claim(SessionVault.SidClaim, session.Id),
        }, CookieAuthenticationDefaults.AuthenticationScheme);

        await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
        vault.Remove(previous);
        logger.LogInformation("Вход через GitLab: пользователь {UserId}, адрес {Address}", user.Id,
            context.Connection.RemoteIpAddress);
        return session;
    }

    public sealed record PendingSignIn(string State, string RedirectUri, string BaseUrl, string ReturnUrl);
}
