using ProAdmin.Automations.Validation;

namespace ProAdmin.Tests;

public class LinterTests
{
    private const string Valid = """
        [
          {
            "_comment_": "Version: 1.0.0. Взять в работу при открытии",
            "when": "DocumentOpened",
            "params": { "triggerType": "Client", "sourceTypes": ["document"] },
            "then": [
              {
                "activity": "SetStateAttributeValue",
                "params": {
                  "sourceTypes": [],
                  "attributeName": "state",
                  "target": "Relations",
                  "relationType": "TaskAttachments",
                  "targetTypes": "task_simple",
                  "initialStateNames": ["assigned"],
                  "toStateName": "inProgress"
                }
              }
            ]
          }
        ]
        """;

    private static MetadataIndex Meta()
    {
        var m = new MetadataIndex();
        m.Types.UnionWith(new[] { "document", "task_simple" });
        m.Attributes.UnionWith(new[] { "state", "executor", "initiator", "auditors", "title" });
        m.States.UnionWith(new[] { "assigned", "inProgress", "completed" });
        m.ScriptActivities["NotifyChief"] = "OtherAutomation";
        return m;
    }

    private static List<LintIssue> Lint(string json, string? cs = null, MetadataIndex? meta = null) =>
        AutomationLinter.Lint(json, cs, "Test", meta);

    [Fact]
    public void ValidScenarioHasNoProblems()
    {
        var issues = Lint(Valid, meta: Meta());
        Assert.DoesNotContain(issues, i => i.Level is "error" or "warn");
    }

    [Fact]
    public void StandaloneCommentBeforeTriggerIsWarning()
    {
        var json = "[\n  { \"_comment_\": \"Version: 1.0.0\" },\n  { \"when\": \"ItemCreated\", \"then\": [] }\n]";
        Assert.Contains(Lint(json), i => i.Level == "warn" && i.Line == 2 && i.Message.Contains("роняет клиент Pilot"));
    }

    [Fact]
    public void LoneCommentIsError()
    {
        Assert.Contains(Lint("[\n  { \"_comment_\": \"Version: 1.0.0\" }\n]"), i => i.IsError && i.Message.Contains("роняет клиент Pilot"));
    }

    [Fact]
    public void MissingCommaReportsLine()
    {
        var broken = "[\n  {\n    \"when\": \"ItemCreated\"\n    \"then\": []\n  }\n]";
        var issue = Assert.Single(Lint(broken));
        Assert.True(issue.IsError);
        Assert.Equal(4, issue.Line);
        Assert.Contains("запят", issue.Message);
    }

    [Fact]
    public void UnquotedValueIsExplained()
    {
        var issue = Assert.Single(Lint("[{\"when\":\"ItemCreated\",\"then\":[{\"activity\":\"X\",\"params\":{\"target\": Self}}]}]"));
        Assert.Contains("кавычках", issue.Message);
    }

    [Fact]
    public void TriggerTypoSuggestsName()
    {
        var issues = Lint(Valid.Replace("DocumentOpened", "DocumentOpend"));
        Assert.Contains(issues, i => i.IsError && i.Message.Contains("«DocumentOpened»") && i.Line == 4);
    }

    [Fact]
    public void ActivityTypoIsError() =>
        Assert.Contains(Lint(Valid.Replace("\"SetStateAttributeValue\"", "\"SetStateAtributeValue\"")),
            i => i.IsError && i.Message.Contains("«SetStateAttributeValue»"));

    [Fact]
    public void WrongEnumValues()
    {
        var issues = Lint(Valid.Replace("\"Client\"", "\"Sever\"").Replace("\"Relations\"", "\"relations\""));
        Assert.Contains(issues, i => i.IsError && i.Message.Contains("«Sever»") && i.Message.Contains("«Server»"));
        Assert.Contains(issues, i => i.IsError && i.Message.Contains("«relations»") && i.Message.Contains("«Relations»"));
    }

    [Fact]
    public void DocumentOpenedMustBeClient() =>
        Assert.Contains(Lint(Valid.Replace("\"Client\"", "\"Server\"")), i => i.IsError && i.Message.Contains("только с \"triggerType\": \"Client\""));

    [Fact]
    public void ArrayExpected() =>
        Assert.Contains(Lint(Valid.Replace("[\"document\"]", "\"document\"")), i => i.IsError && i.Message.Contains("массивом"));

    [Fact]
    public void MetadataNamesAreChecked()
    {
        var issues = Lint(Valid.Replace("\"inProgress\"", "\"inProgres\"").Replace("\"state\"", "\"stat\"").Replace("[\"document\"]", "[\"documnt\"]"), meta: Meta());
        Assert.Contains(issues, i => i.Level == "warn" && i.Message.Contains("состояния «inProgres»") && i.Message.Contains("«inProgress»"));
        Assert.Contains(issues, i => i.Level == "warn" && i.Message.Contains("атрибута «stat»"));
        Assert.Contains(issues, i => i.Level == "warn" && i.Message.Contains("типа «documnt»"));
    }

    [Fact]
    public void UnknownParameterSuggestsName() =>
        Assert.Contains(Lint(Valid.Replace("\"toStateName\"", "\"toState\"")), i => i.Message.Contains("«toState»") && i.Message.Contains("«toStateName»"));

    [Fact]
    public void ExpressionsInNotificationsAreChecked()
    {
        const string json = """
            [{ "when": "AttributeChanged",
               "params": { "triggerType": "Server", "sourceTypes": [], "attributeNames": ["auditors"] },
               "then": [{ "activity": "SendNotifications",
                          "params": { "subscribers": ["new.attr.auditor.person.id"], "message": "{initiatingPersn.displayName} назначил вас" } }] }]
            """;
        var issues = Lint(json, meta: Meta());
        Assert.Contains(issues, i => i.Message.Contains("атрибута «auditor»") && i.Message.Contains("«auditors»"));
        Assert.Contains(issues, i => i.Message.Contains("initiatingPersn") && i.Message.Contains("«initiatingPerson»"));
    }

    [Fact]
    public void CSharpActivitiesAreRecognised()
    {
        const string cs = "public class A : IServerActivity { public string Name => \"MyAction\"; }";
        const string json = """
            [{ "when": "ItemCreated", "params": { "triggerType": "Client", "sourceTypes": [] },
               "then": [{ "activity": "MyAction" }, { "activity": "NotifyChief" }, { "activity": "Unknown" }] }]
            """;
        var issues = Lint(json, cs, Meta());
        Assert.DoesNotContain(issues, i => i.IsError);
        Assert.Contains(issues, i => i.Level == "warn" && i.Message.Contains("только серверными триггерами"));
        Assert.Contains(issues, i => i.Level == "info" && i.Message.Contains("«OtherAutomation»"));
        Assert.Contains(issues, i => i.Level == "warn" && i.Message.Contains("«Unknown» не найдено"));
    }

    [Fact]
    public void DeclaredActivityNamesAreParsed()
    {
        Assert.Equal(new[] { "A", "B" }, MetadataIndex.DeclaredActivities(
            "public string Name => \"A\";\npublic string Name { get { return \"B\"; } }"));
    }

    [Fact]
    public void HostedModuleActivitiesAreRecognised()
    {
        var hosted = HostedActivities.From(new ProAdmin.Pilot.ServerModules.HostState
        {
            Modules =
            {
                new() { Name = "Approval", Version = "1.2.0", Enabled = true, Loaded = true,
                        Activities = { new() { Name = "ApprovalDocumentVersionSaving" } } },
                new() { Name = "Old", Version = "0.1.0", Enabled = false },
            },
        });
        const string json = """
            [{ "when": "ItemStateChanged", "params": { "triggerType": "Server" },
               "then": [{ "activity": "ApprovalDocumentVersionSaving" }, { "activity": "Missing" }] }]
            """;
        var issues = AutomationLinter.Lint(json, null, "Self", Meta(), hosted);
        Assert.Contains(issues, i => i.Level == "info" && i.Message.Contains("«Approval» 1.2.0"));
        Assert.DoesNotContain(issues, i => i.Level == "warn" && i.Message.Contains("ApprovalDocumentVersionSaving"));
        Assert.Contains(issues, i => i.Level == "warn" && i.Message.Contains("«Missing»") && i.Message.Contains("Old"));
    }
}
