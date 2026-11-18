using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using ProAdmin.Jobs;

namespace ProAdmin.Types;

public sealed class GitHubTypesOptions
{
    public string? Repository { get; set; }
    public bool IsConfigured => !string.IsNullOrWhiteSpace(Repository);
}

public sealed partial class GitHubTypesClient(HttpClient http)
{
    public const int MaxArchiveBytes = 16 * 1024 * 1024;
    public const string WorkflowFile = "pilot-types.yml";
    public sealed record ApprovedRevision(string Sha, string PullUrl, string RunUrl);

    public static string? RepositoryPath(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var url)
        && url.Scheme == "https" && url.Host == "github.com" && url.IsDefaultPort
        && url.UserInfo.Length == 0 && url.Query.Length == 0 && url.Fragment.Length == 0
        && RepositoryPattern().IsMatch(url.AbsolutePath)
            ? url.AbsolutePath.Trim('/')
            : null;

    public async Task<ApprovedRevision> VerifyAsync(string repo, long pull, string token, CancellationToken ct)
    {
        var repository = await GetAsync(repo, "", token, ct);
        if (!string.Equals(repository.GetProperty("full_name").GetString(), repo, StringComparison.OrdinalIgnoreCase)
            || !repository.TryGetProperty("permissions", out var permissions)
            || !permissions.TryGetProperty("push", out var push) || push.ValueKind != JsonValueKind.True)
            throw new WorkflowException("Для выкладки нужны права записи в указанный репозиторий GitHub.");
        var repoId = repository.GetProperty("id").GetInt64();
        var request = await GetAsync(repo, $"/pulls/{pull}", token, ct);
        var target = request.GetProperty("base");
        if (request.GetProperty("number").GetInt64() != pull || !request.GetProperty("merged").GetBoolean()
            || target.GetProperty("ref").GetString() != TypesBranches.Dev
            || target.GetProperty("repo").GetProperty("id").GetInt64() != repoId)
            throw new WorkflowException("Нужен влитый Pull Request в ветку dev этого репозитория.");
        var sha = request.GetProperty("merge_commit_sha").GetString();
        if (sha is null || !ShaPattern().IsMatch(sha))
            throw new WorkflowException("GitHub не вернул коммит слияния.");
        await CheckHeadAsync(repo, sha, token, ct);

        var runs = await GetAsync(repo, $"/actions/workflows/{WorkflowFile}/runs?branch=dev&event=push&head_sha={sha}&per_page=100", token, ct);
        var run = runs.GetProperty("workflow_runs").EnumerateArray()
            .Where(r => r.GetProperty("head_sha").GetString() == sha
                && r.GetProperty("head_branch").GetString() == TypesBranches.Dev
                && r.GetProperty("event").GetString() == "push"
                && r.GetProperty("repository").GetProperty("id").GetInt64() == repoId
                && r.GetProperty("head_repository").GetProperty("id").GetInt64() == repoId)
            .OrderByDescending(r => r.GetProperty("run_number").GetInt64()).FirstOrDefault();
        if (run.ValueKind != JsonValueKind.Object || run.GetProperty("status").GetString() != "completed"
            || run.GetProperty("conclusion").GetString() != "success")
            throw new WorkflowException($"Для коммита {sha[..8]} нужен успешный запуск {WorkflowFile} после слияния в dev.");

        return new ApprovedRevision(sha, $"https://github.com/{repo}/pull/{pull}",
            $"https://github.com/{repo}/actions/runs/{run.GetProperty("id").GetInt64()}");
    }

    public async Task CheckHeadAsync(string repo, string sha, string token, CancellationToken ct)
    {
        var branch = await GetAsync(repo, "/branches/dev", token, ct);
        if (branch.GetProperty("commit").GetProperty("sha").GetString() != sha)
            throw new WorkflowException("Коммит Pull Request уже не является головой dev. Выберите последний влитый Pull Request.");
    }

    public async Task<TypesSnapshot> ReadSnapshotAsync(string repo, string sha, string token, CancellationToken ct)
    {
        using var request = Request(repo, $"/zipball/{sha}", token);
        using var response = await http.SendAsync(request, ct);
        var redirect = response.Headers.Location;
        if (response.StatusCode != HttpStatusCode.Found || redirect is not { IsAbsoluteUri: true, Scheme: "https", Host: "codeload.github.com", IsDefaultPort: true }
            || redirect.UserInfo.Length != 0)
            throw new WorkflowException("GitHub не выдал ссылку на архив утверждённого коммита.");

        // Ссылка на архив приватного репозитория краткоживущая. Заголовок с PAT уходит только на api.github.com.
        using var download = await http.GetAsync(redirect, ct);
        if (!download.IsSuccessStatusCode)
            throw new WorkflowException($"Не удалось загрузить архив коммита из GitHub (HTTP {(int)download.StatusCode}).");
        return TypesSnapshot.FromArchive(await download.Content.ReadAsByteArrayAsync(ct), MaxArchiveBytes);
    }

    private async Task<JsonElement> GetAsync(string repo, string suffix, string token, CancellationToken ct)
    {
        using var request = Request(repo, suffix, token);
        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            throw new WorkflowException($"GitHub вернул HTTP {(int)response.StatusCode}. Проверьте репозиторий, PR и права токена.");
        using var data = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        return data.RootElement.Clone();
    }

    private static HttpRequestMessage Request(string repo, string suffix, string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{repo}{suffix}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.UserAgent.ParseAdd("Pilot-ProAdmin/1.0");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        return request;
    }

    [GeneratedRegex(@"^/[A-Za-z0-9-]+/[A-Za-z0-9_.-]+/?$")]
    private static partial Regex RepositoryPattern();

    [GeneratedRegex(@"^[0-9a-f]{40}$")]
    private static partial Regex ShaPattern();
}
