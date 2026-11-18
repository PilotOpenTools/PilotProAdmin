using System.Net;
using System.Text;
using System.Text.Json;
using ProAdmin.GitLab;
using ProAdmin.Infrastructure;
using ProAdmin.Jobs;
using ProAdmin.Modules;
using ProAdmin.Settings;
using Microsoft.Extensions.Options;

namespace ProAdmin.Tests;

/// <summary>Ожидание pipeline сборки модуля на фейковом GitLab.</summary>
public class ModuleArtifactsTests
{
    private const string Project = "client-modules/mod";
    private const string Sha = "abc123abc123";

    /// <summary>
    /// Установщик правит nuget.config коммитом с [skip ci]: у головы main только pipeline «пропущен».
    /// Это не провал сборки: админка один раз запускает pipeline через API (он [skip ci] не учитывает) и ждёт уже его.
    /// </summary>
    [Fact]
    public async Task Skipped_pipeline_of_skip_ci_commit_triggers_a_new_one()
    {
        var gitlab = new FakeGitLab();
        var ex = await Assert.ThrowsAsync<WorkflowException>(() => BuildAsync(gitlab));

        Assert.Equal(1, gitlab.Created);
        // Дальше нет job сборки - значит, ожидание закончилось на новом успешном pipeline, а не на пропущенном.
        Assert.Contains("pipeline #400", ex.Message);
        Assert.DoesNotContain("Сборка не прошла", ex.Message);
    }

    [Fact]
    public async Task Skipped_pipeline_after_the_retry_is_a_failure()
    {
        var gitlab = new FakeGitLab { RetryStatus = "skipped" };
        var ex = await Assert.ThrowsAsync<WorkflowException>(() => BuildAsync(gitlab));

        Assert.Equal(1, gitlab.Created);
        Assert.Contains("Сборка не прошла: pipeline #400", ex.Message);
    }

    private static Task<ModulePackageInfo> BuildAsync(FakeGitLab gitlab)
    {
        var network = new NetworkAddressMapper(null);
        var auth = new GitLabAuthService(new NoFactory(), network, Options.Create(new AppOptions()));
        var token = new GitLabToken { AccessToken = "pat", BaseUrl = "http://gitlab.test" };
        var git = new GitLabClient(new HttpClient(gitlab), auth, network, token);
        var settings = new AdminSettings();
        settings.Normalize();
        return new ModuleArtifacts(git, Project, settings, new PublishJob { PilotUser = "tester" }).BuildAsync("main", Sha, CancellationToken.None);
    }

    private sealed class NoFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new InvalidOperationException("не нужен для PAT");
    }

    private sealed class FakeGitLab : HttpMessageHandler
    {
        public string RetryStatus { get; init; } = "success";
        public int Created { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = Uri.UnescapeDataString(request.RequestUri!.AbsolutePath).Replace("/api/v4/projects/" + Project, "");
            var method = request.Method.Method;

            if (method == "GET" && path == "/pipelines")
            {
                var list = new List<object>();
                if (Created > 0)
                    list.Add(new { id = 400, status = RetryStatus, @ref = "main", sha = Sha, web_url = "http://gitlab.test/p/400" });
                list.Add(new { id = 346, status = "skipped", @ref = "main", sha = Sha, web_url = "http://gitlab.test/p/346" });
                return Task.FromResult(Json(HttpStatusCode.OK, list));
            }
            if (method == "POST" && path == "/pipeline")
            {
                Created++;
                return Task.FromResult(Json(HttpStatusCode.Created, new { id = 400, status = "created", @ref = "main", sha = Sha, web_url = "http://gitlab.test/p/400" }));
            }
            if (method == "GET" && path.StartsWith("/pipelines/") && path.EndsWith("/jobs"))
                return Task.FromResult(Json(HttpStatusCode.OK, Array.Empty<object>()));
            if (method == "GET" && path.StartsWith("/pipelines/"))
                return Task.FromResult(Json(HttpStatusCode.OK, Array.Empty<object>()));
            return Task.FromResult(Json(HttpStatusCode.NotImplemented, new { message = $"не ожидался {method} {path}" }));
        }

        private static HttpResponseMessage Json(HttpStatusCode code, object value) =>
            new(code) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    }
}
