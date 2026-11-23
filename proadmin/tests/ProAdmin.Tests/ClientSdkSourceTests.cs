using System.Net;
using System.Text;
using Microsoft.Extensions.Options;
using ProAdmin.GitLab;
using ProAdmin.Infrastructure;
using ProAdmin.Modules;
using Xunit;

namespace ProAdmin.Tests;

/// <summary>
/// Новый клиентский модуль на Pilot.ClientSdk собирается, только если пакет нужной версии где-то есть:
/// в реестре GitLab pilot-ci/nuget или в nuget.org. Иначе флажок ClientSdk снят, а сборка не падает с NU1101.
/// </summary>
public class ClientSdkSourceTests
{
    private const string Version = ModuleScaffold.DefaultClientSdkVersion;

    [Fact]
    public async Task Package_in_gitlab_feed_is_used()
    {
        var source = await SourceAsync(feed: $$"""[{"name":"Pilot.ClientSdk","version":"{{Version}}"}]""", nuget: null);
        Assert.Equal(ClientSdkSource.GitLabFeed, source);
    }

    [Fact]
    public async Task Package_on_nuget_org_is_used_when_feed_is_empty()
    {
        var source = await SourceAsync(feed: "[]", nuget: $$"""{"versions":["2.0.0","{{Version}}"]}""");
        Assert.Equal(ClientSdkSource.NuGetOrg, source);
    }

    [Fact]
    public async Task Empty_feed_and_no_package_on_nuget_org_means_none()
    {
        // Чистый стенд: pilot-ci/nuget пуст, в nuget.org пакета нет - модуль с ClientSdk не соберётся.
        Assert.Equal(ClientSdkSource.None, await SourceAsync(feed: "[]", nuget: null));
        Assert.Equal(ClientSdkSource.None, await SourceAsync(feed: null, nuget: """{"versions":["1.0.0"]}"""));
    }

    private static Task<ClientSdkSource> SourceAsync(string? feed, string? nuget)
    {
        var handler = new Fake(feed, nuget);
        var network = new NetworkAddressMapper(null);
        var auth = new GitLabAuthService(new NoFactory(), network, Options.Create(new AppOptions()));
        var git = new GitLabClient(new HttpClient(handler), auth, network, new GitLabToken { AccessToken = "pat", BaseUrl = "http://gitlab.test" });
        return ModuleCreation.ClientSdkSourceAsync(git, new HttpClient(handler), Version, CancellationToken.None);
    }

    private sealed class NoFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new InvalidOperationException("не нужен для PAT");
    }

    /// <summary>null - нет (реестра или пакета в nuget.org).</summary>
    private sealed class Fake(string? feed, string? nuget) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.RequestUri!.Host == "api.nuget.org" ? nuget : feed;
            return Task.FromResult(body is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{\"message\":\"404 Not Found\"}") }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }
}
