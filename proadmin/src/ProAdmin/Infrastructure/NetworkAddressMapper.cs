using System.Text.RegularExpressions;

namespace ProAdmin.Infrastructure;

/// <summary>
/// Подменяет loopback-хост в URL на <see cref="AppOptions.LocalhostAlias"/>.
/// Нужно, когда приложение работает в контейнере, а Pilot/GitLab заданы как localhost:порт хоста.
/// </summary>
public sealed partial class NetworkAddressMapper
{
    private readonly string? _alias;

    public NetworkAddressMapper(string? alias) =>
        _alias = string.IsNullOrWhiteSpace(alias) ? null : alias.Trim();

    public string ToInternal(string url)
    {
        if (_alias is null || string.IsNullOrEmpty(url))
            return url;

        return LoopbackHost().Replace(url, m => m.Groups["scheme"].Value + _alias, 1);
    }

    [GeneratedRegex(@"^(?<scheme>[a-zA-Z][a-zA-Z0-9+.-]*://)(localhost|127\.0\.0\.1|\[::1\])(?=[:/?#]|$)", RegexOptions.IgnoreCase)]
    private static partial Regex LoopbackHost();
}
