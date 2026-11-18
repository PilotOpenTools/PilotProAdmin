using System.Net;
using System.Text.Json;
using ProAdmin.Jobs;
using ProAdmin.Types;

namespace ProAdmin.Tests;

public sealed class GitHubTypesTests
{
    private const string Repo = "owner/pilot-types";
    private const string Sha = "1111111111111111111111111111111111111111";

    [Theory]
    [InlineData("https://github.com/owner/pilot-types", Repo)]
    [InlineData("https://github.com/owner/pilot-types/", Repo)]
    [InlineData("https://github.com.evil.example/owner/pilot-types", null)]
    [InlineData("https://secret@github.com/owner/pilot-types", null)]
    [InlineData("https://github.com/owner/pilot-types/issues", null)]
    public void Only_repository_urls_are_accepted(string url, string? path) =>
        Assert.Equal(path, GitHubTypesClient.RepositoryPath(url));

    [Fact]
    public async Task Accepts_only_the_expected_green_push_run_and_builds_links()
    {
        using var http = new HttpClient(new Fixture());
        var result = await new GitHubTypesClient(http).VerifyAsync(Repo, 10, "fixture_token", TestContext.Current.CancellationToken);
        Assert.Equal(Sha, result.Sha);
        Assert.Equal($"https://github.com/{Repo}/pull/10", result.PullUrl);
        Assert.Equal($"https://github.com/{Repo}/actions/runs/99", result.RunUrl);
    }

    [Theory]
    [InlineData("permission")]
    [InlineData("open")]
    [InlineData("target")]
    [InlineData("head")]
    [InlineData("commit")]
    [InlineData("event")]
    [InlineData("fork")]
    [InlineData("failure")]
    [InlineData("rerun")]
    public async Task Rejects_unapproved_revisions(string condition)
    {
        using var http = new HttpClient(new Fixture(condition));
        await Assert.ThrowsAsync<WorkflowException>(() =>
            new GitHubTypesClient(http).VerifyAsync(Repo, 10, "fixture_token", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Sends_archive_request_without_token_to_codeload()
    {
        var calls = 0;
        using var http = new HttpClient(new Fixture(archive: request =>
        {
            calls++;
            if (request.RequestUri!.Host == "api.github.com")
            {
                Assert.Equal("fixture_token", request.Headers.Authorization?.Parameter);
                var redirect = new HttpResponseMessage(HttpStatusCode.Found);
                redirect.Headers.Location = new Uri("https://codeload.github.com/owner/pilot-types/zip/fixture");
                return redirect;
            }
            Assert.Equal("codeload.github.com", request.RequestUri.Host);
            Assert.Null(request.Headers.Authorization);
            using var buffer = new MemoryStream();
            using (var zip = new System.IO.Compression.ZipArchive(buffer, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
            {
                using var json = new StreamWriter(zip.CreateEntry("root/types/sample.json").Open());
                json.Write("""{"name":"sample","title":"Sample","kind":"User","sort":42}""");
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(buffer.ToArray()) };
        }));
        var result = await new GitHubTypesClient(http).ReadSnapshotAsync(Repo, Sha, "fixture_token", TestContext.Current.CancellationToken);
        Assert.Equal(42, result.Types["sample"].Sort);
        Assert.Equal(2, calls);
    }

    private sealed class Fixture(string condition = "", Func<HttpRequestMessage, HttpResponseMessage>? archive = null) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (archive is not null)
                return Task.FromResult(archive(request));
            Assert.Equal("api.github.com", request.RequestUri!.Host);
            Assert.Equal("fixture_token", request.Headers.Authorization?.Parameter);
            var reply = request.RequestUri.AbsolutePath switch
            {
                "/repos/" + Repo => Json(new { full_name = Repo, id = 7, permissions = new { push = condition != "permission" } }),
                "/repos/" + Repo + "/pulls/10" => Json(new
                {
                    number = 10, merged = condition != "open", merge_commit_sha = Sha,
                    @base = new { @ref = condition == "target" ? "main" : "dev", repo = new { id = 7 } },
                }),
                "/repos/" + Repo + "/branches/dev" => Json(new { commit = new { sha = condition == "head" ? new string('2', 40) : Sha } }),
                "/repos/" + Repo + "/actions/workflows/pilot-types.yml/runs" => Runs(request.RequestUri.Query),
                _ => throw new InvalidOperationException("Unexpected GitHub request."),
            };
            return Task.FromResult(reply);
        }

        private HttpResponseMessage Runs(string query)
        {
            Assert.Contains("head_sha=" + Sha, query);
            var approved = new
            {
                id = 99, run_number = 1, head_branch = "dev", head_sha = condition == "commit" ? new string('2', 40) : Sha,
                @event = condition == "event" ? "pull_request" : "push", repository = new { id = 7 },
                head_repository = new { id = condition == "fork" ? 8 : 7 }, status = "completed",
                conclusion = condition == "failure" ? "failure" : "success",
            };
            var runs = condition == "rerun"
                ? new[] { approved, approved with { id = 100, run_number = 2, status = "in_progress" } }
                : new[] { approved };
            return Json(new { workflow_runs = runs });
        }

        private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(value)),
        };
    }
}
