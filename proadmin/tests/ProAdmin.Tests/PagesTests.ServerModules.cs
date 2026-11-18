using System.Net;

namespace ProAdmin.Tests;

/// <summary>Обновление самого ServerHost из админки: только администратор прода и только с правами на группу серверных модулей.</summary>
public sealed partial class PagesTests
{
    [Fact]
    public async Task HostUpdateIsForbiddenWithoutProdAdmin()
    {
        var client = ClientFor(_factory.NewSession(withGitLab: true));
        var response = await PostForm(client, "/Jobs", "/ServerModules?handler=UpdateHost", ("env", "test"));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task HostUpdateNeedsProvenGitLabRightsEvenForProdAdmin()
    {
        var client = ClientFor(_factory.NewSession(withGitLab: true, prodAdmin: true));
        var response = await PostForm(client, "/Jobs", "/ServerModules?handler=UpdateHost", ("env", "test"));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("/Modules", response.Headers.Location!.OriginalString);
    }
}
