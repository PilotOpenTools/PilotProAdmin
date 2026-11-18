using System.Net;

namespace ProAdmin.Tests;

/// <summary>Раздел «Серверы»: у монолита тексты про контейнер, у кластера (applied.conf с Mode=cluster) - про перезапуск всех узлов.</summary>
public sealed partial class PagesTests
{
    private async Task<string> ServersSectionAsync(string env)
    {
        var client = ClientFor(_factory.NewSession(withGitLab: true));
        var response = await client.GetAsync("/Servers");
        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var start = html.IndexOf($"id=\"env-{env}\"", StringComparison.Ordinal);
        Assert.True(start >= 0, $"нет блока контура {env}");
        var end = html.IndexOf("</section>", start, StringComparison.Ordinal);
        return html[start..end];
    }

    [Fact]
    public async Task ClusterServersSectionAsksToRestartAllNodes()
    {
        var section = await ServersSectionAsync("stage");
        Assert.Contains("Ожидает перезапуска", section);
        Assert.Contains("Перезапустите все узлы сервера Pilot контура: остановите все, затем запустите", section);
        Assert.Contains("пока не перезапущены все узлы, остановка или запуск базы может вернуть прежние значения", section.Replace("Пока", "пока"));
        Assert.Contains("В конфигурации кластера", section);
        Assert.Contains("Остальные узлы получат значения только после своего перезапуска", section);
        Assert.DoesNotContain("docker restart", section);
        Assert.DoesNotContain("pilot-server-stage", section);
    }

    [Fact]
    public async Task MonolithServersSectionKeepsContainerTexts()
    {
        var section = await ServersSectionAsync("test");
        Assert.Contains("docker restart pilot-server-test", section);
        Assert.Contains("контейнер", section);
        Assert.DoesNotContain("Перезапустите все узлы", section);
        Assert.DoesNotContain("Остальные узлы", section);
    }
}
