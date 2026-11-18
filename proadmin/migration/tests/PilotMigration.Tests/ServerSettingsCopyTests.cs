using System.Xml.Linq;
using PilotMigration.Config;
using PilotMigration.Engine;
using PilotMigration.Pilot;

namespace PilotMigration.Tests;

public class ServerSettingsCopyTests
{
    private const string ProdXml = """
        <?xml version="1.0" encoding="utf-8"?>
        <Configuration>
          <IsExtendedLoggingEnabled>false</IsExtendedLoggingEnabled>
          <IsAutomationLoggingEnabled>false</IsAutomationLoggingEnabled>
          <IsSnapshotEnabled>false</IsSnapshotEnabled>
          <CheckChangeCreator>true</CheckChangeCreator>
          <AreParallelChangesDisabled>true</AreParallelChangesDisabled>
        </Configuration>
        """;

    [Fact]
    public void OnlyBehaviorIsReadFromSource()
    {
        var values = ServerSettingsCopy.ReadBehavior(XDocument.Parse(ProdXml));
        Assert.Equal(3, values.Count);
        Assert.True(values["AreParallelChangesDisabled"]);
        Assert.DoesNotContain("IsAutomationLoggingEnabled", values.Keys);
    }

    [Fact]
    public void TargetLoggingIsKeptWhenCopyingFromProd()
    {
        var target = new[]
        {
            "# Настройки сервера Pilot (контур test).",
            "# Изменено: 2026-10-01T10:00:00+03:00, admin",
            "IsAutomationLoggingEnabled=true",
            "AreParallelChangesDisabled=false",
        };
        var behavior = ServerSettingsCopy.ReadBehavior(XDocument.Parse(ProdXml));

        var (text, changes) = ServerSettingsCopy.Merge(target, behavior, "test", "миграция (admin)", DateTimeOffset.Parse("2026-10-02T12:00:00+03:00"));

        Assert.Contains("\nIsAutomationLoggingEnabled=true\n", text);
        Assert.Contains("\nAreParallelChangesDisabled=true\n", text);
        Assert.Contains("\nCheckChangeCreator=true\n", text);
        Assert.Contains("# Изменено: 2026-10-02T12:00:00+03:00, миграция (admin)\n", text);
        Assert.DoesNotContain("\r", text);
        Assert.Equal(3, changes.Count);
        Assert.Contains("AreParallelChangesDisabled: false / true", changes);
    }

    [Fact]
    public void DirectoryIsNextToSettingsFolder()
    {
        Assert.Equal("/mnt/test/server-settings", ServerSettingsCopy.DirectoryFor(new EnvConfig { SettingsXmlPath = "/mnt/test/settings/settings.xml" }));
        Assert.Null(ServerSettingsCopy.DirectoryFor(new EnvConfig()));
    }

    [Fact]
    public void ServerSettingsAloneIsNotEmptySelectiveRequest()
    {
        var request = new MigrationRequest { Mode = MigrationMode.Selective, CopyServerSettings = true };
        Assert.True(request.Options.IsEmpty);
        Assert.EndsWith("+ настройки сервера", request.Describe());
    }
}
