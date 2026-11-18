using System.Text.Json;
using ProAdmin.Pilot;
using ProAdmin.Pilot.ServerModules;
using Xunit;

namespace ProAdmin.Tests;

public class DebugCaseTests
{
    private const string Triggers = """
        [
          {
            "when": "VersionChanged",
            "_comment_": "Version 1.2.0",
            "params": { "triggerType": "Server", "sourceTypes": [ "document", "drawing" ] },
            "then": [
              { "activity": "Other" },
              { "activity": "ApprovalDocumentVersionSaving", "params": { "attr": "documentVersion", "list": [ "a", "b" ] } }
            ]
          },
          { "when": "DocumentSigned", "params": { "sourceTypes": [ "document" ] }, "then": [ { "activity": "ApprovalDocumentVersionSaving" } ] }
        ]
        """;

    private static readonly AutomationSnapshot[] Automations =
    {
        new("Версия документа", Triggers, "", false, 0),
        new("Битая", "не json", "", true, 1),
    };

    [Fact]
    public void FindsEveryUseOfActivity()
    {
        var uses = DebugCaseBuilder.FindUses(Automations, "ApprovalDocumentVersionSaving");

        Assert.Equal(2, uses.Count);
        Assert.Equal("VersionChanged", uses[0].When);
        Assert.Equal(new[] { "document", "drawing" }, uses[0].SourceTypes);
        Assert.Equal("documentVersion", uses[0].Params!["attr"]!.ToString());
        Assert.Equal("DocumentSigned", uses[1].When);
        Assert.Null(uses[1].Params);
    }

    [Fact]
    public void CaseTakesParamsFromFirstUseAndHasNoPassword()
    {
        var source = Guid.NewGuid();
        var json = DebugCaseBuilder.Build("http://pilot:5547/db", "ivanov", 103, "ApprovalDocumentVersionSaving", "ApprovalDocumentVersionSaving", source, Automations);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("http://pilot:5547/db", root.GetProperty("server").GetString());
        Assert.Equal("ivanov", root.GetProperty("login").GetString());
        Assert.Equal(source.ToString(), root.GetProperty("sourceId").GetString());
        Assert.Equal("VersionChanged", root.GetProperty("when").GetString());
        Assert.Equal("documentVersion", root.GetProperty("params").GetProperty("attr").GetString());
        Assert.Equal(2, root.GetProperty("automations").GetArrayLength());
        Assert.False(root.TryGetProperty("password", out _));
        Assert.False(root.GetProperty("apply").GetBoolean());
    }

    [Fact]
    public void CaseCarriesApplyChoice()
    {
        var json = DebugCaseBuilder.Build("http://pilot:5547/db", "ivanov", 103, "ApprovalDocumentVersionSaving", "ApprovalDocumentVersionSaving", null, Automations, apply: true);
        using var doc = JsonDocument.Parse(json);

        Assert.True(doc.RootElement.GetProperty("apply").GetBoolean());
    }

    [Theory]
    [InlineData("prod")]
    [InlineData("stage")]
    [InlineData("test")]
    public void CaseCarriesContour(string contour)
    {
        var json = DebugCaseBuilder.Build("http://pilot:5547/db", "ivanov", 103, "ApprovalDocumentVersionSaving", null, null, Automations, apply: true, contour: contour);
        using var doc = JsonDocument.Parse(json);

        Assert.Equal(contour, doc.RootElement.GetProperty("contour").GetString());
    }

    [Fact]
    public void CaseWithoutContourOmitsField()
    {
        var json = DebugCaseBuilder.Build("http://pilot:5547/db", "ivanov", 103, "ApprovalDocumentVersionSaving", null, null, Automations);
        using var doc = JsonDocument.Parse(json);

        Assert.False(doc.RootElement.TryGetProperty("contour", out _));
    }

    [Fact]
    public void UnknownActivityStillGivesUsableCase()
    {
        var json = DebugCaseBuilder.Build("http://pilot:5547/db", "ivanov", 103, "Nope", null, null, Automations);
        using var doc = JsonDocument.Parse(json);

        Assert.Equal(JsonValueKind.Object, doc.RootElement.GetProperty("params").ValueKind);
        Assert.Contains("не найдена", doc.RootElement.GetProperty("_comment_").GetString());
    }
}
