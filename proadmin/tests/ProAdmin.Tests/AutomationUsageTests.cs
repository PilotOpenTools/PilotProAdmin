using System.Net;
using ProAdmin.Automations;
using ProAdmin.Pilot;
using ProAdmin.Settings;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using ProAdmin.GitLab;
using ProAdmin.Sessions;

namespace ProAdmin.Tests;

public class AutomationUsageTests
{
    internal const string Json = """
        [
          {
            "_comment_": "Version: 1.0.0",
            "when": "StateAttributeChanged",
            "params": {
              "triggerType": "Server",
              "sourceTypes": ["task_simple", "doc_issue"],
              "attributeNames": ["state"],
              "statesTo": "assigned"
            },
            "then": [
              {
                "activity": "SetStateAttributeValue",
                "params": { "target": "Self", "targetTypes": "doc_remark", "attributeName": "actualFor", "toStateName": "inProgress" }
              },
              {
                "activity": "SendNotifications",
                "params": { "subscribers": ["new.attr.executor.person.id", "user@example.com"], "message": "Задача {new.attr.title}" }
              },
              {
                "activity": "SetOrgUnitAttributeValueBasedOnState",
                "params": { "stateToOrgUnitConfig": { "approved": ["signer"] } }
              },
              {
                "activity": "MyCustomActivity",
                "params": { "attributeName": "customAttr", "somethingElse": "notAnAttribute" }
              }
            ]
          }
        ]
        """;

    internal const string CSharp = """
        public class MyCustomActivity
        {
            public string Name => "MyCustomActivity";
            private const string Key = "hiddenAttr";
        }
        """;

    private static EnvironmentSettings Env(string key, string title, string branch) =>
        new() { Key = key, Title = title, Branch = branch, PilotUrl = "http://127.0.0.1:9/" + key };

    internal static CatalogRow Row(string name, string json, string csharp = "", string? devJson = null)
    {
        var env = Env("test", "Тест", "dev");
        var row = new CatalogRow { Name = name };
        row.Envs[env.Key] = new CatalogEnvState { Env = env, Pilot = new AutomationSnapshot(name, json, csharp, false, 0) };
        if (devJson is not null)
            row.BranchJson["dev"] = devJson;
        return row;
    }

    private static string[] Names(IEnumerable<UsageRef> refs, UsageKind kind) =>
        refs.Where(r => r.Kind == kind).Select(r => r.Name).Distinct().OrderBy(n => n, StringComparer.Ordinal).ToArray();

    [Fact]
    public void ExtractClassifiesNamesBySchema()
    {
        var refs = AutomationUsage.Extract(Json)!;

        Assert.Equal(new[] { "doc_issue", "doc_remark", "task_simple" }, Names(refs, UsageKind.Type));
        Assert.Equal(new[] { "actualFor", "customAttr", "executor", "signer", "state", "title" }, Names(refs, UsageKind.Attribute));
        Assert.Equal(new[] { "approved", "assigned", "inProgress" }, Names(refs, UsageKind.State));
        Assert.Equal(new[] { "MyCustomActivity", "SendNotifications", "SetOrgUnitAttributeValueBasedOnState", "SetStateAttributeValue" }, Names(refs, UsageKind.Activity));

        Assert.Contains(refs, r => r is { Name: "task_simple", Place: "StateAttributeChanged · sourceTypes" });
        Assert.Contains(refs, r => r is { Name: "actualFor", Place: "SetStateAttributeValue · attributeName" });
        Assert.DoesNotContain(refs, r => r.Name == "notAnAttribute" || r.Name == "Self" || r.Name.Contains('@'));
    }

    [Theory]
    [InlineData("""{ "when": "ItemCreated", "params": { "sourceTypes": ["task_simple"] } }""")]
    [InlineData("""{ "when": "UnknownTrigger", "params": { "sourceTypes": ["task_simple"] } }""")]
    public void ExtractReadsObjectInsteadOfArrayAndUnknownTrigger(string json) =>
        Assert.Contains(AutomationUsage.Extract(json)!, r => r is { Kind: UsageKind.Type, Name: "task_simple" });

    [Fact]
    public void ExtractReturnsNullForBrokenJson() => Assert.Null(AutomationUsage.Extract("[ { \"when\": "));

    [Fact]
    public void IndexJoinsSourcesAndReportsUnparsed()
    {
        var index = AutomationUsageIndex.Build(new[]
        {
            Row("Beta", Json, devJson: Json),
            Row("Alpha", Json),
            Row("Broken", "[ {"),
        });

        var type = Assert.Single(index.Find(UsageKind.Type, "task_simple"));
        Assert.Equal(new[] { "Alpha", "Beta" }, type.Automations.Select(a => a.Name));
        Assert.Equal(new[] { "Тест", "dev" }, type.Automations[1].Sources);
        Assert.Equal(("Broken", "Тест"), Assert.Single(index.Unparsed));
    }

    [Fact]
    public void FindIsCaseInsensitiveAndFiltersByKind()
    {
        var index = AutomationUsageIndex.Build(new[] { Row("Alpha", Json) });

        Assert.Equal(new[] { "doc_issue", "doc_remark" }, index.Find(UsageKind.Type, "DOC_").Select(e => e.Name));
        Assert.Empty(index.Find(UsageKind.State, "doc_"));
        Assert.Equal(4, index.Find(UsageKind.Activity, null).Count);
    }

    [Fact]
    public void CSharpDeclarationAndMentionAreDistinguished()
    {
        var index = AutomationUsageIndex.Build(new[] { Row("Alpha", Json, CSharp) });

        var activity = Assert.Single(index.Find(UsageKind.Activity, "MyCustomActivity")).Automations.Single();
        Assert.Contains(AutomationUsage.CSharpDeclaration, activity.Places);
        Assert.DoesNotContain(AutomationUsage.CSharpMention, activity.Places);

        // Имени нет в параметрах сценария, но код использует его строкой - перед изменением типа это важно.
        var mention = Assert.Single(index.Find(UsageKind.Attribute, "hiddenAttr"));
        Assert.Equal(UsageKind.Attribute, mention.Kind);
        Assert.Equal(AutomationUsage.CSharpMention, mention.Automations.Single().Places.Single());

        // Часть имени в C# не считается упоминанием.
        Assert.Empty(index.Find(UsageKind.Attribute, "hiddenAtt"));
    }
}

/// <summary>Страницы с данными каталога. Фабрика задаёт настройки на весь процесс, поэтому эти тесты живут в одном классе с остальными страничными.</summary>
public sealed partial class PagesTests
{
    [Theory]
    [InlineData("/Automations/Usage", "task_simple")]
    [InlineData("/Automations/Usage?kind=Type&name=task_simple", "StateAttributeChanged · sourceTypes")]
    [InlineData("/Automations/Usage?kind=Attribute&name=hiddenAttr", "встречается в тексте")]
    [InlineData("/Automations/Usage?name=NoSuchName", "не найдено в разобранных параметрах")]
    [InlineData("/Automations/Usage", "Broken (Тест)")]
    [InlineData("/Automations", "search-index")]
    [InlineData("/Automations", "Где используется")]
    public async Task PagesShowCatalogData(string url, string expected)
    {
        using var factory = WithCatalog(out var session);
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(TestAuthHandler.Header, session);

        var response = await client.GetAsync(url);
        var html = await response.Content.ReadAsStringAsync();

        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{url}: {(int)response.StatusCode}\n{html}");
        Assert.Contains(expected, WebUtility.HtmlDecode(html));
    }

    [Fact]
    public async Task SearchIndexCannotCloseScriptTag()
    {
        using var factory = WithCatalog(out var session, AutomationUsageTests.Row("Evil", """[ { "when": "</script><b>" } ]"""));
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.Header, session);

        var html = await client.GetStringAsync("/Automations");

        Assert.DoesNotContain("</script><b>", html);
        Assert.Contains("\\u003C/script\\u003E", html);
    }

    private WebApplicationFactory<Program> WithCatalog(out string session, params CatalogRow[] extra)
    {
        _factory.NewSession(withGitLab: true); // сохраняет настройки с адресами серверов
        var rows = new[] { AutomationUsageTests.Row("Alpha", AutomationUsageTests.Json, AutomationUsageTests.CSharp, devJson: AutomationUsageTests.Json), AutomationUsageTests.Row("Broken", "[ {") }.Concat(extra).ToArray();
        var factory = _factory.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
            s.AddSingleton<AutomationCatalog>(p => new FakeCatalog(p.GetRequiredService<PilotGateway>(), rows))));

        var created = factory.Services.GetRequiredService<SessionVault>().Create("http://127.0.0.1:9", new PilotLogin("admin", "secret"));
        created.GitLabToken = new GitLabToken { AccessToken = "fake", BaseUrl = "http://127.0.0.1:9" };
        created.GitLabUser = new GitLabUser { Username = "tester", Name = "Tester" };
        session = created.Id;
        return factory;
    }

    private sealed class FakeCatalog : AutomationCatalog
    {
        private readonly CatalogRow[] _rows;

        public FakeCatalog(PilotGateway pilot, CatalogRow[] rows) : base(pilot) => _rows = rows;

        public override Task<CatalogResult> LoadAsync(AdminSettings settings, Func<string, PilotLogin> login, GitLabClient git, CancellationToken ct)
        {
            var result = new CatalogResult();
            result.Envs.AddRange(settings.Environments.Where(e => e.HasServer));
            foreach (var env in result.Envs)
                foreach (var row in _rows)
                    if (!row.Envs.ContainsKey(env.Key))
                        row.Envs[env.Key] = new CatalogEnvState { Env = env, State = SyncState.Unknown };
            result.Rows.AddRange(_rows);
            return Task.FromResult(result);
        }
    }
}
