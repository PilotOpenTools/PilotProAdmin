using Ascon.Pilot.DataClasses;
using ProAdmin.Automations;

namespace ProAdmin.Pilot;

/// <summary>Учётные данные администратора сервера Pilot (живут только в памяти процесса).</summary>
public sealed record PilotLogin(string Login, string Password);

/// <summary>Адрес вида http://host:5545/database, разобранный на сервер и базу.</summary>
public sealed record PilotAddress(string ServerUrl, string Database)
{
    public static bool TryParse(string? url, out PilotAddress? address, out string? error)
    {
        address = null;
        error = null;

        if (string.IsNullOrWhiteSpace(url) ||
            !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            error = "Адрес сервера Pilot должен быть вида http://host:5545/имя_базы.";
            return false;
        }

        var segments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var database = segments.Length > 0 ? Uri.UnescapeDataString(segments[^1]) : "";
        var basePath = segments.Length > 1 ? "/" + string.Join('/', segments[..^1]) : "";
        address = new PilotAddress($"{uri.Scheme}://{uri.Authority}{basePath}", database);
        return true;
    }
}

public sealed record AutomationSnapshot(string Name, string Triggers, string CSharpCode, bool IsPaused, int Index)
{
    public static AutomationSnapshot From(AutomationScript script, int index) =>
        new(script.Name ?? "", script.Triggers ?? "", script.CSharpCode ?? "", script.IsPaused, index);

    public string Hash => AutomationFiles.ContentHash(Triggers, CSharpCode, IsPaused);

    public bool HasCSharp => !string.IsNullOrWhiteSpace(CSharpCode);

    public AutomationVersion? Version => AutomationVersion.Find(Triggers);
}

public enum PilotErrorKind
{
    Configuration,
    Authentication,
    Connection,
    Other,
}

public sealed class PilotGatewayException : Exception
{
    public PilotGatewayException(PilotErrorKind kind, string message, Exception? inner = null)
        : base(message, inner) => Kind = kind;

    public PilotErrorKind Kind { get; }
}
