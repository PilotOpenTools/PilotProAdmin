using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using ProAdmin.Infrastructure;

namespace ProAdmin.GitLab;

/// <summary>Клиент GitLab REST API v4 от имени пользователя (его OAuth-токен или PAT).</summary>
public sealed class GitLabClient
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;
    private readonly GitLabAuthService _auth;
    private readonly GitLabToken _token;
    private readonly string _apiBase;

    public GitLabClient(HttpClient http, GitLabAuthService auth, NetworkAddressMapper network, GitLabToken token)
    {
        _http = http;
        _auth = auth;
        _token = token;
        _apiBase = network.ToInternal(token.BaseUrl).TrimEnd('/') + "/api/v4/";
    }

    public Task<GitLabUser> GetCurrentUserAsync(CancellationToken ct) =>
        GetAsync<GitLabUser>("user", ct);

    public Task<GitLabProject?> FindProjectAsync(string path, CancellationToken ct) =>
        GetOrNullAsync<GitLabProject>($"projects/{E(path)}", ct);

    public Task<GitLabGroup?> FindGroupAsync(string groupPath, CancellationToken ct) =>
        GetOrNullAsync<GitLabGroup>($"groups/{E(groupPath)}?with_projects=false", ct);

    /// <summary>Участник группы с учётом унаследованного членства. null - пользователь не участник.</summary>
    public Task<GitLabMember?> FindGroupMemberAsync(string groupPath, long userId, CancellationToken ct) =>
        GetOrNullAsync<GitLabMember>($"groups/{E(groupPath)}/members/all/{userId}", ct);

    /// <summary>Все проекты группы и её подгрупп. null - группы нет.</summary>
    public Task<List<GitLabProject>?> ListGroupProjectsAsync(string groupPath, string? search, CancellationToken ct) =>
        GetPagedOrNullAsync<GitLabProject>(
            $"groups/{E(groupPath)}/projects?include_subgroups=true&archived=false&order_by=path&sort=asc" +
            (string.IsNullOrEmpty(search) ? "" : $"&search={E(search)}"), ct);

    /// <summary>Все подгруппы (на любой глубине). null - группы нет.</summary>
    public Task<List<GitLabGroup>?> ListDescendantGroupsAsync(string groupPath, CancellationToken ct) =>
        GetPagedOrNullAsync<GitLabGroup>($"groups/{E(groupPath)}/descendant_groups?order_by=path&sort=asc", ct);

    public Task<GitLabProject> CreateProjectAsync(string name, string path, long namespaceId, string description, string defaultBranch, CancellationToken ct, string? ciConfigPath = null) =>
        SendJsonAsync<GitLabProject>(HttpMethod.Post, "projects", new
        {
            name,
            path,
            namespace_id = namespaceId,
            description,
            default_branch = defaultBranch,
            initialize_with_readme = false,
            ci_config_path = ciConfigPath,
            only_allow_merge_if_pipeline_succeeds = true, // «Pipelines must succeed»: merge только после успешного pipeline
        }, ct);

    public Task<GitLabBranch> CreateBranchAsync(string project, string branch, string reference, CancellationToken ct) =>
        SendJsonAsync<GitLabBranch>(HttpMethod.Post, $"projects/{E(project)}/repository/branches", new { branch, @ref = reference }, ct);

    public Task<GitLabBranch?> FindBranchAsync(string project, string branch, CancellationToken ct) =>
        GetOrNullAsync<GitLabBranch>($"projects/{E(project)}/repository/branches/{E(branch)}", ct);

    public Task<GitLabFile?> FindFileAsync(string project, string path, string reference, CancellationToken ct) =>
        GetOrNullAsync<GitLabFile>($"projects/{E(project)}/repository/files/{E(path)}?ref={E(reference)}", ct);

    /// <summary>Полное дерево репозитория. null - нет такой ветки (или репозиторий пуст).</summary>
    public Task<List<GitLabTreeItem>?> ListTreeAsync(string project, string reference, CancellationToken ct) =>
        GetPagedOrNullAsync<GitLabTreeItem>($"projects/{E(project)}/repository/tree?ref={E(reference)}&recursive=true", ct);

    private async Task<List<T>?> GetPagedOrNullAsync<T>(string relative, CancellationToken ct)
    {
        var result = new List<T>();
        for (var page = 1; page <= 100; page++)
        {
            using var response = await SendAsync(HttpMethod.Get, $"{relative}&per_page=100&page={page}", null, ct);
            if (response.StatusCode == HttpStatusCode.NotFound && page == 1)
                return null;
            await EnsureSuccessAsync(response, ct);

            var items = await response.Content.ReadFromJsonAsync<List<T>>(Json, ct) ?? new();
            result.AddRange(items);

            var next = response.Headers.TryGetValues("X-Next-Page", out var values) ? values.FirstOrDefault() : null;
            if (string.IsNullOrEmpty(next) || items.Count == 0)
                break;
        }
        return result;
    }

    public Task<GitLabCommit> CreateCommitAsync(string project, string branch, string? startBranch, string message,
        IReadOnlyCollection<GitLabCommitAction> actions, CancellationToken ct) =>
        SendJsonAsync<GitLabCommit>(HttpMethod.Post, $"projects/{E(project)}/repository/commits", new
        {
            branch,
            start_branch = startBranch,
            commit_message = message,
            actions = actions.Select(a => new
            {
                action = a.Action, file_path = a.FilePath, previous_path = a.PreviousPath, content = a.Content,
                encoding = a.Content is null ? null : a.Encoding ?? "text", last_commit_id = a.LastCommitId,
            }),
        }, ct);

    /// <summary>Коммиты ветки (новые первыми), при <paramref name="since"/> - только после этой даты. Не больше 100.</summary>
    public async Task<List<GitLabCommit>> ListCommitsAsync(string project, string branch, DateTimeOffset? since, CancellationToken ct) =>
        await GetOrNullAsync<List<GitLabCommit>>($"projects/{E(project)}/repository/commits?ref_name={E(branch)}&per_page=100" +
                                                 (since is { } s ? $"&since={E(s.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ"))}" : ""), ct)
        ?? new();

    /// <summary>
    /// Убирает из <paramref name="commits"/> (задачи вне <paramref name="reference"/>) те, что уже попали в неё cherry-pick'ом:
    /// выборочный релиз переносит копии задач, и после выпуска исходные коммиты dev так и остаются «впереди» main.
    /// Копии ищутся среди коммитов ветки не старше самой ранней задачи (не больше 100).
    /// </summary>
    public async Task<List<GitLabCommit>> WithoutPickedIntoAsync(string project, string reference, List<GitLabCommit> commits, CancellationToken ct)
    {
        if (commits.Count == 0)
            return commits;
        var since = commits.Min(c => c.CommittedDate)?.AddMinutes(-1);
        var picked = (await ListCommitsAsync(project, reference, since, ct))
            .SelectMany(c => ProAdmin.Types.TypesRepository.PickedFrom(c.Message))
            .ToList();
        return picked.Count == 0
            ? commits
            : commits.Where(c => !picked.Any(p => c.Id.StartsWith(p, StringComparison.OrdinalIgnoreCase))).ToList();
    }

    /// <summary>Коммиты ветки за период [since, until] (новые первыми). Не больше 100.</summary>
    public async Task<List<GitLabCommit>> ListCommitsAsync(string project, string branch, DateTimeOffset since, DateTimeOffset until, CancellationToken ct) =>
        await GetOrNullAsync<List<GitLabCommit>>($"projects/{E(project)}/repository/commits?ref_name={E(branch)}&per_page=100" +
                                                 $"&since={E(IsoUtc(since))}&until={E(IsoUtc(until))}", ct)
        ?? new();

    /// <summary>Проекты группы и подгрупп с активностью после даты. null - группы нет.</summary>
    public Task<List<GitLabProject>?> ListActiveGroupProjectsAsync(string groupPath, DateTimeOffset after, CancellationToken ct) =>
        GetPagedOrNullAsync<GitLabProject>(
            $"groups/{E(groupPath)}/projects?include_subgroups=true&archived=false&order_by=last_activity_at&sort=desc" +
            $"&last_activity_after={E(IsoUtc(after))}", ct);

    private static string IsoUtc(DateTimeOffset value) => value.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ");

    public Task<GitLabMergeRequest> CreateMergeRequestAsync(string project, string source, string target, string title, string description,
        CancellationToken ct, bool removeSourceBranch = true, bool squash = false) =>
        SendJsonAsync<GitLabMergeRequest>(HttpMethod.Post, $"projects/{E(project)}/merge_requests", new
        {
            source_branch = source,
            target_branch = target,
            title,
            description,
            remove_source_branch = removeSourceBranch,
            squash,
        }, ct);

    /// <summary>Открытые MR проекта; <paramref name="target"/> - только в эту ветку.</summary>
    public Task<List<GitLabMergeRequest>?> ListOpenMergeRequestsToAsync(string project, string? target, CancellationToken ct) =>
        GetPagedOrNullAsync<GitLabMergeRequest>($"projects/{E(project)}/merge_requests?state=opened&order_by=created_at&sort=asc" +
                                                (target is null ? "" : $"&target_branch={E(target)}"), ct);

    /// <summary>Ветки проекта; <paramref name="search"/> - как в GitLab (^release/ - начинается с).</summary>
    public Task<List<GitLabBranch>?> ListBranchesAsync(string project, string? search, CancellationToken ct) =>
        GetPagedOrNullAsync<GitLabBranch>($"projects/{E(project)}/repository/branches?sort=name_asc" +
                                          (string.IsNullOrEmpty(search) ? "" : $"&search={E(search)}"), ct);

    /// <summary>Коммиты, которые есть в <paramref name="to"/> и которых нет в <paramref name="from"/> (старые первыми).</summary>
    public async Task<List<GitLabCommit>> CompareCommitsAsync(string project, string from, string to, CancellationToken ct) =>
        (await GetAsync<GitLabCompare>($"projects/{E(project)}/repository/compare?from={E(from)}&to={E(to)}&straight=false", ct)).Commits ?? new();

    /// <summary>
    /// Файлы, изменённые в <paramref name="to"/> с момента расхождения с <paramref name="from"/> (diff от merge-base).
    /// </summary>
    public async Task<List<GitLabDiff>> CompareDiffsAsync(string project, string from, string to, CancellationToken ct) =>
        (await GetAsync<GitLabCompare>($"projects/{E(project)}/repository/compare?from={E(from)}&to={E(to)}&straight=false", ct)).Diffs ?? new();

    /// <summary>Cherry-pick коммита в ветку. Конфликт - GitLabApiException 400/409.</summary>
    public Task<GitLabCommit> CherryPickAsync(string project, string sha, string branch, CancellationToken ct) =>
        SendJsonAsync<GitLabCommit>(HttpMethod.Post, $"projects/{E(project)}/repository/commits/{E(sha)}/cherry_pick", new { branch }, ct);

    /// <summary>Cherry-pick со своим сообщением коммита (GitLab 14+). Конфликт - GitLabApiException 400/409.</summary>
    public Task<GitLabCommit> CherryPickAsync(string project, string sha, string branch, string message, CancellationToken ct) =>
        SendJsonAsync<GitLabCommit>(HttpMethod.Post, $"projects/{E(project)}/repository/commits/{E(sha)}/cherry_pick", new { branch, message }, ct);

    /// <summary>Изменённые файлы коммита.</summary>
    public async Task<List<GitLabDiff>> GetCommitDiffAsync(string project, string sha, CancellationToken ct) =>
        await GetPagedOrNullAsync<GitLabDiff>($"projects/{E(project)}/repository/commits/{E(sha)}/diff?unidiff=false", ct) ?? new();

    /// <summary>Архив ветки/коммита (zip) - весь репозиторий одним запросом.</summary>
    public async Task<byte[]> DownloadArchiveAsync(string project, string reference, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Get, $"projects/{E(project)}/repository/archive.zip?sha={E(reference)}", null, ct);
        await EnsureSuccessAsync(response, ct);
        return await response.Content.ReadAsByteArrayAsync(ct);
    }

    /// <summary>Защитить ветку: push и merge - с указанных уровней доступа (0 - никто, 30 - Developer, 40 - Maintainer). Уже защищена - false.</summary>
    public async Task<bool> ProtectBranchAsync(string project, string branch, int pushLevel, int mergeLevel, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Post, $"projects/{E(project)}/protected_branches", new
        {
            name = branch,
            push_access_level = pushLevel,
            merge_access_level = mergeLevel,
        }, ct);
        if (response.StatusCode == HttpStatusCode.Conflict)
            return false;
        await EnsureSuccessAsync(response, ct);
        return true;
    }

    /// <summary>Есть ли у группы переменная CI/CD (значение не читается).</summary>
    public async Task<bool> GroupVariableExistsAsync(string group, string key, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Get, $"groups/{E(group)}/variables/{E(key)}", null, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return false;
        await EnsureSuccessAsync(response, ct);
        return true;
    }

    /// <summary>Создать переменную CI/CD группы (raw - без подстановки $…).</summary>
    public async Task CreateGroupVariableAsync(string group, string key, string value, bool masked, bool isProtected, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Post, $"groups/{E(group)}/variables", new Dictionary<string, object>
        {
            ["key"] = key, ["value"] = value, ["masked"] = masked, ["protected"] = isProtected, ["raw"] = true,
        }, ct);
        await EnsureSuccessAsync(response, ct);
    }

    /// <summary>Снять защиту ветки (нет защиты - ничего не делает).</summary>
    public async Task UnprotectBranchAsync(string project, string branch, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Delete, $"projects/{E(project)}/protected_branches/{E(branch)}", null, ct);
        if (response.StatusCode != HttpStatusCode.NotFound)
            await EnsureSuccessAsync(response, ct);
    }

    public Task<GitLabCommit?> FindCommitAsync(string project, string sha, CancellationToken ct) =>
        GetOrNullAsync<GitLabCommit>($"projects/{E(project)}/repository/commits/{E(sha)}", ct);

    /// <summary>Squash-merge с заданным сообщением итогового коммита (одна задача = один коммит).</summary>
    public Task<GitLabMergeRequest> SquashMergeAsync(string project, long iid, string sha, string message, CancellationToken ct, bool removeSourceBranch = true) =>
        SendJsonAsync<GitLabMergeRequest>(HttpMethod.Put, $"projects/{E(project)}/merge_requests/{iid}/merge", new
        {
            sha,
            squash = true,
            squash_commit_message = message,
            should_remove_source_branch = removeSourceBranch,
        }, ct);

    /// <summary>Влить MR автоматически, когда pipeline пройдёт (auto-merge).</summary>
    public Task<GitLabMergeRequest> MergeWhenPipelineSucceedsAsync(string project, long iid, string sha, bool removeSourceBranch, CancellationToken ct) =>
        SendJsonAsync<GitLabMergeRequest>(HttpMethod.Put, $"projects/{E(project)}/merge_requests/{iid}/merge", new
        {
            sha,
            merge_when_pipeline_succeeds = true,
            should_remove_source_branch = removeSourceBranch,
        }, ct);

    public Task<List<GitLabJob>> ListPipelineJobsAsync(string project, long pipelineId, CancellationToken ct) =>
        GetAsync<List<GitLabJob>>($"projects/{E(project)}/pipelines/{pipelineId}/jobs?per_page=100&include_retried=false", ct);

    /// <summary>Архив артефактов job (zip).</summary>
    public async Task<byte[]> DownloadJobArtifactsAsync(string project, long jobId, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Get, $"projects/{E(project)}/jobs/{jobId}/artifacts", null, ct);
        await EnsureSuccessAsync(response, ct);
        return await response.Content.ReadAsByteArrayAsync(ct);
    }

    /// <summary>Запустить pipeline для ветки или тега.</summary>
    public Task<GitLabPipeline> CreatePipelineAsync(string project, string reference, CancellationToken ct) =>
        SendJsonAsync<GitLabPipeline>(HttpMethod.Post, $"projects/{E(project)}/pipeline", new { @ref = reference }, ct);

    /// <summary>Открытые MR из ветки source в target.</summary>
    public Task<List<GitLabMergeRequest>> ListOpenMergeRequestsAsync(string project, string source, string target, CancellationToken ct) =>
        GetAsync<List<GitLabMergeRequest>>($"projects/{E(project)}/merge_requests?state=opened&source_branch={E(source)}&target_branch={E(target)}", ct);

    /// <summary>Влитые MR из ветки (в любую цель): есть - ветка уже влита, а не новая.</summary>
    public Task<List<GitLabMergeRequest>> ListMergedMergeRequestsFromAsync(string project, string source, CancellationToken ct) =>
        GetAsync<List<GitLabMergeRequest>>($"projects/{E(project)}/merge_requests?state=merged&source_branch={E(source)}&per_page=1", ct);

    /// <summary>
    /// Ветка уже влита: из неё есть влитый MR, и ветка с тех пор не двигалась (голова = коммит MR).
    /// Одноимённую ветку, созданную заново от dev, влитой не считаем - она в работе.
    /// </summary>
    public async Task<bool> IsBranchMergedAsync(string project, GitLabBranch branch, CancellationToken ct)
    {
        var mrs = await GetAsync<List<GitLabMergeRequest>>($"projects/{E(project)}/merge_requests?state=merged&source_branch={E(branch.Name)}&per_page=20", ct) ?? new();
        var head = branch.Commit?.Id;
        return head is not null && mrs.Any(m => string.Equals(m.Sha, head, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Последние влитые MR в ветку (свежие сверху).</summary>
    public async Task<List<GitLabMergeRequest>> ListMergedMergeRequestsToAsync(string project, string target, CancellationToken ct) =>
        await GetAsync<List<GitLabMergeRequest>>($"projects/{E(project)}/merge_requests?state=merged&target_branch={E(target)}&order_by=updated_at&per_page=100", ct) ?? new();

    /// <summary>MR, в которых есть коммит (по его исходным коммитам).</summary>
    public async Task<List<GitLabMergeRequest>> ListCommitMergeRequestsAsync(string project, string sha, CancellationToken ct) =>
        await GetAsync<List<GitLabMergeRequest>>($"projects/{E(project)}/repository/commits/{E(sha)}/merge_requests", ct) ?? new();

    /// <summary>
    /// Коммиты ветки, которых нет в <paramref name="target"/> (без merge-коммитов), кроме уже влитых последним MR из неё в target.
    /// Squash merge кладёт в target новый коммит, а исходные коммиты ветки остаются «впереди» - без этой поправки
    /// влитая ветка выглядела бы задачей в работе. Коммиты, запушенные после влития, остаются.
    /// </summary>
    public async Task<List<GitLabCommit>> UnmergedCommitsAsync(string project, string target, string branch, CancellationToken ct)
    {
        var ahead = (await CompareCommitsAsync(project, target, branch, ct)).Where(c => c.ParentIds is not { Count: > 1 }).ToList();
        if (ahead.Count == 0)
            return ahead;
        var merged = await GetAsync<List<GitLabMergeRequest>>(
            $"projects/{E(project)}/merge_requests?state=merged&source_branch={E(branch)}&target_branch={E(target)}&per_page=1", ct);
        if (merged?.FirstOrDefault()?.Sha is not { } sha)
            return ahead;
        try
        {
            var after = (await CompareCommitsAsync(project, sha, branch, ct)).Select(c => c.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            return ahead.Where(c => after.Contains(c.Id)).ToList();
        }
        catch (GitLabApiException)
        {
            return ahead; // коммита MR уже нет (ветку переписали) - считаем по-старому
        }
    }

    /// <summary>Файл ветки байт в байт. null - файла или ветки нет.</summary>
    public async Task<byte[]?> GetRawFileAsync(string project, string path, string reference, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Get, $"projects/{E(project)}/repository/files/{E(path)}/raw?ref={E(reference)}", null, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        await EnsureSuccessAsync(response, ct);
        return await response.Content.ReadAsByteArrayAsync(ct);
    }

    /// <summary>Содержимое git-блоба по его SHA.</summary>
    public async Task<byte[]> GetBlobRawAsync(string project, string sha, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Get, $"projects/{E(project)}/repository/blobs/{E(sha)}/raw", null, ct);
        await EnsureSuccessAsync(response, ct);
        return await response.Content.ReadAsByteArrayAsync(ct);
    }

    public Task<GitLabMergeRequest> GetMergeRequestAsync(string project, long iid, CancellationToken ct) =>
        GetAsync<GitLabMergeRequest>($"projects/{E(project)}/merge_requests/{iid}", ct);

    public async Task<string> GetMergeRefAsync(string project, long iid, CancellationToken ct) =>
        (await GetAsync<MergeRef>($"projects/{E(project)}/merge_requests/{iid}/merge_ref", ct)).CommitId;

    private sealed record MergeRef(string CommitId);

    public Task<GitLabApprovals> GetApprovalsAsync(string project, long iid, CancellationToken ct) =>
        GetAsync<GitLabApprovals>($"projects/{E(project)}/merge_requests/{iid}/approvals", ct);

    public Task<GitLabMergeRequest> MergeAsync(string project, long iid, string sha, CancellationToken ct, bool removeSourceBranch = true) =>
        SendJsonAsync<GitLabMergeRequest>(HttpMethod.Put, $"projects/{E(project)}/merge_requests/{iid}/merge", new
        {
            sha,
            should_remove_source_branch = removeSourceBranch,
        }, ct);

    public async Task CloseMergeRequestAsync(string project, long iid, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Put, $"projects/{E(project)}/merge_requests/{iid}", new { state_event = "close" }, ct);
        await EnsureSuccessAsync(response, ct);
    }

    /// <summary>Меняет только имя проекта: путь (и все ссылки на репозиторий) остаются прежними.</summary>
    public Task<GitLabProject> RenameProjectAsync(string project, string newName, CancellationToken ct) =>
        SendJsonAsync<GitLabProject>(HttpMethod.Put, $"projects/{E(project)}", new { name = newName }, ct);

    public Task<GitLabProject> SetCiConfigPathAsync(string project, string path, CancellationToken ct) =>
        SendJsonAsync<GitLabProject>(HttpMethod.Put, $"projects/{E(project)}", new { ci_config_path = path }, ct);

    public Task<GitLabProject> ArchiveProjectAsync(string project, CancellationToken ct) =>
        SendJsonAsync<GitLabProject>(HttpMethod.Post, $"projects/{E(project)}/archive", new { }, ct);

    public async Task DeleteProjectAsync(string project, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Delete, $"projects/{E(project)}", null, ct);
        await EnsureSuccessAsync(response, ct);
    }

    /// <summary>Теги версий vX.Y.Z (новые первыми).</summary>
    public Task<List<GitLabTag>?> ListVersionTagsAsync(string project, CancellationToken ct) =>
        GetPagedOrNullAsync<GitLabTag>($"projects/{E(project)}/repository/tags?order_by=updated&sort=desc&search=%5Ev", ct);

    public Task<GitLabTag?> FindTagAsync(string project, string tag, CancellationToken ct) =>
        GetOrNullAsync<GitLabTag>($"projects/{E(project)}/repository/tags/{E(tag)}", ct);

    public Task<GitLabTag> CreateTagAsync(string project, string tag, string reference, string message, CancellationToken ct) =>
        SendJsonAsync<GitLabTag>(HttpMethod.Post, $"projects/{E(project)}/repository/tags", new { tag_name = tag, @ref = reference, message }, ct);

    public Task<GitLabRelease> CreateReleaseAsync(string project, string tag, string name, string description, CancellationToken ct) =>
        SendJsonAsync<GitLabRelease>(HttpMethod.Post, $"projects/{E(project)}/releases", new { tag_name = tag, name, description }, ct);

    /// <summary>
    /// Прикрепить файл к релизу: загрузка в uploads проекта и ссылка-ассет на неё (раздел Assets релиза).
    /// Возвращает абсолютный URL файла.
    /// </summary>
    public async Task<string> AddReleaseFileAsync(string project, string tag, string fileName, byte[] content, CancellationToken ct,
        string contentType = "application/zip", string linkType = "package")
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(content);
        file.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        form.Add(file, "file", fileName);

        GitLabUpload upload;
        using (var response = await SendAsync(HttpMethod.Post, $"projects/{E(project)}/uploads", form, ct))
        {
            await EnsureSuccessAsync(response, ct);
            upload = (await response.Content.ReadFromJsonAsync<GitLabUpload>(Json, ct))!;
        }

        var url = _token.BaseUrl.TrimEnd('/') + (upload.FullPath ?? upload.Url);
        using (var response = await SendAsync(HttpMethod.Post, $"projects/{E(project)}/releases/{E(tag)}/assets/links",
                   new { name = fileName, url, link_type = linkType }, ct))
            await EnsureSuccessAsync(response, ct);
        return url;
    }

    private sealed class GitLabUpload
    {
        public string Url { get; set; } = "";
        public string? FullPath { get; set; }
    }

    public async Task DeleteBranchAsync(string project, string branch, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Delete, $"projects/{E(project)}/repository/branches/{E(branch)}", null, ct);
        if (response.StatusCode != HttpStatusCode.NotFound)
            await EnsureSuccessAsync(response, ct);
    }

    public Task<List<GitLabPipeline>> ListMergeRequestPipelinesAsync(string project, long iid, CancellationToken ct) =>
        GetAsync<List<GitLabPipeline>>($"projects/{E(project)}/merge_requests/{iid}/pipelines", ct);

    public Task<List<GitLabPipeline>> ListPipelinesForShaAsync(string project, string sha, CancellationToken ct) =>
        GetAsync<List<GitLabPipeline>>($"projects/{E(project)}/pipelines?sha={E(sha)}&per_page=50", ct);

    /// <summary>Все pipeline, которые проверяют Merge Request: его собственные и pipeline его коммита.</summary>
    public async Task<List<GitLabPipeline>> ListMergeRequestChecksAsync(string project, long iid, string sha, CancellationToken ct) =>
        (await ListMergeRequestPipelinesAsync(project, iid, ct))
        .Concat(await ListPipelinesForShaAsync(project, sha, ct))
        .GroupBy(p => p.Id).Select(g => g.First())
        .ToList();

    /// <summary>Запустить заново упавшие job pipeline.</summary>
    public Task<GitLabPipeline> RetryPipelineAsync(string project, long pipelineId, CancellationToken ct) =>
        SendJsonAsync<GitLabPipeline>(HttpMethod.Post, $"projects/{E(project)}/pipelines/{pipelineId}/retry", new { }, ct);

    public Task<List<GitLabJob>> ListFailedJobsAsync(string project, long pipelineId, CancellationToken ct) =>
        GetAsync<List<GitLabJob>>($"projects/{E(project)}/pipelines/{pipelineId}/jobs?scope[]=failed&per_page=20", ct);

    public async Task<string> GetJobTraceAsync(string project, long jobId, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Get, $"projects/{E(project)}/jobs/{jobId}/trace", null, ct);
        await EnsureSuccessAsync(response, ct);
        return await response.Content.ReadAsStringAsync(ct);
    }

    private static string E(string value) => Uri.EscapeDataString(value);

    /// <summary>Есть ли в реестре пакетов проекта пакет NuGet этой версии (null - проекта нет).</summary>
    public async Task<bool?> HasNuGetPackageAsync(string project, string name, string version, CancellationToken ct)
    {
        var packages = await GetOrNullAsync<List<GitLabPackage>>(
            $"projects/{E(project)}/packages?package_type=nuget&package_name={E(name)}&per_page=100", ct);
        return packages?.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase) && p.Version == version);
    }

    private async Task<T> GetAsync<T>(string relative, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Get, relative, null, ct);
        await EnsureSuccessAsync(response, ct);
        try
        {
            return await response.Content.ReadFromJsonAsync<T>(Json, ct)
                ?? throw new GitLabApiException((int)response.StatusCode, "GitLab API вернул пустой ответ.");
        }
        catch (System.Text.Json.JsonException)
        {
            throw new GitLabApiException((int)response.StatusCode,
                "Ответ GitLab API не является ожидаемым JSON. Проверьте адрес GitLab.");
        }
    }

    private async Task<T?> GetOrNullAsync<T>(string relative, CancellationToken ct) where T : class
    {
        using var response = await SendAsync(HttpMethod.Get, relative, null, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        await EnsureSuccessAsync(response, ct);
        return await response.Content.ReadFromJsonAsync<T>(Json, ct);
    }

    private async Task<T> SendJsonAsync<T>(HttpMethod method, string relative, object body, CancellationToken ct)
    {
        using var response = await SendAsync(method, relative, body, ct);
        await EnsureSuccessAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<T>(Json, ct))!;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string relative, object? body, CancellationToken ct)
    {
        await _auth.EnsureFreshAsync(_token, ct);

        using var request = new HttpRequestMessage(method, _apiBase + relative);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token.AccessToken);
        if (body is HttpContent content)
            request.Content = content;
        else if (body is not null)
            request.Content = JsonContent.Create(body, options: Json);

        try
        {
            return await _http.SendAsync(request, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new GitLabApiException(0, $"GitLab {_token.BaseUrl} недоступен: {ex.Message}");
        }
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode)
            throw await ErrorAsync(response, ct);
    }

    private static async Task<GitLabApiException> ErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var status = (int)response.StatusCode;
        if (status == 401)
            return new GitLabApiException(status, "Токен GitLab недействителен или истёк - переподключите GitLab.");
        if (status == 403)
            return new GitLabApiException(status, "Недостаточно прав в GitLab для этой операции (нужен доступ Developer+ к проекту и право push/merge в ветку).");

        var body = await response.Content.ReadAsStringAsync(ct);
        var message = body;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("message", out var m))
                message = m.ValueKind == JsonValueKind.String ? m.GetString()! : m.GetRawText();
            else if (doc.RootElement.TryGetProperty("error", out var e))
                message = e.ValueKind == JsonValueKind.String ? e.GetString()! : e.GetRawText();
        }
        catch (JsonException)
        {
            // не JSON - оставляем как есть
        }

        if (message.Length > 500)
            message = message[..500] + "…";
        return new GitLabApiException(status, $"GitLab {status}: {message}");
    }
}

public sealed class GitLabClientFactory
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly GitLabAuthService _auth;
    private readonly NetworkAddressMapper _network;

    public GitLabClientFactory(IHttpClientFactory httpFactory, GitLabAuthService auth, NetworkAddressMapper network)
    {
        _httpFactory = httpFactory;
        _auth = auth;
        _network = network;
    }

    public GitLabClient Create(GitLabToken token) =>
        new(_httpFactory.CreateClient("gitlab"), _auth, _network, token);
}
