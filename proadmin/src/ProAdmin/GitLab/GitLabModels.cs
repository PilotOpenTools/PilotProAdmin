using System.Text;

namespace ProAdmin.GitLab;

/// <summary>Адрес проекта/группы GitLab: база сервера + путь (например http://localhost:8080 + automation).</summary>
public sealed record GitLabLocation(string BaseUrl, string Path)
{
    public string WebUrl => $"{BaseUrl}/{Path}";

    public static bool TryParse(string? url, out GitLabLocation? location, out string? error)
    {
        location = null;
        error = null;

        if (string.IsNullOrWhiteSpace(url) ||
            !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            error = "Адрес GitLab должен быть вида http://host:8080/группа/проект.";
            return false;
        }

        var path = Uri.UnescapeDataString(uri.AbsolutePath).Trim('/');
        var dash = path.IndexOf("/-/", StringComparison.Ordinal);
        if (dash >= 0)
            path = path[..dash];
        if (path.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            path = path[..^4];

        if (path.Length == 0)
        {
            error = "В адресе GitLab не указан путь к проекту или группе.";
            return false;
        }

        location = new GitLabLocation($"{uri.Scheme}://{uri.Authority}", path);
        return true;
    }
}

public sealed class GitLabToken
{
    public required string AccessToken { get; set; }
    public string? RefreshToken { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public bool IsOAuth { get; init; }
    public string? RedirectUri { get; init; }

    /// <summary>База GitLab, для которой выдан токен (http://host:port).</summary>
    public required string BaseUrl { get; init; }

    internal SemaphoreSlim RefreshLock { get; } = new(1, 1);
}

public sealed class GitLabUser
{
    public long Id { get; set; }
    public string Username { get; set; } = "";
    public string Name { get; set; } = "";
    public string? AvatarUrl { get; set; }
    public string? WebUrl { get; set; }

    /// <summary>Администратор экземпляра GitLab (поле приходит только самому администратору).</summary>
    public bool IsAdmin { get; set; }
}

public sealed class GitLabMember
{
    public long Id { get; set; }
    public string Username { get; set; } = "";
    public int AccessLevel { get; set; }
}

public sealed class GitLabProject
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public string PathWithNamespace { get; set; } = "";
    public string WebUrl { get; set; } = "";
    public string? Description { get; set; }
    public string? DefaultBranch { get; set; }
    public string? CiConfigPath { get; set; }
    public GitLabNamespace? Namespace { get; set; }
}

public sealed class GitLabNamespace
{
    public long Id { get; set; }
    public string FullPath { get; set; } = "";
}

public sealed class GitLabGroup
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string FullPath { get; set; } = "";
    public string WebUrl { get; set; } = "";
}

public sealed class GitLabTreeItem
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
    public string Path { get; set; } = "";
}

public sealed class GitLabFile
{
    public string FilePath { get; set; } = "";
    public string Content { get; set; } = "";
    public string Encoding { get; set; } = "";
    public string BlobId { get; set; } = "";
    public string LastCommitId { get; set; } = "";

    public string Text
    {
        get
        {
            var text = Encoding == "base64"
                ? System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(Content))
                : Content;
            return text.TrimStart('﻿');
        }
    }
}

public sealed class GitLabBranch
{
    public string Name { get; set; } = "";
    public bool Protected { get; set; }
    public GitLabCommit? Commit { get; set; }
}

public sealed class GitLabCommit
{
    public string Id { get; set; } = "";
    public string ShortId { get; set; } = "";
    public string? Title { get; set; }
    public string? WebUrl { get; set; }
    public DateTimeOffset? CommittedDate { get; set; }
    public string? AuthorName { get; set; }
    public List<string>? ParentIds { get; set; }
    public string? Message { get; set; }
}

public sealed class GitLabCommitAction
{
    public required string Action { get; init; }
    public required string FilePath { get; init; }
    public string? Content { get; init; }
    public string? PreviousPath { get; init; }
    public string? LastCommitId { get; init; }

    /// <summary>"text" (по умолчанию) или "base64" - для файлов, которые должны попасть в репозиторий байт в байт.</summary>
    public string? Encoding { get; init; }

    public static GitLabCommitAction Create(string path, string content) => new() { Action = "create", FilePath = path, Content = content };
    public static GitLabCommitAction Update(string path, string content, string? lastCommitId = null) =>
        new() { Action = "update", FilePath = path, Content = content, LastCommitId = lastCommitId };
    public static GitLabCommitAction Delete(string path, string? lastCommitId = null) =>
        new() { Action = "delete", FilePath = path, LastCommitId = lastCommitId };
    public static GitLabCommitAction CreateBinary(string path, byte[] content) => new() { Action = "create", FilePath = path, Content = Convert.ToBase64String(content), Encoding = "base64" };
    public static GitLabCommitAction UpdateBinary(string path, byte[] content) => new() { Action = "update", FilePath = path, Content = Convert.ToBase64String(content), Encoding = "base64" };
    public static GitLabCommitAction Move(string from, string to, string? content = null) => new() { Action = "move", FilePath = to, PreviousPath = from, Content = content };
}

public sealed class GitLabMergeRequest
{
    public long Id { get; set; }
    public long Iid { get; set; }
    public string State { get; set; } = "";
    public string? Title { get; set; }
    public string? SourceBranch { get; set; }
    public string? TargetBranch { get; set; }
    public bool Draft { get; set; }
    public GitLabUser? Author { get; set; }
    public DateTimeOffset? CreatedAt { get; set; }
    public GitLabPipeline? HeadPipeline { get; set; }
    public string? DetailedMergeStatus { get; set; }
    public string? Sha { get; set; }
    public string? MergeCommitSha { get; set; }
    public string? SquashCommitSha { get; set; }
    public string WebUrl { get; set; } = "";
    public bool HasConflicts { get; set; }
}

/// <summary>Апрувы MR (в GitLab CE необязательные: кнопка Approve есть, правил нет).</summary>
public sealed class GitLabApprovals
{
    public List<GitLabApprover> ApprovedBy { get; set; } = new();

    /// <summary>Одобрившие, кроме автора MR.</summary>
    public List<string> Reviewers(GitLabMergeRequest mr) =>
        ApprovedBy.Select(a => a.User).Where(u => u is not null && u.Id != mr.Author?.Id).Select(u => u!.Username).ToList();
}

public sealed class GitLabApprover
{
    public GitLabUser? User { get; set; }
}

public sealed class GitLabPipeline
{
    public long Id { get; set; }
    public string Status { get; set; } = "";
    public string? Ref { get; set; }
    public string? Sha { get; set; }
    public string? Source { get; set; }
    public string? WebUrl { get; set; }
    public DateTimeOffset? CreatedAt { get; set; }

    public bool IsFinished => Status is "success" or "failed" or "canceled" or "skipped" or "manual";

    /// <summary>Закончился не успехом: с таким pipeline Merge Request не вливается.</summary>
    public static bool Blocks(string? status) => status is "failed" or "canceled" or "skipped" or "manual";

    /// <summary>Такой pipeline можно запустить заново.</summary>
    public bool CanRetry => Status is "failed" or "canceled";
}

/// <summary>Проверки Merge Request: по какому pipeline админка решает, можно ли его влить.</summary>
public static class MergeRequestChecks
{
    /// <summary>По каждому источнику (push / merge_request_event) учитывается только последний pipeline.</summary>
    public static List<GitLabPipeline> Latest(IEnumerable<GitLabPipeline> all) =>
        all.GroupBy(p => p.Source ?? "").Select(g => g.MaxBy(p => p.Id)!).ToList();

    /// <summary>Что показать в списке MR: сначала упавший, затем незавершённый, иначе самый новый.</summary>
    public static GitLabPipeline? Summary(IReadOnlyCollection<GitLabPipeline> latest) =>
        latest.Where(p => GitLabPipeline.Blocks(p.Status)).MaxBy(p => p.Id)
        ?? latest.Where(p => !p.IsFinished).MaxBy(p => p.Id)
        ?? latest.MaxBy(p => p.Id);
}

public sealed class GitLabJob
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string Stage { get; set; } = "";
    public string Status { get; set; } = "";
    public string? WebUrl { get; set; }
    public bool AllowFailure { get; set; }
    public List<GitLabJobArtifact>? Artifacts { get; set; }
    public GitLabJobArtifactsFile? ArtifactsFile { get; set; }
}

public sealed class GitLabJobArtifact
{
    public string FileType { get; set; } = "";
    public long Size { get; set; }
}

public sealed class GitLabJobArtifactsFile
{
    public string Filename { get; set; } = "";
    public long Size { get; set; }
}

/// <summary>Файл в diff коммита.</summary>
public sealed class GitLabDiff
{
    public string? OldPath { get; set; }
    public string? NewPath { get; set; }
    public bool NewFile { get; set; }
    public bool DeletedFile { get; set; }
    public bool RenamedFile { get; set; }
    public string? Diff { get; set; }
}

public sealed class GitLabCompare
{
    public List<GitLabCommit>? Commits { get; set; }
    public List<GitLabDiff>? Diffs { get; set; }
}

internal sealed class GitLabOAuthTokenResponse
{
    public string AccessToken { get; set; } = "";
    public string? RefreshToken { get; set; }
    public int? ExpiresIn { get; set; }
    public long? CreatedAt { get; set; }
}

public sealed class GitLabApiException : Exception
{
    public GitLabApiException(int statusCode, string message) : base(message) => StatusCode = statusCode;

    public int StatusCode { get; }
}

public static class GitLabText
{
    public static string PipelineStatus(string status) => status switch
    {
        "created" => "создан",
        "waiting_for_resource" => "ждёт ресурс",
        "preparing" => "подготовка",
        "pending" => "в очереди",
        "running" => "выполняется",
        "success" => "успешно",
        "failed" => "ошибка",
        "canceled" => "отменён",
        "skipped" => "пропущен",
        "manual" => "ждёт ручного запуска",
        "scheduled" => "запланирован",
        _ => status,
    };

    public static string MergeStatus(string? status) => status switch
    {
        "not_approved" => "нужны апрувы",
        "conflict" => "конфликт с целевой веткой",
        "need_rebase" => "нужен rebase",
        "discussions_not_resolved" => "есть нерешённые обсуждения",
        "draft_status" => "MR в статусе Draft",
        "blocked_status" => "MR заблокирован",
        "ci_must_pass" => "pipeline должен пройти",
        "ci_still_running" => "pipeline ещё выполняется",
        "not_open" => "MR закрыт",
        "requested_changes" => "запрошены изменения",
        "mergeable" => "можно вливать",
        null => "неизвестно",
        _ => status,
    };

    /// <summary>Хвост лога job без ANSI-цветов и служебных маркеров секций GitLab.</summary>
    public static string TraceTail(string trace, int lines)
    {
        var clean = System.Text.RegularExpressions.Regex.Replace(trace, @"\x1B\[[0-9;]*[A-Za-z]|section_(start|end):\d+:[^\r\n]*?\r?(\x1B\[0K)?", "");
        var all = clean.Replace("\r\n", "\n").Split('\n')
            .Select(l => l.Contains('\r') ? l[(l.LastIndexOf('\r') + 1)..] : l)
            .Where(l => l.Trim().Length > 0)
            .ToArray();
        var sb = new StringBuilder();
        foreach (var line in all.Skip(Math.Max(0, all.Length - lines)))
            sb.AppendLine(line);
        return sb.ToString().TrimEnd();
    }
}

public sealed class GitLabTag
{
    public string Name { get; set; } = "";
    public GitLabCommit? Commit { get; set; }
    public string? Message { get; set; }
    public GitLabRelease? Release { get; set; }
}

public sealed class GitLabRelease
{
    public string TagName { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Description { get; set; }
}
