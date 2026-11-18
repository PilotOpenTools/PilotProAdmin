using System.Net;
using System.Text.Json;
using ProAdmin.Infrastructure;

namespace ProAdmin.Modules;

/// <summary>Задание GitLab CI, подтверждённое самим GitLab по его job-токену. Только эти значения идут в журнал и контекст выкладки.</summary>
public sealed record CiJob(string Ref, string Sha, long PipelineId, long ProjectId, string? PipelineUrl, string? User,
    string JobUrl, string ProjectPath, string ProjectUrl, bool IsTag = false);

/// <summary>
/// Проверка <c>$CI_JOB_TOKEN</c>: GET {GitLab}/api/v4/job с заголовком JOB-TOKEN. GitLab отвечает только для работающего
/// задания и описывает его: ветка, коммит, pipeline, проект, пользователь. Любая ошибка или не-200 - токен не принят.
/// </summary>
/// <param name="GitLabUnavailable">true - GitLab не ответил или ответил ошибкой сервера: это не вина клиента и в счётчик перебора не идёт.</param>
public sealed record CiJobCheck(CiJob? Job, bool GitLabUnavailable = false);

public sealed class CiJobVerifier(IHttpClientFactory http, NetworkAddressMapper network, ILogger<CiJobVerifier> logger)
{
    public const string ClientName = "gitlab-ci";

    public async Task<CiJobCheck> VerifyAsync(string gitLabBaseUrl, string jobToken, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, network.ToInternal(gitLabBaseUrl).TrimEnd('/') + "/api/v4/job");
            request.Headers.TryAddWithoutValidation("JOB-TOKEN", jobToken);
            using var response = await http.CreateClient(ClientName).SendAsync(request, ct);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                logger.LogWarning("GitLab не принял job-токен выкладки: HTTP {Status}", (int)response.StatusCode);
                return new CiJobCheck(null, GitLabUnavailable: (int)response.StatusCode >= 500 || response.StatusCode == HttpStatusCode.TooManyRequests);
            }
            return new CiJobCheck(Parse(await response.Content.ReadAsStringAsync(ct), gitLabBaseUrl));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException && !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Не удалось проверить job-токен выкладки в GitLab");
            return new CiJobCheck(null, GitLabUnavailable: true);
        }
    }

    /// <summary>Разбор ответа <c>/api/v4/job</c>; null - не хватает полей или адрес задания не вида {base}/{проект}/-/jobs/{id}.</summary>
    public static CiJob? Parse(string json, string gitLabBaseUrl)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            return null;

        var reference = Str(root, "ref");
        var jobUrl = Str(root, "web_url");
        var sha = root.TryGetProperty("commit", out var commit) ? Str(commit, "id") : null;
        if (!root.TryGetProperty("pipeline", out var pipeline) || pipeline.ValueKind != JsonValueKind.Object
            || !pipeline.TryGetProperty("id", out var pipelineId) || !pipelineId.TryGetInt64(out var pid)
            || !pipeline.TryGetProperty("project_id", out var projectId) || !projectId.TryGetInt64(out var projId)
            || string.IsNullOrEmpty(reference) || string.IsNullOrEmpty(sha) || string.IsNullOrEmpty(jobUrl))
            return null;

        var path = ProjectPathOf(jobUrl, gitLabBaseUrl);
        if (path is null)
            return null;
        var root2 = jobUrl[..jobUrl.IndexOf("/-/jobs/", StringComparison.Ordinal)];
        var user = root.TryGetProperty("user", out var u) && u.ValueKind == JsonValueKind.Object ? Str(u, "username") : null;
        var isTag = root.TryGetProperty("tag", out var tag) && tag.ValueKind == JsonValueKind.True;
        return new CiJob(reference, sha, pid, projId, Str(pipeline, "web_url"), user, jobUrl, path, root2, isTag);
    }

    /// <summary>Путь проекта из адреса задания {base}/{project_path}/-/jobs/{id}; null - адрес другого вида.</summary>
    public static string? ProjectPathOf(string jobUrl, string gitLabBaseUrl)
    {
        if (!Uri.TryCreate(jobUrl, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return null;
        var path = Uri.UnescapeDataString(uri.AbsolutePath);
        var cut = path.IndexOf("/-/jobs/", StringComparison.Ordinal);
        if (cut < 0)
            return null;
        path = path[..cut].Trim('/');
        // GitLab может стоять под относительным корнем (http://host/gitlab): он входит в базу, но не в путь проекта.
        if (Uri.TryCreate(gitLabBaseUrl, UriKind.Absolute, out var baseUri))
        {
            var prefix = baseUri.AbsolutePath.Trim('/');
            if (prefix.Length > 0 && path.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase))
                path = path[(prefix.Length + 1)..];
        }
        return path.Length == 0 ? null : path;
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
