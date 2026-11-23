using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ProAdmin.GitLab;
using ProAdmin.Infrastructure;
using ProAdmin.Sessions;
using ProAdmin.Settings;
using Xunit;

namespace ProAdmin.Tests;

/// <summary>
/// Права администратора GitLab перед административной записью подтверждаются в GitLab заново:
/// роль, сохранённая в сессии при входе, после отзыва роли или токена доступа не даёт.
/// </summary>
public class GitLabAdminCheckTests
{
    private const string GitLabUrl = "http://gitlab.test";

    [Fact]
    public async Task Admin_confirmed_by_gitlab_keeps_write_access()
    {
        var (check, settings, session, _) = Setup("""{"id":7,"username":"root","is_admin":true}""");
        Assert.True(await check.ConfirmAsync(session, settings, CancellationToken.None));
        Assert.True(session.GitLabUser!.IsAdmin);
    }

    [Fact]
    public async Task Revoked_role_denies_write_and_resets_session()
    {
        var (check, settings, session, _) = Setup("""{"id":7,"username":"root","is_admin":false}""");
        Assert.False(await check.ConfirmAsync(session, settings, CancellationToken.None));
        Assert.False(session.GitLabUser!.IsAdmin);
        Assert.False(session.IsAdmin(settings));
    }

    [Fact]
    public async Task Revoked_token_denies_write()
    {
        var (check, settings, session, gitlab) = Setup("""{"message":"401 Unauthorized"}""", HttpStatusCode.Unauthorized);
        Assert.False(await check.ConfirmAsync(session, settings, CancellationToken.None));
        Assert.Equal("/api/v4/user", gitlab.Last!.RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task Unreachable_gitlab_denies_write()
    {
        var (check, settings, session, gitlab) = Setup("{}");
        gitlab.Throw = new HttpRequestException("connection refused");
        Assert.False(await check.ConfirmAsync(session, settings, CancellationToken.None));
    }

    [Fact]
    public async Task Skipping_code_review_requires_current_gitlab_admin_role()
    {
        // Роль администратора отозвана, обычный доступ Developer остался: обход Code Review больше не разрешён,
        // в том числе в режиме входа в Pilot, где администратор админки определяется не по GitLab.
        var (check, _, session, _) = Setup("""{"id":7,"username":"root","is_admin":false}""");
        Assert.False(await check.ConfirmGitLabAdminAsync(session, CancellationToken.None));
        Assert.False(session.GitLabUser!.IsAdmin);

        var (granted, _, admin, _) = Setup("""{"id":7,"username":"root","is_admin":true}""");
        Assert.True(await granted.ConfirmGitLabAdminAsync(admin, CancellationToken.None));
    }

    [Fact]
    public async Task Token_of_another_user_denies_write()
    {
        var (check, settings, session, _) = Setup("""{"id":8,"username":"other","is_admin":true}""");
        Assert.False(await check.ConfirmAsync(session, settings, CancellationToken.None));
    }

    private static (GitLabAdminCheck Check, AdminSettings Settings, UserSession Session, FakeGitLab GitLab) Setup(string user, HttpStatusCode status = HttpStatusCode.OK)
    {
        var gitlab = new FakeGitLab { Body = user, Status = status };
        var network = new NetworkAddressMapper(null);
        var http = new SingleClient(gitlab);
        var factory = new GitLabClientFactory(http, new GitLabAuthService(http, network, Options.Create(new AppOptions())), network);
        var settings = new AdminSettings { GitLabGroupUrl = GitLabUrl + "/automation" };
        var session = new UserSession("s1")
        {
            GitLabToken = new GitLabToken { AccessToken = "pat", BaseUrl = GitLabUrl },
            GitLabUser = new GitLabUser { Id = 7, Username = "root", IsAdmin = true },
        };
        Assert.True(session.IsAdmin(settings));
        return (new GitLabAdminCheck(factory, NullLogger<GitLabAdminCheck>.Instance), settings, session, gitlab);
    }

    private sealed class SingleClient(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class FakeGitLab : HttpMessageHandler
    {
        public string Body { get; init; } = "{}";
        public HttpStatusCode Status { get; init; } = HttpStatusCode.OK;
        public Exception? Throw { get; set; }
        public HttpRequestMessage? Last { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Last = request;
            if (Throw is not null)
                throw Throw;
            return Task.FromResult(new HttpResponseMessage(Status) { Content = new StringContent(Body, System.Text.Encoding.UTF8, "application/json") });
        }
    }
}
