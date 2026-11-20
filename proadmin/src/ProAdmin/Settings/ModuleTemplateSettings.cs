namespace ProAdmin.Settings;

/// <summary>Источник пакетов в nuget.config нового модуля.</summary>
public sealed class NuGetSourceSetting
{
    /// <summary>Имя источника (key в nuget.config): nuget.org, cicd…</summary>
    public string Key { get; set; } = "";

    public string Url { get; set; } = "";

    /// <summary>
    /// Шаблоны пакетов через запятую для packageSourceMapping (Pilot.*). Пусто - «*»: все остальные пакеты.
    /// Если шаблоны есть хотя бы у одного источника, nuget.config получает packageSourceMapping.
    /// </summary>
    public string Patterns { get; set; } = "";

    public bool IsEmpty => string.IsNullOrWhiteSpace(Key) && string.IsNullOrWhiteSpace(Url);
}

/// <summary>Шаблон модулей одного вида: из него «Новый модуль» собирает каркас репозитория.</summary>
public sealed class ModuleKindTemplate
{
    public List<NuGetSourceSetting> Sources { get; set; } = new();

    /// <summary>Версия Ascon.Pilot.SDK (клиентские) или Ascon.Pilot.ServerExtensions.SDK (серверные).</summary>
    public string PilotSdkVersion { get; set; } = ModuleTemplateSettings.DefaultPilotSdkVersion;
}

/// <summary>Настройки шаблонов «Новый модуль» (Настройки / «Шаблон нового модуля»).</summary>
public sealed class ModuleTemplateSettings
{
    public const string DefaultPilotSdkVersion = "26.13.0";
    public const string DefaultClientSdkVersion = "2.1.2";
    public const string NuGetOrg = "https://api.nuget.org/v3/index.json";

    /// <summary>Клиентские модули: по умолчанию nuget.org - там же пакет Pilot.ClientSdk (github.com/PilotOpenTools/PilotClientSdk).</summary>
    public ModuleKindTemplate Client { get; set; } = new() { Sources = DefaultSources() };

    public ModuleKindTemplate Server { get; set; } = new() { Sources = DefaultSources() };

    /// <summary>Версия Pilot.ClientSdk в новых клиентских модулях.</summary>
    public string ClientSdkVersion { get; set; } = DefaultClientSdkVersion;

    /// <summary>Галочка «На Pilot.ClientSdk» в форме нового клиентского модуля по умолчанию.</summary>
    public bool UseClientSdkByDefault { get; set; } = true;

    /// <summary>Клиент, который запускается по F5: Pilot-BIM или Pilot-ICE Enterprise.</summary>
    public string DebugClient { get; set; } = "Pilot-BIM";

    public static List<NuGetSourceSetting> DefaultSources() => new() { new() { Key = "nuget.org", Url = NuGetOrg } };

    /// <summary>Пустые строки формы убираются; без источников - nuget.org.</summary>
    public void Normalize()
    {
        foreach (var t in new[] { Client ??= new(), Server ??= new() })
        {
            t.Sources = (t.Sources ?? new()).Where(s => s is not null && !s.IsEmpty)
                .Select(s => new NuGetSourceSetting { Key = s.Key?.Trim() ?? "", Url = s.Url?.Trim() ?? "", Patterns = NormalizePatterns(s.Patterns) })
                .ToList();
            if (t.Sources.Count == 0)
                t.Sources = DefaultSources();
            t.PilotSdkVersion = string.IsNullOrWhiteSpace(t.PilotSdkVersion) ? DefaultPilotSdkVersion : t.PilotSdkVersion.Trim();
        }
        ClientSdkVersion = string.IsNullOrWhiteSpace(ClientSdkVersion) ? DefaultClientSdkVersion : ClientSdkVersion.Trim();
        DebugClient = DebugClient == "Pilot-ICE Enterprise" ? DebugClient : "Pilot-BIM";
    }

    /// <summary>Ошибки формы настроек.</summary>
    public IEnumerable<string> Validate()
    {
        foreach (var (title, t) in new[] { ("клиентских", Client), ("серверных", Server) })
        {
            foreach (var s in t.Sources.Where(s => !s.IsEmpty))
            {
                if (string.IsNullOrWhiteSpace(s.Key) || s.Key.IndexOfAny(new[] { '"', '<', '>', '&' }) >= 0)
                    yield return $"Шаблон {title} модулей: у источника {s.Url} нет имени (или в нём кавычки и угловые скобки).";
                if (!Uri.TryCreate(s.Url?.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length > 0)
                    yield return $"Шаблон {title} модулей: адрес источника «{s.Key}» - http(s)://… без логина и пароля (вход - в настройках NuGet разработчика).";
            }
            var duplicates = t.Sources.Where(s => !s.IsEmpty).GroupBy(s => s.Key.Trim(), StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
            if (duplicates.Count > 0)
                yield return $"Шаблон {title} модулей: источники с одинаковым именем - {string.Join(", ", duplicates)}.";
            if (!IsVersion(t.PilotSdkVersion))
                yield return $"Шаблон {title} модулей: версия Pilot SDK - X.Y.Z.";
        }
        if (!IsVersion(ClientSdkVersion))
            yield return "Шаблон клиентских модулей: версия Pilot.ClientSdk - X.Y.Z.";
    }

    private static bool IsVersion(string? v) => v is not null && System.Text.RegularExpressions.Regex.IsMatch(v.Trim(), @"^\d+\.\d+\.\d+$");

    private static string NormalizePatterns(string? patterns) =>
        string.Join(", ", (patterns ?? "").Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
}
