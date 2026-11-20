using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using ProAdmin.GitLab;
using ProAdmin.Jobs;
using ProAdmin.Pilot;
using ProAdmin.Pilot.ServerModules;
using ProAdmin.Settings;

namespace ProAdmin.Modules;

/// <summary>Куда ставится модуль PilotOpenTools.</summary>
public enum OpenToolKind
{
    /// <summary>Клиентский модуль: объект Extension в папке расширений базы.</summary>
    Client,

    /// <summary>Центральный серверный модуль ServerHost: объект $PilotServerExtension, читается Pilot-Server при старте.</summary>
    ServerHost,
}

/// <summary>
/// Модуль из открытых репозиториев github.com/PilotOpenTools. Версия берётся из последнего релиза (не pre-release),
/// файл - первый файл релиза, имя которого подходит под <see cref="Asset"/>.
/// </summary>
public sealed record OpenTool(string Key, string Title, string Repository, OpenToolKind Kind, Regex Asset, string Description,
    IReadOnlyList<string> Names)
{
    public string RepositoryUrl => $"https://github.com/{Repository}";
    public string ReleasesUrl => $"{RepositoryUrl}/releases";

    /// <summary>Объект модуля на сервере - по «Наименованию», коду в скобках или имени сборки.</summary>
    public bool Matches(string? extensionName)
    {
        if (string.IsNullOrWhiteSpace(extensionName))
            return false;
        var keys = new[] { extensionName, ModulePackage.CodeFromName(extensionName) }
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .SelectMany(k => new[] { ProjectNames.Key(k), ProjectNames.Key(ModuleKinds.CoreName(k!)) })
            .ToHashSet();
        return Names.Any(n => keys.Contains(ProjectNames.Key(n)));
    }
}

public static class OpenToolsCatalog
{
    public const string Organization = "https://github.com/PilotOpenTools";

    /// <summary>Порядок - порядок установки: общий модуль раньше модулей, которые на нём построены.</summary>
    public static IReadOnlyList<OpenTool> All { get; } = new[]
    {
        new OpenTool("clientsdk", "PilotClientSdk", "PilotOpenTools/PilotClientSdk", OpenToolKind.Client,
            new Regex(@"^113Pilot\.ClientSdk\.ext2-[\d.]+\.zip$", RegexOptions.IgnoreCase),
            "Общий модуль для клиентских модулей: статистика, окно загрузки модулей, общие методы SDK. Ставится раньше остальных.",
            new[] { "Pilot.ClientSdk", "113Pilot.ClientSdk.ext2", "Modules", "113Modules.ext2" }),
        new OpenTool("reports", "PilotReports", "PilotOpenTools/PilotReports", OpenToolKind.Client,
            new Regex(@"^112Reports\.ext2-[\d.]+\.zip$", RegexOptions.IgnoreCase),
            "Модуль отчётов: общие функции и Pilot SDK для скриптов отчётов, статистика построений, отправка отчётов в GitLab. Нужен PilotClientSdk.",
            new[] { "Reports", "112Reports.ext2" }),
        new OpenTool("lookup", "PilotLookUp", "PilotOpenTools/PilotLookUp", OpenToolKind.Client,
            new Regex(@"^PilotLookUp.*\.zip$", RegexOptions.IgnoreCase),
            "Инструмент разработчика: просмотр объектов, типов и атрибутов Pilot из клиента.",
            new[] { "PilotLookUp", "PilotLookUp.ext2" }),
        new OpenTool("serverhost", "ServerHost", "PilotOpenTools/PilotProAdmin", OpenToolKind.ServerHost,
            new Regex(@"^ServerHost-[\d.]+\.zip$", RegexOptions.IgnoreCase),
            "Центральный серверный модуль: загружает остальные серверные модули и обновляет их без перезапуска сервера. Выпускается вместе с PilotProAdmin.",
            new[] { ServerHostPackage.ExtensionName, "ServerHost.servext" }),
    };

    public static OpenTool? Find(string? key) => All.FirstOrDefault(t => string.Equals(t.Key, key, StringComparison.OrdinalIgnoreCase));

    /// <summary>Клиентский модуль PilotOpenTools, которому соответствует объект на сервере; null - не из PilotOpenTools.</summary>
    public static OpenTool? ForClientModule(string? extensionName) =>
        All.FirstOrDefault(t => t.Kind == OpenToolKind.Client && t.Matches(extensionName));
}

/// <summary>Последний релиз модуля на GitHub и его пакет (или почему его нет).</summary>
public sealed record OpenToolRelease(string? Tag, string? Url, DateTimeOffset? Published, ModulePackageInfo? Package, string? Error)
{
    public string? Version => Package is null ? null : ModuleVersion.Display(Package.Version);
}

/// <summary>
/// Релизы PilotOpenTools на GitHub без токена: api.github.com/repos/…/releases/latest и файл релиза.
/// Ответы держатся в памяти 10 минут, чтобы не упираться в лимит анонимных запросов GitHub (60 в час).
/// </summary>
public sealed class OpenToolsReleases
{
    public const string ClientName = "pilotopentools";
    public const long MaxAssetBytes = 64 * 1024 * 1024;
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(10);

    /// <summary>Подпапка каталога данных админки с файлами релизов на случай, когда GitHub недоступен или файла в релизе нет.</summary>
    public const string LocalFolder = "opentools";

    private readonly IHttpClientFactory _http;
    private readonly ILogger<OpenToolsReleases> _logger;
    private readonly string? _localDir;
    private readonly ConcurrentDictionary<string, (DateTimeOffset At, OpenToolRelease Release)> _cache = new();

    public OpenToolsReleases(IHttpClientFactory http, ILogger<OpenToolsReleases> logger, string? localDir = null)
    {
        _http = http;
        _logger = logger;
        _localDir = localDir;
    }

    /// <summary>Папка локальных файлов релизов (data/opentools); null - не задана.</summary>
    public string? LocalDirectory => _localDir;

    public async Task<OpenToolRelease> GetAsync(OpenTool tool, bool refresh, CancellationToken ct)
    {
        if (!refresh && _cache.TryGetValue(tool.Key, out var hit) && DateTimeOffset.UtcNow - hit.At < Ttl)
            return hit.Release;
        var release = await LoadAsync(tool, ct);
        if (release.Package is null && Local(tool) is { } local)
            release = local with { Error = null, Tag = local.Tag + (release.Error is null ? "" : $" (GitHub: {release.Error})") };
        // Ошибку держим недолго: GitHub мог быть недоступен минуту.
        _cache[tool.Key] = (release.Error is null ? DateTimeOffset.UtcNow : DateTimeOffset.UtcNow - Ttl + TimeSpan.FromMinutes(1), release);
        return release;
    }

    private async Task<OpenToolRelease> LoadAsync(OpenTool tool, CancellationToken ct)
    {
        var http = _http.CreateClient(ClientName);
        string? tag = null, url = null;
        DateTimeOffset? published = null;
        try
        {
            List<(string Name, string? Url, long Size)> assets;
            using (var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{tool.Repository}/releases/latest"))
            {
                request.Headers.Accept.ParseAdd("application/vnd.github+json");
                using var response = await http.SendAsync(request, ct);
                if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
                {
                    // Лимит анонимных запросов к API (60 в час на адрес) - читаем страницу релиза, у неё лимита API нет.
                    _logger.LogInformation("PilotOpenTools: api.github.com ответил {Status}, читаю страницу релиза {Repository}", (int)response.StatusCode, tool.Repository);
                    (tag, url, assets) = await FromWebAsync(http, tool, ct);
                }
                else if (response.StatusCode == HttpStatusCode.NotFound)
                    return new OpenToolRelease(null, null, null, null, "в репозитории нет опубликованных релизов");
                else if (!response.IsSuccessStatusCode)
                    return new OpenToolRelease(null, null, null, null, $"GitHub ответил {(int)response.StatusCode} {response.ReasonPhrase}");
                else
                {
                    using var json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct));
                    var root = json.RootElement;
                    tag = root.GetProperty("tag_name").GetString();
                    url = root.GetProperty("html_url").GetString();
                    if (root.TryGetProperty("published_at", out var p) && p.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(p.GetString(), out var at))
                        published = at;
                    assets = root.GetProperty("assets").EnumerateArray()
                        .Select(a => (a.GetProperty("name").GetString() ?? "", a.GetProperty("browser_download_url").GetString(), a.GetProperty("size").GetInt64()))
                        .ToList();
                }
            }
            var asset = assets.FirstOrDefault(a => tool.Asset.IsMatch(a.Name));
            if (asset.Url is null)
                return new OpenToolRelease(tag, url, published, null, $"в релизе {tag} нет файла модуля ({tool.Asset})");
            if (asset.Size is < 0 or > MaxAssetBytes)
                return new OpenToolRelease(tag, url, published, null, $"файл {asset.Name} размером {ModulePackage.FormatSize(asset.Size)} не поддерживается");
            if (!IsGitHubDownload(asset.Url))
                return new OpenToolRelease(tag, url, published, null, $"неожиданный адрес файла релиза: {asset.Url}");

            var zip = await http.GetByteArrayAsync(asset.Url, ct);
            var kind = tool.Kind == OpenToolKind.ServerHost ? ModuleKind.Server : ModuleKind.Client;
            var package = tool.Kind == OpenToolKind.ServerHost
                ? ServerHostPackage.Check(zip, asset.Name, null).Package
                : ModulePackage.Analyze(zip, asset.Name, kind);
            return new OpenToolRelease(tag, url, published, package, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidDataException or KeyNotFoundException or InvalidOperationException
                                   && !ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "PilotOpenTools: релиз {Repository} не прочитан", tool.Repository);
            var reason = ex is TaskCanceledException ? "GitHub не ответил вовремя" : ex.Message;
            return new OpenToolRelease(tag, url, published, null, $"релиз не прочитан: {reason}");
        }
    }

    /// <summary>
    /// Файл релиза, положенный администратором в data/opentools (закрытый контур или файла ещё нет в релизе GitHub):
    /// подходящий по имени файл с самой большой версией.
    /// </summary>
    private OpenToolRelease? Local(OpenTool tool)
    {
        if (_localDir is null || !Directory.Exists(_localDir))
            return null;
        var kind = tool.Kind == OpenToolKind.ServerHost ? ModuleKind.Server : ModuleKind.Client;
        OpenToolRelease? best = null;
        foreach (var file in Directory.EnumerateFiles(_localDir).Where(f => tool.Asset.IsMatch(Path.GetFileName(f))))
        {
            try
            {
                var info = new FileInfo(file);
                if (info.Length is <= 0 or > MaxAssetBytes)
                    continue;
                var zip = File.ReadAllBytes(file);
                var name = Path.GetFileName(file);
                var package = tool.Kind == OpenToolKind.ServerHost ? ServerHostPackage.Check(zip, name, null).Package : ModulePackage.Analyze(zip, name, kind);
                var candidate = new OpenToolRelease($"локальный файл {name}", null, new DateTimeOffset(info.LastWriteTimeUtc), package, null);
                if (best is null || OpenToolsService.Compare(best.Version, candidate.Version) == OpenToolSync.Outdated)
                    best = candidate;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "PilotOpenTools: локальный файл {File} не прочитан", file);
            }
        }
        return best;
    }

    /// <summary>
    /// Без API: github.com/…/releases/latest перенаправляет на страницу тега, список файлов - во фрагменте expanded_assets.
    /// Размер файла здесь неизвестен (0): его ограничивает MaxResponseContentBufferSize клиента.
    /// </summary>
    private static async Task<(string? Tag, string? Url, List<(string Name, string? Url, long Size)> Assets)> FromWebAsync(
        HttpClient http, OpenTool tool, CancellationToken ct)
    {
        using var latest = await http.GetAsync($"https://github.com/{tool.Repository}/releases/latest", ct);
        var final = latest.RequestMessage?.RequestUri;
        var marker = $"/{tool.Repository}/releases/tag/";
        if (!latest.IsSuccessStatusCode || final is null || !final.AbsolutePath.StartsWith(marker, StringComparison.OrdinalIgnoreCase))
            throw new HttpRequestException(latest.StatusCode == HttpStatusCode.NotFound
                ? "в репозитории нет опубликованных релизов"
                : $"страница последнего релиза не открылась ({(int)latest.StatusCode})");
        var tag = Uri.UnescapeDataString(final.AbsolutePath[marker.Length..]);
        var html = await http.GetStringAsync($"https://github.com/{tool.Repository}/releases/expanded_assets/{Uri.EscapeDataString(tag)}", ct);
        var prefix = $"/{tool.Repository}/releases/download/{tag}/";
        var assets = Regex.Matches(html, "href=\"([^\"]+)\"").Select(m => WebUtility.HtmlDecode(m.Groups[1].Value))
            .Where(h => h.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Distinct()
            .Select(h => (Uri.UnescapeDataString(h[prefix.Length..]), (string?)("https://github.com" + h), 0L))
            .ToList();
        return (tag, final.ToString(), assets);
    }

    /// <summary>Файлы релизов отдаёт github.com, дальше редирект на хранилище GitHub.</summary>
    private static bool IsGitHubDownload(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps && u.Host == "github.com" && u.UserInfo.Length == 0;

    /// <summary>Редиректы файла релиза разрешены только на хранилище GitHub по https.</summary>
    public static bool IsAllowedRedirect(Uri? target) =>
        target is { Scheme: "https" } && target.UserInfo.Length == 0 &&
        (target.Host == "github.com" || target.Host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase));
}

/// <summary>Сравнение версии на сервере с релизом GitHub.</summary>
public enum OpenToolSync
{
    /// <summary>Сервер недоступен или версия не прочитана.</summary>
    Unknown,
    Missing,
    Outdated,
    Current,
    /// <summary>На сервере версия новее релиза (выложена из GitLab или вручную).</summary>
    Newer,
    /// <summary>Модуль отключён админкой (объект в корзине Pilot).</summary>
    Disabled,
}

public sealed record OpenToolCell(PilotModule? Module, OpenToolSync Sync, string? Error)
{
    public string? Version => Module?.Version;
}

public sealed class OpenToolRow
{
    public required OpenTool Tool { get; init; }
    public required OpenToolRelease Release { get; init; }
    public Dictionary<string, OpenToolCell> Envs { get; } = new();

    public bool CanInstall(string env) =>
        Release.Package is not null && Envs.TryGetValue(env, out var c) &&
        c.Sync is OpenToolSync.Missing or OpenToolSync.Outdated;

    public IEnumerable<string> Pending => Envs.Keys.Where(CanInstall);
}

public sealed class OpenToolsState
{
    public List<ModuleEnv> Envs { get; } = new();
    public List<OpenToolRow> Rows { get; } = new();
    public Dictionary<string, PilotGatewayException> EnvErrors { get; } = new();
}

/// <summary>Версии модулей PilotOpenTools на тесте, стейдже и проде против последних релизов GitHub; установка и обновление.</summary>
public sealed class OpenToolsService
{
    private readonly OpenToolsReleases _releases;
    private readonly PilotModuleGateway _modules;
    private readonly DisabledModuleStore _disabled;
    private readonly JobStore _jobs;

    public OpenToolsService(OpenToolsReleases releases, PilotModuleGateway modules, DisabledModuleStore disabled, JobStore jobs)
    {
        _releases = releases;
        _modules = modules;
        _disabled = disabled;
        _jobs = jobs;
    }

    public async Task<OpenToolsState> LoadAsync(AdminSettings settings, Func<string, PilotLogin> login, bool refresh, CancellationToken ct)
    {
        var state = new OpenToolsState();
        state.Envs.AddRange(ModuleEnv.All(settings));
        var releases = OpenToolsCatalog.All.ToDictionary(t => t.Key, t => _releases.GetAsync(t, refresh, ct));

        var servers = await Task.WhenAll(state.Envs.Select(async env =>
        {
            try
            {
                var clients = await _modules.ListAsync(env.PilotUrl, login(env.Key), settings.ReportsLicenseType, ct);
                var extensions = await _modules.ListServerExtensionsAsync(env.PilotUrl, login(env.Key), settings.ReportsLicenseType, ct);
                return new ServerModules(env, clients.Modules, extensions, null);
            }
            catch (PilotGatewayException ex)
            {
                return new ServerModules(env, null, null, ex);
            }
        }));

        foreach (var tool in OpenToolsCatalog.All)
        {
            var release = await releases[tool.Key];
            var row = new OpenToolRow { Tool = tool, Release = release };
            foreach (var (env, clients, extensions, error) in servers)
            {
                if (error is not null)
                {
                    state.EnvErrors[env.Key] = error;
                    row.Envs[env.Key] = new OpenToolCell(null, OpenToolSync.Unknown, error.Message);
                    continue;
                }
                row.Envs[env.Key] = Cell(tool, release, env, tool.Kind == OpenToolKind.ServerHost ? extensions! : clients!);
            }
            state.Rows.Add(row);
        }
        return state;
    }

    private sealed record ServerModules(ModuleEnv Env, List<PilotModule>? Clients, List<PilotModule>? Extensions, PilotGatewayException? Error);

    private OpenToolCell Cell(OpenTool tool, OpenToolRelease release, ModuleEnv env, List<PilotModule> modules)
    {
        var module = Find(tool, release, modules);
        if (module is null)
        {
            var off = tool.Kind == OpenToolKind.Client
                ? _disabled.For(env.Key).FirstOrDefault(d => tool.Matches(d.Name))
                : null;
            return off is not null
                ? new OpenToolCell(off.ToModule(), OpenToolSync.Disabled, null)
                : new OpenToolCell(null, OpenToolSync.Missing, null);
        }
        return new OpenToolCell(module, Compare(module.Version, release.Version), null);
    }

    private static PilotModule? Find(OpenTool tool, OpenToolRelease release, List<PilotModule> modules)
    {
        if (tool.Kind == OpenToolKind.ServerHost)
            return ServerHostPackage.Find(modules);
        if (release.Package is { } package && ModuleWorkflowBase.MatchPackage(modules, package) is { } byPackage)
            return byPackage;
        return modules.FirstOrDefault(m => tool.Matches(m.Name));
    }

    internal static OpenToolSync Compare(string? server, string? github)
    {
        if (!TryVersion(server, out var s) || !TryVersion(github, out var g))
            return OpenToolSync.Unknown;
        var c = s!.CompareTo(g);
        return c < 0 ? OpenToolSync.Outdated : c == 0 ? OpenToolSync.Current : OpenToolSync.Newer;
    }

    private static bool TryVersion(string? text, out Version? version)
    {
        version = null;
        if (string.IsNullOrWhiteSpace(text) || !Version.TryParse(text.Trim().TrimStart('v', 'V'), out var v))
            return false;
        // 2.1.1 и 2.1.1.0 - одна версия
        version = new Version(v.Major, v.Minor, Math.Max(v.Build, 0), Math.Max(v.Revision, 0));
        return true;
    }

    /// <summary>
    /// Установить или обновить модуль на сервере контура из релиза GitHub. Возвращает текст для пользователя;
    /// ошибка - <see cref="WorkflowException"/> или <see cref="PilotGatewayException"/>. Каждая запись - операция в «Операциях».
    /// </summary>
    public async Task<string> InstallAsync(AdminSettings settings, OpenTool tool, ModuleEnv env, PilotLogin login, string pilotUser, string? gitLabUser,
        CancellationToken ct)
    {
        var release = await _releases.GetAsync(tool, refresh: false, ct);
        if (release.Package is not { } package)
            throw new WorkflowException($"{tool.Title}: {release.Error ?? "нет файла релиза"}.");
        var license = settings.ReportsLicenseType;
        var modules = tool.Kind == OpenToolKind.ServerHost
            ? await _modules.ListServerExtensionsAsync(env.PilotUrl, login, license, ct)
            : (await _modules.ListAsync(env.PilotUrl, login, license, ct)).Modules;
        var existing = Find(tool, release, modules);

        var job = new PublishJob
        {
            Operation = existing is null ? "установка из PilotOpenTools" : "обновление из PilotOpenTools",
            Env = env.Key,
            EnvTitle = env.Title,
            Branch = $"GitHub {release.Tag}",
            AutomationName = tool.Title,
            CommitMessage = existing is null ? package.Version : $"{existing.Version ?? "?"} / {package.Version}",
            PilotUser = pilotUser,
            GitLabUser = gitLabUser,
        };
        job.Add("info", $"{tool.Title} {release.Tag}: {package.FileName}", release.Url);
        _jobs.Add(job);

        var reason = $"{package.Version} · PilotOpenTools {tool.Repository} {release.Tag} · из админки, {gitLabUser ?? pilotUser}";
        try
        {
            string message;
            if (tool.Kind == OpenToolKind.ServerHost)
            {
                if (existing is null)
                {
                    await _modules.CreateServerExtensionAsync(env.PilotUrl, login, license, ServerHostPackage.ExtensionName,
                        ServerHostPackage.ExtensionName + ".zip", package.Zip, package.ExtensionAdditional, reason, ct);
                    message = $"{env.Title}: ServerHost {package.Version} установлен в базу. Перезапустите сервер Pilot (все узлы в кластере) - " +
                              "это последний перезапуск, дальше серверные модули обновляются без него.";
                }
                else
                {
                    await _modules.UpdateServerExtensionAsync(env.PilotUrl, login, license, existing.Id,
                        existing.FileName ?? ServerHostPackage.ExtensionName + ".zip", package.Zip, package.ExtensionAdditional, reason, ct);
                    message = $"{env.Title}: ServerHost {package.Version} записан в базу. Вступит в силу после перезапуска сервера Pilot (всех узлов в кластере).";
                }
            }
            else if (existing is not null)
            {
                await _modules.UpdateAsync(env.PilotUrl, login, license, existing.Id, package, reason, ct);
                message = $"{env.Title}: {tool.Title} обновлён {existing.Version ?? "?"} / {package.Version}. {_modules.AfterUpdateNote}";
            }
            else
            {
                if (_disabled.For(env.Key).FirstOrDefault(d => tool.Matches(d.Name)) is { } off)
                    throw new WorkflowException($"{env.Title}: {tool.Title} отключён ({off.By}: {off.Reason}). Включите его на странице «Модули», затем обновите.");
                var list = await _modules.ListAsync(env.PilotUrl, login, license, ct);
                var folder = list.RootFolderId ?? throw new WorkflowException($"{env.Title}: в базе нет папки расширений.");
                await _modules.CreateAsync(env.PilotUrl, login, license, folder, package, reason, ct);
                message = $"{env.Title}: {tool.Title} {package.Version} установлен, назначьте его пользователям в Pilot-myAdmin.";
            }
            job.Add("ok", message);
            Finish(job, null);
            return message;
        }
        catch (Exception ex) when (ex is WorkflowException or PilotGatewayException)
        {
            Finish(job, ex.Message);
            throw;
        }
    }

    private void Finish(PublishJob job, string? error)
    {
        job.Update(j =>
        {
            j.Status = error is null ? JobStatus.Succeeded : JobStatus.Failed;
            j.Error = error;
            j.FinishedAt = DateTimeOffset.UtcNow;
        });
        if (error is not null)
            job.Add("error", error);
        _jobs.Persist();
    }
}

/// <summary>
/// Редиректы для клиента PilotOpenTools: файл релиза github.com отдаёт через редирект на хранилище GitHub.
/// Переходим не больше трёх раз и только на github.com и *.githubusercontent.com по https.
/// </summary>
public sealed class GitHubRedirectHandler : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var response = await base.SendAsync(request, ct);
        for (var hop = 0; hop < 3 && (int)response.StatusCode is 301 or 302 or 303 or 307 or 308; hop++)
        {
            var location = response.Headers.Location;
            if (location is null)
                return response;
            var target = location.IsAbsoluteUri ? location : new Uri(request.RequestUri!, location);
            if (!OpenToolsReleases.IsAllowedRedirect(target))
                throw new HttpRequestException($"GitHub перенаправил на неразрешённый адрес {target.Host}");
            response.Dispose();
            // Не освобождаем: ответ ссылается на запрос (RequestMessage.RequestUri - итоговый адрес после редиректов).
            var next = new HttpRequestMessage(HttpMethod.Get, target);
            foreach (var accept in request.Headers.Accept)
                next.Headers.Accept.Add(accept);
            response = await base.SendAsync(next, ct);
        }
        return response;
    }
}
