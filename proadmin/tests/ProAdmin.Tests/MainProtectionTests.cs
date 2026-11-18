using System.Net;
using System.Text;
using System.Text.Json;
using ProAdmin.Automations;
using ProAdmin.GitLab;
using ProAdmin.Infrastructure;
using ProAdmin.Reports;
using ProAdmin.Settings;
using Microsoft.Extensions.Options;

namespace ProAdmin.Tests;

/// <summary>Защита main у новых репозиториев автоматизаций и отчётов и прямой коммит в защищённый main.</summary>
public class MainProtectionTests
{
    private const string Project = "reports/r1";

    [Fact]
    public async Task Automation_repository_gets_merge_gate_and_protected_main_after_its_commits()
    {
        var gitlab = new FakeGitLab();
        var logs = new List<string>();
        await AutomationRepositories.CreateAsync(Client(gitlab), "Auto", 7, "d", null, null, null, CancellationToken.None, (l, _) => logs.Add(l));

        Assert.True(gitlab.ProjectBody.GetProperty("only_allow_merge_if_pipeline_succeeds").GetBoolean());
        Assert.Equal(new[] { "ok" }, logs);
        AssertMainProtectedLast(gitlab);
    }

    [Fact]
    public async Task Report_repository_gets_protected_main_after_its_commits()
    {
        var gitlab = new FakeGitLab();
        await ReportRepositories.CreateAsync(Client(gitlab), "Rep", 7, "d", null, null, null, CancellationToken.None);

        AssertMainProtectedLast(gitlab);
    }

    [Fact]
    public async Task Already_protected_main_is_not_an_error_and_not_logged()
    {
        var gitlab = new FakeGitLab { ProtectStatus = HttpStatusCode.Conflict };
        var logs = new List<string>();
        await AutomationRepositories.ProtectMainAsync(Client(gitlab), Project, (l, _) => logs.Add(l), CancellationToken.None);

        Assert.Empty(logs);
    }

    [Fact]
    public async Task Protection_failure_is_a_warning_with_log_and_an_exception_without()
    {
        var gitlab = new FakeGitLab { ProtectStatus = HttpStatusCode.Forbidden };
        var logs = new List<string>();
        await AutomationRepositories.ProtectMainAsync(Client(gitlab), Project, (l, _) => logs.Add(l), CancellationToken.None);
        Assert.Equal(new[] { "warn" }, logs);

        await Assert.ThrowsAsync<GitLabApiException>(() => AutomationRepositories.ProtectMainAsync(Client(gitlab), Project, null, CancellationToken.None));
    }

    [Fact]
    public async Task Commit_to_open_main_does_not_touch_protection()
    {
        var gitlab = new FakeGitLab();
        await AutomationRepositories.CommitToMainAsync(Client(gitlab), Project, "m", new[] { GitLabCommitAction.Create("a", "b") }, CancellationToken.None);

        Assert.Equal(new[] { "POST commits" }, gitlab.Calls);
    }

    [Fact]
    public async Task Commit_to_protected_main_lifts_the_rule_for_the_commit_and_puts_it_back()
    {
        var gitlab = new FakeGitLab { CommitFailures = 1 };
        await AutomationRepositories.CommitToMainAsync(Client(gitlab), Project, "m", new[] { GitLabCommitAction.Create("a", "b") }, CancellationToken.None);

        Assert.Equal(new[] { "POST commits", "DELETE protected_branches/main", "POST commits", "POST protected_branches" }, gitlab.Calls);
        Assert.Equal(0, gitlab.ProtectBody.GetProperty("push_access_level").GetInt32());
        Assert.Equal(40, gitlab.ProtectBody.GetProperty("merge_access_level").GetInt32());
    }

    [Fact]
    public async Task Failed_commit_still_puts_the_rule_back()
    {
        var gitlab = new FakeGitLab { CommitFailures = 2 };
        await Assert.ThrowsAsync<GitLabApiException>(() =>
            AutomationRepositories.CommitToMainAsync(Client(gitlab), Project, "m", new[] { GitLabCommitAction.Create("a", "b") }, CancellationToken.None));

        Assert.Equal("POST protected_branches", gitlab.Calls[^1]);
    }

    [Fact]
    public async Task Bad_request_not_allowed_to_push_is_handled_like_forbidden()
    {
        var gitlab = new FakeGitLab { CommitFailures = 1, CommitFailureStatus = HttpStatusCode.BadRequest };
        await AutomationRepositories.CommitToMainAsync(Client(gitlab), Project, "m", new[] { GitLabCommitAction.Create("a", "b") }, CancellationToken.None);

        Assert.Equal(new[] { "POST commits", "DELETE protected_branches/main", "POST commits", "POST protected_branches" }, gitlab.Calls);
    }

    [Fact]
    public async Task Unprotect_denied_reports_the_original_push_refusal()
    {
        var gitlab = new FakeGitLab { CommitFailures = 1, CommitFailureStatus = HttpStatusCode.BadRequest, UnprotectStatus = HttpStatusCode.Forbidden };
        var ex = await Assert.ThrowsAsync<GitLabApiException>(() =>
            AutomationRepositories.CommitToMainAsync(Client(gitlab), Project, "m", new[] { GitLabCommitAction.Create("a", "b") }, CancellationToken.None));

        Assert.Contains("not allowed to push", ex.Message);
        Assert.DoesNotContain("POST protected_branches", gitlab.Calls);
    }

    [Fact]
    public async Task Unprotect_of_missing_rule_is_not_an_error()
    {
        var gitlab = new FakeGitLab { UnprotectStatus = HttpStatusCode.NotFound };
        await Client(gitlab).UnprotectBranchAsync(Project, "main", CancellationToken.None);

        Assert.Equal(new[] { "DELETE protected_branches/main" }, gitlab.Calls);
    }

    [Fact]
    public async Task Failed_reprotect_after_a_successful_commit_is_reported()
    {
        var gitlab = new FakeGitLab { CommitFailures = 1, ProtectStatus = HttpStatusCode.Forbidden };
        await Assert.ThrowsAsync<GitLabApiException>(() =>
            AutomationRepositories.CommitToMainAsync(Client(gitlab), Project, "m", new[] { GitLabCommitAction.Create("a", "b") }, CancellationToken.None));

        Assert.Equal(2, gitlab.Calls.Count(c => c == "POST commits"));
    }

    [Fact]
    public async Task Failed_reprotect_does_not_hide_the_commit_error()
    {
        var gitlab = new FakeGitLab { CommitFailures = 2, CommitFailureStatus = HttpStatusCode.BadRequest, ProtectStatus = HttpStatusCode.Forbidden };
        var ex = await Assert.ThrowsAsync<GitLabApiException>(() =>
            AutomationRepositories.CommitToMainAsync(Client(gitlab), Project, "m", new[] { GitLabCommitAction.Create("a", "b") }, CancellationToken.None));

        Assert.Contains("not allowed to push", ex.Message);
        Assert.Contains("защиту main вернуть тоже не удалось", ex.Message);
    }

    [Theory]
    [InlineData(typeof(HttpRequestException))]
    [InlineData(typeof(OperationCanceledException))]
    public async Task Network_error_or_cancellation_after_unprotect_still_puts_the_rule_back(Type failure)
    {
        var gitlab = new FakeGitLab { CommitFailures = 1, ThrowOnSecondCommit = (Exception)Activator.CreateInstance(failure)! };
        await Assert.ThrowsAnyAsync<Exception>(() =>
            AutomationRepositories.CommitToMainAsync(Client(gitlab), Project, "m", new[] { GitLabCommitAction.Create("a", "b") }, CancellationToken.None));

        Assert.Equal(new[] { "POST commits", "DELETE protected_branches/main", "POST commits", "POST protected_branches" }, gitlab.Calls);
        Assert.Equal(0, gitlab.ProtectBody.GetProperty("push_access_level").GetInt32());
    }

    [Fact]
    public async Task Reprotect_is_not_cancelled_with_the_request()
    {
        using var cts = new CancellationTokenSource();
        var gitlab = new FakeGitLab { CommitFailures = 1, ThrowOnSecondCommit = new OperationCanceledException(cts.Token) };
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            AutomationRepositories.CommitToMainAsync(Client(gitlab), Project, "m", new[] { GitLabCommitAction.Create("a", "b") }, CancellationToken.None));

        Assert.Equal("POST protected_branches", gitlab.Calls[^1]);
    }

    private static void AssertMainProtectedLast(FakeGitLab gitlab)
    {
        Assert.Equal("POST protected_branches", gitlab.Calls[^1]);
        Assert.Equal("main", gitlab.ProtectBody.GetProperty("name").GetString());
        Assert.Equal(0, gitlab.ProtectBody.GetProperty("push_access_level").GetInt32());
        Assert.Equal(40, gitlab.ProtectBody.GetProperty("merge_access_level").GetInt32());
        Assert.Single(gitlab.Calls, c => c == "POST protected_branches");
    }

    private static GitLabClient Client(FakeGitLab gitlab)
    {
        var network = new NetworkAddressMapper(null);
        var auth = new GitLabAuthService(new NoFactory(), network, Options.Create(new AppOptions()));
        return new GitLabClient(new HttpClient(gitlab), auth, network, new GitLabToken { AccessToken = "pat", BaseUrl = "http://gitlab.test" });
    }

    private sealed class NoFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new InvalidOperationException("не нужен для PAT");
    }

    private sealed class FakeGitLab : HttpMessageHandler
    {
        public HttpStatusCode ProtectStatus { get; init; } = HttpStatusCode.Created;
        public int CommitFailures { get; set; }
        /// <summary>Исключение вместо ответа на второй коммит (после снятия защиты): сеть, отмена.</summary>
        public Exception? ThrowOnSecondCommit { get; init; }
        private int _commits;
        public HttpStatusCode CommitFailureStatus { get; init; } = HttpStatusCode.Forbidden;
        public HttpStatusCode UnprotectStatus { get; init; } = HttpStatusCode.NoContent;
        public List<string> Calls { get; } = new();
        public JsonElement ProjectBody { get; private set; }
        public JsonElement ProtectBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = Uri.UnescapeDataString(request.RequestUri!.AbsolutePath).Replace("/api/v4/", "");
            var body = request.Content is null ? default : JsonDocument.Parse(await request.Content.ReadAsStringAsync(ct)).RootElement.Clone();
            var method = request.Method.Method;
            var kind = path switch
            {
                "projects" => "projects",
                _ when path.EndsWith("/repository/commits") => "commits",
                _ when path.EndsWith("/repository/branches") => "branches",
                _ => path[path.IndexOf("protected_branches", StringComparison.Ordinal)..],
            };
            Calls.Add($"{method} {kind}");

            if (method == "POST" && kind == "projects")
            {
                ProjectBody = body;
                return Json(HttpStatusCode.Created, new { id = 1, name = "n", path = "p", path_with_namespace = Project, web_url = "http://gitlab.test/p" });
            }
            if (method == "POST" && kind == "commits")
            {
                if (++_commits == 2 && ThrowOnSecondCommit is not null)
                    throw ThrowOnSecondCommit;
                if (CommitFailures > 0)
                {
                    CommitFailures--;
                    return Json(CommitFailureStatus, new { message = "You are not allowed to push into this branch" });
                }
                return Json(HttpStatusCode.Created, new { id = "abc", short_id = "abc", web_url = "http://gitlab.test/c" });
            }
            if (method == "POST" && kind == "branches")
                return Json(HttpStatusCode.Created, new { name = "dev" });
            if (method == "POST" && kind == "protected_branches")
            {
                ProtectBody = body;
                return Json(ProtectStatus, new { message = "x" });
            }
            if (method == "DELETE" && kind.StartsWith("protected_branches/"))
                return Json(UnprotectStatus, new { message = "You are not allowed to push into this branch" });
            return Json(HttpStatusCode.NotImplemented, new { message = $"не ожидался {method} {path}" });
        }

        private static HttpResponseMessage Json(HttpStatusCode code, object value) =>
            new(code) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    }
}
