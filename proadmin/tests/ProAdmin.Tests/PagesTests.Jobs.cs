using System.Net;
using ProAdmin.Jobs;
using ProAdmin.Modules;
using Microsoft.Extensions.DependencyInjection;

namespace ProAdmin.Tests;

/// <summary>Журнал операций: обычный пользователь видит только свои, администратор прода - все.</summary>
public sealed partial class PagesTests
{
    private PublishJob AddJob(string name, string pilotUser, string? gitLabUser)
    {
        var job = new PublishJob { AutomationName = name, PilotUser = pilotUser, GitLabUser = gitLabUser, Env = "test", EnvTitle = "Тест", Branch = "dev", Status = JobStatus.Succeeded };
        _factory.Services.GetRequiredService<JobStore>().Add(job);
        return job;
    }

    [Fact]
    public async Task ForeignJobIs404ForOrdinaryUserAndVisibleToProdAdmin()
    {
        var foreign = AddJob("foreign-auto", "someone", "other-dev");
        var user = ClientFor(_factory.NewSession(withGitLab: true));
        var admin = ClientFor(_factory.NewSession(withGitLab: true, prodAdmin: true));

        Assert.Equal(HttpStatusCode.NotFound, (await user.GetAsync($"/Jobs/{foreign.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await user.GetAsync($"/Jobs/{foreign.Id}?handler=Status")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync($"/Jobs/{foreign.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync($"/Jobs/{foreign.Id}?handler=Status")).StatusCode);
    }

    [Fact]
    public async Task OwnJobIsVisibleByPilotLoginOrGitLabUser()
    {
        // сессия теста: вход в Pilot «admin», пользователь GitLab «tester»
        var byPilot = AddJob("own-by-pilot", "admin", null);
        var byGitLab = AddJob("own-by-gitlab", "ci-service", "tester");
        var user = ClientFor(_factory.NewSession(withGitLab: true));

        Assert.Equal(HttpStatusCode.OK, (await user.GetAsync($"/Jobs/{byPilot.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await user.GetAsync($"/Jobs/{byGitLab.Id}?handler=Status")).StatusCode);
    }

    [Fact]
    public async Task JobsListShowsOnlyOwnToOrdinaryUser()
    {
        AddJob("list-foreign-auto", "someone", "other-dev");
        AddJob("list-own-auto", "admin", null);

        var userPage = WebUtility.HtmlDecode(await ClientFor(_factory.NewSession(withGitLab: true)).GetStringAsync("/Jobs"));
        Assert.Contains("Ваши операции", userPage);
        Assert.Contains("list-own-auto", userPage);
        Assert.DoesNotContain("list-foreign-auto", userPage);

        var adminPage = WebUtility.HtmlDecode(await ClientFor(_factory.NewSession(withGitLab: true, prodAdmin: true)).GetStringAsync("/Jobs"));
        Assert.DoesNotContain("Ваши операции", adminPage);
        Assert.Contains("list-own-auto", adminPage);
        Assert.Contains("list-foreign-auto", adminPage);
    }

    [Theory]
    [InlineData("https://admin.example.local", "https://admin.example.local/Jobs/")]
    [InlineData("https://admin.example.local/", "https://admin.example.local/Jobs/")]
    [InlineData(null, "/Jobs/")]
    [InlineData("", "/Jobs/")]
    public void CiJobLinkPointsToExistingJobRoute(string? publicUrl, string prefix)
    {
        var id = Guid.NewGuid();
        Assert.Equal(prefix + id, ModuleCiDeployEndpoint.JobLink(publicUrl, id));
    }
}
