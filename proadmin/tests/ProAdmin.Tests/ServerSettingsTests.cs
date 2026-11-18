using ProAdmin.Infrastructure;
using ProAdmin.Servers;
using Microsoft.Extensions.Options;

namespace ProAdmin.Tests;

public sealed class ServerSettingsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pwa-servers-" + Guid.NewGuid().ToString("N"));

    public ServerSettingsTests() => Directory.CreateDirectory(Path.Combine(_root, "test"));

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { /* временная папка */ }
    }

    private ServerSettingsStore Store() =>
        new(Options.Create(new AppOptions { ServerSettings = new ServerSettingsOptions { Root = _root } }));

    [Fact]
    public void WrittenConfIsPlainKeyValueForBash()
    {
        Store().Write("test", new Dictionary<string, bool> { ["IsAutomationLoggingEnabled"] = true, ["Unknown"] = true }, "admin");

        var text = File.ReadAllText(Path.Combine(_root, "test", ServerSettingsStore.ConfFile));
        Assert.DoesNotContain("\r", text);
        Assert.Contains("\nIsAutomationLoggingEnabled=true\n", text);
        Assert.DoesNotContain("Unknown", text);
        Assert.Contains("pilot-server-test", text);
    }

    [Fact]
    public void RestartRequiredUntilServerAppliesValues()
    {
        var store = Store();
        store.Write("test", new Dictionary<string, bool> { ["IsAutomationLoggingEnabled"] = true, ["EnableCSharpScriptDebug"] = false }, "admin");

        var state = store.Read("test");
        Assert.True(state.Available);
        Assert.Equal("admin", state.UpdatedBy);
        Assert.Null(state.Applied);
        Assert.True(state.RestartRequired);

        // Так пишет wait-for-postgres.sh при старте контейнера.
        File.WriteAllText(Path.Combine(_root, "test", ServerSettingsStore.AppliedFile),
            "# комментарий\nAppliedAt=2026-10-02T15:00:00+03:00\nIsAutomationLoggingEnabled=false\nEnableCSharpScriptDebug=false\nCheckChangeCreator=true\n");
        state = store.Read("test");
        Assert.Equal(new[] { "IsAutomationLoggingEnabled" }, state.PendingKeys);
        Assert.Equal(true, state.AppliedValue("CheckChangeCreator"));
        Assert.NotNull(state.AppliedAt);

        File.WriteAllText(Path.Combine(_root, "test", ServerSettingsStore.AppliedFile),
            "AppliedAt=2026-10-02T15:05:00+03:00\nIsAutomationLoggingEnabled=true\nEnableCSharpScriptDebug=false\n");
        Assert.False(store.Read("test").RestartRequired);
    }

    [Fact]
    public void ModeClusterInAppliedConfMarksClusterAndIsNotAPendingSetting()
    {
        var store = Store();
        store.Write("test", new Dictionary<string, bool> { ["IsSnapshotEnabled"] = true }, "admin");
        Assert.False(store.Read("test").IsCluster);

        File.WriteAllText(Path.Combine(_root, "test", ServerSettingsStore.AppliedFile),
            "AppliedAt=2026-11-12T10:00:00+03:00\nMode=cluster\nIsSnapshotEnabled=false\n");
        var state = store.Read("test");
        Assert.True(state.IsCluster);
        Assert.Equal(new[] { "IsSnapshotEnabled" }, state.PendingKeys);
        Assert.Null(state.AppliedValue("Mode"));

        File.WriteAllText(Path.Combine(_root, "test", ServerSettingsStore.AppliedFile), "Mode=standalone\nIsSnapshotEnabled=true\n");
        Assert.False(store.Read("test").IsCluster);
    }

    [Fact]
    public void ClusterNodeWithoutConfMarksModeOnlyAndSavedFlashAsksToRestartAllNodes()
    {
        // Так пишет обёртка узла кластера, когда server-settings.conf ещё нет: только AppliedAt и Mode.
        File.WriteAllText(Path.Combine(_root, "test", ServerSettingsStore.AppliedFile), "# Фактические значения\nAppliedAt=2026-11-12T10:00:00+03:00\nMode=cluster\n");
        var store = Store();
        Assert.True(store.Read("test").IsCluster);

        // Первое сохранение в таком контуре: кластерный текст, без имени контейнера и docker restart.
        store.Write("test", new Dictionary<string, bool> { ["IsSnapshotEnabled"] = true }, "admin");
        var flash = ProAdmin.Pages.Servers.IndexModel.SavedFlash("Тест", "test", store.Read("test"));
        Assert.Contains(ServerSettingsStore.ClusterRestartHint, flash);
        Assert.DoesNotContain("pilot-server-test", flash);
        Assert.DoesNotContain("docker restart", flash);
    }

    [Fact]
    public void SavedFlashForMonolithNamesContainerOrSaysNoRestartNeeded()
    {
        var store = Store();
        store.Write("test", new Dictionary<string, bool> { ["IsSnapshotEnabled"] = true }, "admin");
        Assert.Contains("перезапуска контейнера pilot-server-test", ProAdmin.Pages.Servers.IndexModel.SavedFlash("Тест", "test", store.Read("test")));

        File.WriteAllText(Path.Combine(_root, "test", ServerSettingsStore.AppliedFile), "AppliedAt=2026-11-12T10:00:00+03:00\nIsSnapshotEnabled=true\n");
        Assert.Contains("перезапуск не нужен", ProAdmin.Pages.Servers.IndexModel.SavedFlash("Тест", "test", store.Read("test")));
    }

    [Fact]
    public void ClusterConfNamesNodesNotContainer()
    {
        var store = Store();
        File.WriteAllText(Path.Combine(_root, "test", ServerSettingsStore.AppliedFile), "Mode=cluster\n");
        store.Write("test", new Dictionary<string, bool> { ["IsSnapshotEnabled"] = true }, "admin");

        var text = File.ReadAllText(Path.Combine(_root, "test", ServerSettingsStore.ConfFile));
        Assert.Contains("узлов кластера", text);
        Assert.DoesNotContain("pilot-server-test", text);
        Assert.Contains("\nIsSnapshotEnabled=true\n", text);
    }

    [Fact]
    public void MissingDirectoryOrBadEnvIsUnavailable()
    {
        Assert.False(Store().Read("prod").Available);
        Assert.False(Store().Read("../test").Available);
        Assert.False(new ServerSettingsStore(Options.Create(new AppOptions())).Read("test").Available);
    }

    [Fact]
    public void RecommendedLoggingIsOffOnlyOnProd()
    {
        var logging = ServerSettingCatalog.Find("IsAutomationLoggingEnabled")!;
        Assert.Equal(false, logging.Recommended("prod"));
        Assert.Equal(true, logging.Recommended("test"));
        Assert.Equal(true, logging.Recommended("stage"));
        Assert.Equal(ServerSettingScope.Contour, logging.Scope);
    }
}
