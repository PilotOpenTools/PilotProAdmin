namespace ProAdmin.Infrastructure;

/// <summary>Ссылки из внешних данных (GitLab, настройки) выводятся в href только со схемой http или https.</summary>
public static class SafeUrl
{
    public static string? Http(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            ? url
            : null;
}
