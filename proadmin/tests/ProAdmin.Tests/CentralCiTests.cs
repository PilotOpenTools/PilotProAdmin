using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using ProAdmin.GitLab;
using ProAdmin.Infrastructure;
using ProAdmin.Jobs;
using ProAdmin.Settings;

namespace ProAdmin.Tests;

/// <summary>Подключение репозитория к общим проверкам перед публикацией.</summary>
public class CentralCiTests
{
    private const string Central = "report.yml@pilot-ci/checks";

    [Fact]
    public async Task Connects_repository_without_ci()
    {
        var gitlab = new FakeGitLab();
        var path = await EnsureAsync(gitlab, current: null, configured: Central);
        Assert.Equal(Central, path);
        Assert.Equal(Central, gitlab.SavedPath);
    }

    [Fact]
    public async Task Keeps_existing_setting()
    {
        var gitlab = new FakeGitLab();
        var path = await EnsureAsync(gitlab, current: "other.yml@group/project", configured: Central);
        Assert.Equal("other.yml@group/project", path);
        Assert.Null(gitlab.SavedPath);
    }

    [Fact]
    public async Task Keeps_own_ci_file_that_includes_central_checks()
    {
        var gitlab = new FakeGitLab { OwnCi = "include:\n  - project: pilot-ci/checks\n    file: report.yml\n" };
        var path = await EnsureAsync(gitlab, current: null, configured: Central);
        Assert.Null(path);
        Assert.Null(gitlab.SavedPath);
    }

    [Fact]
    public async Task Replaces_own_ci_file_without_central_checks()
    {
        // Собственный CI вида «script: true» не должен подменять общие проверки.
        var gitlab = new FakeGitLab { OwnCi = "check:\n  script: 'true'\n" };
        var log = new List<string>();
        var path = await EnsureAsync(gitlab, current: null, configured: Central, log);
        Assert.Equal(Central, path);
        Assert.Equal(Central, gitlab.SavedPath);
        Assert.Contains(log, l => l.StartsWith("warn") && l.Contains("свой .gitlab-ci.yml"));
    }

    [Theory]
    [InlineData("# project: pilot-ci/checks\ncheck:\n  script: 'true'\n")]
    [InlineData("check:\n  script: echo pilot-ci/checks\n")]
    [InlineData("include:\n  - project: pilot-ci/checks-other\n    file: report.yml\n")]
    public async Task Mention_of_central_path_outside_include_does_not_count(string ci)
    {
        var gitlab = new FakeGitLab { OwnCi = ci };
        Assert.Equal(Central, await EnsureAsync(gitlab, current: null, configured: Central));
        Assert.Equal(Central, gitlab.SavedPath);
    }

    [Theory]
    [InlineData("include:\n  - project: 'pilot-ci/checks'\n    file: report.yml\n")]
    [InlineData("include:\n  project: \"pilot-ci/checks\" # общие проверки\n  file: report.yml\n")]
    public async Task Quoted_and_commented_include_counts(string ci)
    {
        var gitlab = new FakeGitLab { OwnCi = ci };
        Assert.Null(await EnsureAsync(gitlab, current: null, configured: Central));
        Assert.Null(gitlab.SavedPath);
    }

    [Fact]
    public async Task Local_config_path_does_not_override_own_ci_file()
    {
        var gitlab = new FakeGitLab { OwnCi = "check:\n  script: 'true'\n" };
        Assert.Null(await EnsureAsync(gitlab, current: null, configured: "ci/report.yml"));
        Assert.Null(gitlab.SavedPath);
    }

    [Fact]
    public async Task Empty_setting_connects_nothing()
    {
        var gitlab = new FakeGitLab();
        Assert.Null(await EnsureAsync(gitlab, current: null, configured: ""));
        Assert.Null(gitlab.SavedPath);
    }

    [Fact]
    public async Task No_rights_is_a_warning_and_leaves_repository_without_ci()
    {
        var gitlab = new FakeGitLab { DenyUpdate = true };
        var log = new List<string>();
        var path = await EnsureAsync(gitlab, current: null, configured: Central, log);
        Assert.Null(path);
        Assert.Single(log, l => l.StartsWith("warn"));
    }

    [Fact]
    public void Publishing_without_ci_is_off_by_default()
    {
        Assert.False(new AdminSettings().AllowPublishWithoutCi);
    }

    private static Task<string?> EnsureAsync(FakeGitLab gitlab, string? current, string? configured, List<string>? log = null)
    {
        var network = new NetworkAddressMapper(null);
        var auth = new GitLabAuthService(new NoFactory(), network, Options.Create(new AppOptions()));
        var token = new GitLabToken { AccessToken = "pat", BaseUrl = "http://gitlab.test" };
        var git = new GitLabClient(new HttpClient(gitlab), auth, network, token);
        return CentralCi.EnsureAsync(git, "reports/x", current, configured, "dev", (level, text) => log?.Add($"{level}: {text}"), CancellationToken.None);
    }

    private sealed class FakeGitLab : HttpMessageHandler
    {
        public string? OwnCi { get; init; }
        public bool DenyUpdate { get; init; }
        public string? SavedPath { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Method == HttpMethod.Get)
                return OwnCi is not null
                    ? Json(HttpStatusCode.OK, new { file_name = ".gitlab-ci.yml", file_path = ".gitlab-ci.yml", encoding = "base64", content = Convert.ToBase64String(Encoding.UTF8.GetBytes(OwnCi)) })
                    : Json(HttpStatusCode.NotFound, new { message = "404 File Not Found" });
            if (DenyUpdate)
                return Json(HttpStatusCode.Forbidden, new { message = "403 Forbidden" });
            var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct)).RootElement;
            SavedPath = body.GetProperty("ci_config_path").GetString();
            return Json(HttpStatusCode.OK, new { id = 1 });
        }

        private static HttpResponseMessage Json(HttpStatusCode code, object body) =>
            new(code) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
    }

    private sealed class NoFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new NotSupportedException();
    }
}
