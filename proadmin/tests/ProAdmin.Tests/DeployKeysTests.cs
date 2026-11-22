using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Options;
using ProAdmin.GitLab;
using ProAdmin.Infrastructure;
using ProAdmin.Modules;
using Xunit;

namespace ProAdmin.Tests;

/// <summary>
/// Ключи выкладки в группе модулей, созданной после установки: админка сама задаёт недостающие DEPLOY_KEY_*,
/// иначе deploy_test нового модуля падает «Не задана переменная DEPLOY_KEY_TEST».
/// </summary>
public class DeployKeysTests
{
    [Fact]
    public async Task Missing_group_variables_are_created_from_admin_keys()
    {
        var gitlab = new FakeGitLab { Existing = { "DEPLOY_KEY_STAGE" } };
        var keys = new CiDeployOptions { Keys = { ["test"] = "testkey1234567890", ["stage"] = "stagekey123456789", ["prod"] = "prodkey1234567890" } };
        var log = new List<(string Level, string Text)>();

        await ModuleCreation.EnsureDeployKeysAsync(Client(gitlab), "client", keys, (l, t, _) => log.Add((l, t)), CancellationToken.None);

        Assert.Equal(new[] { "DEPLOY_KEY_TEST", "DEPLOY_KEY_PROD" }, gitlab.Created.Select(c => c.GetProperty("key").GetString()));
        var test = gitlab.Created[0];
        Assert.Equal("testkey1234567890", test.GetProperty("value").GetString());
        Assert.True(test.GetProperty("masked").GetBoolean());
        Assert.False(test.GetProperty("protected").GetBoolean());
        Assert.True(gitlab.Created[1].GetProperty("protected").GetBoolean());
        Assert.Equal("groups/client/variables", gitlab.PostPath);
        Assert.Contains(log, e => e.Level == "ok" && e.Text.Contains("DEPLOY_KEY_TEST, DEPLOY_KEY_PROD"));
        Assert.DoesNotContain(log, e => e.Text.Contains("testkey1234567890"));
    }

    [Fact]
    public async Task No_admin_key_and_api_error_are_warnings()
    {
        var gitlab = new FakeGitLab { PostStatus = HttpStatusCode.Forbidden };
        var keys = new CiDeployOptions { Keys = { ["test"] = "testkey1234567890" } };
        var log = new List<(string Level, string Text)>();

        await ModuleCreation.EnsureDeployKeysAsync(Client(gitlab), "client", keys, (l, t, _) => log.Add((l, t)), CancellationToken.None);

        Assert.Contains(log, e => e.Level == "warn" && e.Text.Contains("DEPLOY_KEY_TEST") && e.Text.Contains("вручную"));
        Assert.Contains(log, e => e.Level == "warn" && e.Text.Contains("DEPLOY_KEY_STAGE, DEPLOY_KEY_PROD"));
        Assert.DoesNotContain(log, e => e.Level == "ok");
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
        public HashSet<string> Existing { get; } = new();
        public HttpStatusCode PostStatus { get; init; } = HttpStatusCode.Created;
        public List<JsonElement> Created { get; } = new();
        public string? PostPath { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = Uri.UnescapeDataString(request.RequestUri!.AbsolutePath).Replace("/api/v4/", "").TrimStart('/');
            if (request.Method == HttpMethod.Get)
                return new HttpResponseMessage(Existing.Contains(path.Split('/')[^1]) ? HttpStatusCode.OK : HttpStatusCode.NotFound)
                    { Content = new StringContent("{}") };
            PostPath = path;
            if (PostStatus != HttpStatusCode.Created)
                return new HttpResponseMessage(PostStatus) { Content = new StringContent("{\"message\":\"403 Forbidden\"}") };
            Created.Add(JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct)).RootElement.Clone());
            return new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent("{}") };
        }
    }
}
