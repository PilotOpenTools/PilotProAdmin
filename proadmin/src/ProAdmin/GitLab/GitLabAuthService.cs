using System.Collections.Concurrent;
using System.Net.Http.Json;
using ProAdmin.Infrastructure;
using Microsoft.Extensions.Options;

namespace ProAdmin.GitLab;

/// <summary>OAuth2 (authorization code) для входа пользователя в свой GitLab.</summary>
public sealed class GitLabAuthService
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly NetworkAddressMapper _network;
    private readonly GitLabOAuthOptions _options;
    private readonly string? _publicUrl;

    public GitLabAuthService(IHttpClientFactory httpFactory, NetworkAddressMapper network, IOptions<AppOptions> options)
    {
        _httpFactory = httpFactory;
        _network = network;
        _options = options.Value.GitLab;
        _publicUrl = options.Value.PublicUrl;
    }

    public bool OAuthEnabled => _options.IsConfigured;

    private readonly ConcurrentDictionary<string, (DateTimeOffset At, string Url)> _publicBase = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Адрес GitLab для браузера - его external_url. Если в настройках localhost:8080, а external_url другой
    /// (например, http://gitlab.example.local:8080), GitLab перекидывает вход на свой адрес, cookie другого хоста теряется
    /// и после входа пользователь попадает на главную GitLab вместо страницы разрешения доступа.
    /// Определяется по редиректу неавторизованного /oauth/authorize на страницу входа; кэш на 10 минут.
    /// </summary>
    public async Task<string> ResolvePublicBaseUrlAsync(string baseUrl, CancellationToken ct)
    {
        if (_publicBase.TryGetValue(baseUrl, out var cached) && cached.At > DateTimeOffset.UtcNow.AddMinutes(-10))
            return cached.Url;

        var result = baseUrl;
        try
        {
            var http = _httpFactory.CreateClient("gitlab-probe");
            using var response = await http.GetAsync($"{_network.ToInternal(baseUrl)}/oauth/authorize", ct);
            var location = response.Headers.Location;
            if (location is { IsAbsoluteUri: true })
            {
                var path = location.AbsolutePath;
                var signIn = path.IndexOf("/users/sign_in", StringComparison.OrdinalIgnoreCase);
                var root = signIn >= 0 ? path[..signIn] : "";
                result = $"{location.Scheme}://{location.Authority}{root}".TrimEnd('/');
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // GitLab недоступен - оставляем адрес из настроек, ошибку покажет сам GitLab.
        }

        _publicBase[baseUrl] = (DateTimeOffset.UtcNow, result);
        return result;
    }

    /// <summary>
    /// Адрес возврата OAuth: явный RedirectUri, иначе публичный адрес админки (App:PublicUrl), иначе адрес запроса.
    /// </summary>
    public string ResolveRedirectUri(HttpRequest request) =>
        !string.IsNullOrWhiteSpace(_options.RedirectUri) ? _options.RedirectUri!
        : !string.IsNullOrWhiteSpace(_publicUrl) ? $"{_publicUrl.Trim().TrimEnd('/')}/GitLab/Callback"
        : $"{request.Scheme}://{request.Host}{request.PathBase}/GitLab/Callback";

    public string BuildAuthorizeUrl(string baseUrl, string redirectUri, string state) =>
        $"{baseUrl}/oauth/authorize?client_id={Uri.EscapeDataString(_options.ClientId!)}" +
        $"&redirect_uri={Uri.EscapeDataString(redirectUri)}&response_type=code" +
        $"&state={Uri.EscapeDataString(state)}&scope={Uri.EscapeDataString(_options.Scopes)}";

    public async Task<GitLabToken> ExchangeCodeAsync(string baseUrl, string code, string redirectUri, CancellationToken ct)
    {
        var response = await RequestTokenAsync(baseUrl, new Dictionary<string, string>
        {
            ["client_id"] = _options.ClientId!,
            ["client_secret"] = _options.ClientSecret!,
            ["code"] = code,
            ["grant_type"] = "authorization_code",
            ["redirect_uri"] = redirectUri,
        }, ct);

        return new GitLabToken
        {
            AccessToken = response.AccessToken,
            RefreshToken = response.RefreshToken,
            ExpiresAt = ExpiresAt(response),
            IsOAuth = true,
            RedirectUri = redirectUri,
            BaseUrl = baseUrl,
        };
    }

    /// <summary>Обновляет OAuth-токен, если до истечения меньше двух минут.</summary>
    public async Task EnsureFreshAsync(GitLabToken token, CancellationToken ct)
    {
        if (!token.IsOAuth || token.RefreshToken is null || token.ExpiresAt is null)
            return;
        if (token.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(2))
            return;

        await token.RefreshLock.WaitAsync(ct);
        try
        {
            if (token.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(2))
                return;

            var response = await RequestTokenAsync(token.BaseUrl, new Dictionary<string, string>
            {
                ["client_id"] = _options.ClientId!,
                ["client_secret"] = _options.ClientSecret!,
                ["refresh_token"] = token.RefreshToken,
                ["grant_type"] = "refresh_token",
                ["redirect_uri"] = token.RedirectUri ?? "",
            }, ct);

            token.AccessToken = response.AccessToken;
            token.RefreshToken = response.RefreshToken ?? token.RefreshToken;
            token.ExpiresAt = ExpiresAt(response);
        }
        finally
        {
            token.RefreshLock.Release();
        }
    }

    private async Task<GitLabOAuthTokenResponse> RequestTokenAsync(string baseUrl, Dictionary<string, string> form, CancellationToken ct)
    {
        var http = _httpFactory.CreateClient("gitlab");
        using var response = await http.PostAsync($"{_network.ToInternal(baseUrl)}/oauth/token", new FormUrlEncodedContent(form), ct);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            throw new GitLabApiException((int)response.StatusCode, $"GitLab отклонил OAuth-запрос: {Truncate(body)}");
        }

        return await response.Content.ReadFromJsonAsync<GitLabOAuthTokenResponse>(GitLabClient.Json, ct)
               ?? throw new GitLabApiException(500, "Пустой ответ GitLab на OAuth-запрос.");
    }

    private static DateTimeOffset? ExpiresAt(GitLabOAuthTokenResponse r) =>
        r.ExpiresIn is { } seconds ? DateTimeOffset.UtcNow.AddSeconds(seconds) : null;

    private static string Truncate(string s) => s.Length > 300 ? s[..300] + "…" : s;
}
