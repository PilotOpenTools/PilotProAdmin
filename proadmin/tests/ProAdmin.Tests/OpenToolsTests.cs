using System.Net;
using ProAdmin.Modules;
using ProAdmin.Pilot;

namespace ProAdmin.Tests;

/// <summary>Модули PilotOpenTools: какие объекты на сервере им соответствуют, сравнение версий с релизом GitHub, что можно ставить.</summary>
public sealed class OpenToolsTests
{
    private static OpenTool Tool(string key) => OpenToolsCatalog.Find(key)!;

    [Theory]
    [InlineData("0 - Общий модуль для клиентских модулей - (Pilot.ClientSdk) - (CICD)", "clientsdk")]
    [InlineData("113Pilot.ClientSdk.ext2", "clientsdk")]
    [InlineData("0 - Общий модуль - (Modules) - (CICD)", "clientsdk")]
    [InlineData("0 - Модуль для отчётов - (Reports) - (CICD)", "reports")]
    [InlineData("112Reports.ext2", "reports")]
    [InlineData("PilotLookUp", "lookup")]
    [InlineData("PilotLookUp.ext2", "lookup")]
    public void ClientModulesAreRecognised(string extensionName, string key) =>
        Assert.Equal(key, OpenToolsCatalog.ForClientModule(extensionName)?.Key);

    [Theory]
    [InlineData("01.087 - Открыть Storadge - (OpenStoradge) - (CICD)")]
    [InlineData("114TestModule.ext2")]
    [InlineData("ServerHost")]
    [InlineData("")]
    [InlineData(null)]
    public void OtherModulesAreNotOpenTools(string? extensionName) =>
        Assert.Null(OpenToolsCatalog.ForClientModule(extensionName));

    [Fact]
    public void SharedSdkIsInstalledBeforeModulesBuiltOnIt()
    {
        var keys = OpenToolsCatalog.All.Select(t => t.Key).ToList();
        Assert.True(keys.IndexOf("clientsdk") < keys.IndexOf("reports"));
    }

    [Theory]
    [InlineData("113Pilot.ClientSdk.ext2-2.1.1.zip", "clientsdk", true)]
    [InlineData("Pilot.ClientSdk.2.1.1.nupkg", "clientsdk", false)]
    [InlineData("SHA256SUMS.txt", "clientsdk", false)]
    [InlineData("112Reports.ext2-1.5.0.zip", "reports", true)]
    [InlineData("PilotLookUp.zip", "lookup", true)]
    [InlineData("ServerHost-1.3.0.zip", "serverhost", true)]
    [InlineData("PilotProAdmin-v1.0.3-lite.zip", "serverhost", false)]
    public void ReleaseAssetIsPickedByName(string asset, string key, bool expected) =>
        Assert.Equal(expected, Tool(key).Asset.IsMatch(asset));

    [Theory]
    [InlineData("2.1.0", "2.1.1", OpenToolSync.Outdated)]
    [InlineData("2.1.1", "2.1.1", OpenToolSync.Current)]
    [InlineData("2.8.1.0", "2.8.1", OpenToolSync.Current)]
    [InlineData("2.9.0", "2.8.1.0", OpenToolSync.Newer)]
    [InlineData(null, "2.8.1", OpenToolSync.Unknown)]
    [InlineData("1.0", null, OpenToolSync.Unknown)]
    public void VersionsAreCompared(string? server, string? github, OpenToolSync expected) =>
        Assert.Equal(expected, OpenToolsService.Compare(server, github));

    [Fact]
    public void OnlyMissingOrOutdatedModulesAreOffered()
    {
        var package = new ModulePackageInfo(new byte[] { 1 }, "112Reports.ext2-1.5.0.zip", "112Reports.ext2", "1.5.0.0", null);
        var row = new OpenToolRow { Tool = Tool("reports"), Release = new OpenToolRelease("v1.5.0", null, null, package, null) };
        row.Envs["test"] = new OpenToolCell(null, OpenToolSync.Missing, null);
        row.Envs["stage"] = new OpenToolCell(null, OpenToolSync.Outdated, null);
        row.Envs["prod"] = new OpenToolCell(null, OpenToolSync.Newer, null);
        Assert.Equal(new[] { "test", "stage" }, row.Pending);

        var noRelease = new OpenToolRow { Tool = Tool("reports"), Release = new OpenToolRelease(null, null, null, null, "нет релиза") };
        noRelease.Envs["test"] = new OpenToolCell(null, OpenToolSync.Missing, null);
        Assert.Empty(noRelease.Pending);
    }

    [Fact]
    public void ServerHostCanBeInstalledAndUpdated()
    {
        var package = new ModulePackageInfo(new byte[] { 1 }, "ServerHost-1.3.0.zip", "ServerHost.servext", "1.3.0.0", null);
        var row = new OpenToolRow { Tool = Tool("serverhost"), Release = new OpenToolRelease("v1.0.4", null, null, package, null) };
        row.Envs["test"] = new OpenToolCell(null, OpenToolSync.Missing, null);
        row.Envs["prod"] = new OpenToolCell(null, OpenToolSync.Outdated, null);
        Assert.Equal(new[] { "test", "prod" }, row.Pending);
    }

    [Theory]
    [InlineData("https://objects.githubusercontent.com/github-production-release-asset/1", true)]
    [InlineData("https://release-assets.githubusercontent.com/x", true)]
    [InlineData("https://github.com/PilotOpenTools/PilotLookUp/releases/download/2.8.1.0/PilotLookUp.zip", true)]
    [InlineData("http://objects.githubusercontent.com/x", false)]
    [InlineData("https://evil.example/githubusercontent.com", false)]
    [InlineData("https://user:pass@objects.githubusercontent.com/x", false)]
    public void ReleaseDownloadsFollowOnlyGitHubStorage(string url, bool allowed) =>
        Assert.Equal(allowed, OpenToolsReleases.IsAllowedRedirect(new Uri(url)));
}

/// <summary>Повторы при обрыве соединения с GitHub.</summary>
public sealed class GitHubRetryTests
{
    private sealed class Flaky(int failures) : HttpMessageHandler
    {
        public int Calls;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            if (Calls <= failures)
                throw new HttpRequestException("The SSL connection could not be established", new IOException("Received an unexpected EOF"));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request });
        }
    }

    [Fact]
    public async Task BrokenConnectionIsRetried()
    {
        var inner = new Flaky(failures: 2);
        using var client = new HttpClient(new GitHubRedirectHandler { InnerHandler = inner });
        var response = await client.GetAsync("https://api.github.com/repos/PilotOpenTools/PilotLookUp/releases/latest");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(3, inner.Calls);
    }

    [Fact]
    public async Task RetriesAreLimited()
    {
        var inner = new Flaky(failures: 10);
        using var client = new HttpClient(new GitHubRedirectHandler { InnerHandler = inner });
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("https://api.github.com/"));
        Assert.Equal(GitHubRedirectHandler.Attempts, inner.Calls);
    }
}

/// <summary>Настоящие релизы github.com/PilotOpenTools: OPENTOOLS_IT=1.</summary>
public sealed class OpenToolsGitHubTests
{
    private sealed class Factory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
        {
            var handler = new GitHubRedirectHandler { InnerHandler = new HttpClientHandler { AllowAutoRedirect = false } };
            var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("PilotProAdmin-tests");
            return client;
        }
    }

    [RequiresEnvFact("OPENTOOLS_IT")]
    public async Task ClientModulesHaveReadableReleases()
    {
        var releases = new OpenToolsReleases(new Factory(), Microsoft.Extensions.Logging.Abstractions.NullLogger<OpenToolsReleases>.Instance);
        foreach (var tool in OpenToolsCatalog.All)
        {
            var release = await releases.GetAsync(tool, refresh: true, CancellationToken.None);
            TestContext.Current.SendDiagnosticMessage($"{tool.Title}: {release.Tag} {release.Version} {release.Package?.ExtensionName} {release.Error}");
            if (tool.Kind == OpenToolKind.Client)
            {
                Assert.Null(release.Error);
                Assert.NotNull(release.Version);
                Assert.Same(tool, OpenToolsCatalog.ForClientModule(release.Package!.ExtensionName));
            }
        }
    }
}

public sealed partial class PagesTests
{
    [Fact]
    public async Task OpenToolsNeedsLogin()
    {
        var response = await _factory.CreateClient().GetAsync("/OpenTools");
        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task OpenToolsPageOpensAndIsLinkedFromModules()
    {
        var client = ClientFor(_factory.NewSession(withGitLab: true));
        var response = await client.GetAsync("/OpenTools");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Модули PilotOpenTools", await Text(response));
    }

    [Fact]
    public async Task OpenToolsInstallNeedsProvenGitLabRights()
    {
        var client = ClientFor(_factory.NewSession(withGitLab: true));
        var response = await PostForm(client, "/OpenTools", "/OpenTools?handler=InstallAll");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.DoesNotContain("OpenTools", response.Headers.Location!.OriginalString);
    }
}
