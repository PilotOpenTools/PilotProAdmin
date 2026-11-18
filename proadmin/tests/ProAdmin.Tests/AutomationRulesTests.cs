using ProAdmin.Automations;
using ProAdmin.GitLab;
using ProAdmin.Infrastructure;
using ProAdmin.Pilot;
using ProAdmin.Settings;

namespace ProAdmin.Tests;

public class AutomationVersionTests
{
    [Fact]
    public void FindsVersionInComment()
    {
        const string json = "[\n  {\n    \"_comment_\": \"Version: 1.2.10. Уведомление\"\n  }\n]";
        Assert.Equal(new AutomationVersion(1, 2, 10), AutomationVersion.Find(json));
    }

    [Fact]
    public void ComparesNumerically()
    {
        Assert.True(new AutomationVersion(1, 0, 10) > new AutomationVersion(1, 0, 9));
        Assert.True(new AutomationVersion(2, 0, 0) > new AutomationVersion(1, 99, 99));
        Assert.False(new AutomationVersion(1, 0, 1) > new AutomationVersion(1, 0, 1));
    }

    [Fact]
    public void MissingVersionIsNull() => Assert.Null(AutomationVersion.Find("[{\"_comment_\":\"без версии\"}]"));
}

public class AutomationChecksTests
{
    private const string V101 = "[{\"_comment_\":\"Version: 1.0.1\"}, {\"a\":1}]";
    private const string V102 = "[{\"_comment_\":\"Version: 1.0.2\"}, {\"a\":2}]";

    [Fact]
    public void VersionMustGrowWhenJsonChanged()
    {
        Assert.Equal("error", AutomationChecks.CheckVersion("[{\"_comment_\":\"Version: 1.0.1\"}, {\"a\":2}]", V101, "dev").Level);
        Assert.Equal("ok", AutomationChecks.CheckVersion(V102, V101, "dev").Level);
    }

    [Fact]
    public void SameJsonDoesNotNeedBump() =>
        Assert.Equal("ok", AutomationChecks.CheckVersion(V101 + "\r\n", V101, "dev").Level);

    [Fact]
    public void NewFileOnlyNeedsVersion()
    {
        Assert.Equal("ok", AutomationChecks.CheckVersion(V101, null, "dev").Level);
        Assert.Equal("error", AutomationChecks.CheckVersion("[{}]", null, "dev").Level);
    }

    [Theory]
    [InlineData("[{\"when\":\"Version: 2.0.0\"}]")]
    [InlineData("[{\"when\":\"x\"},{\"_comment_\":\"Version: 2.0.0\"}]")]
    public void OnlyTheFirstTriggerCommentProvidesTheVersion(string json) =>
        Assert.Equal("error", AutomationChecks.CheckVersion(json, null, "feature/test").Level);

    [Fact]
    public void CSharpOnlyChangeNeedsJsonUpdateEvenWhenOldVersionIsMissing()
    {
        const string json = "[{\"when\":\"x\"}]";
        Assert.Equal("ok", AutomationChecks.CheckVersion(json, json, "feature/test").Level);
        Assert.Equal("error", AutomationChecks.CheckVersion(json, json, "feature/test", csharpChanged: true).Level);
    }

    [Fact]
    public void JsonWithCommentsIsAccepted() =>
        Assert.Equal("ok", AutomationChecks.CheckJson("// выключено\n[{\"a\": 1,}]").Level);

    [Fact]
    public void BrokenJsonIsRejected() =>
        Assert.Equal("error", AutomationChecks.CheckJson("[{\"a\": }]").Level);

    [Fact]
    public void CommitMessageFormats()
    {
        var dev = new EnvironmentSettings { Branch = "dev" };
        var main = new EnvironmentSettings { Branch = "main", RequireTaskReference = true };

        Assert.Equal("Check: обновлена проверка", AutomationChecks.BuildCommitMessage(dev, "Check", "обновлена проверка", null, out var e1));
        Assert.Null(e1);

        AutomationChecks.BuildCommitMessage(main, "Check", "добавлено поле", "", out var e2);
        Assert.NotNull(e2);

        Assert.Equal("Check: TASK-1, добавлено поле", AutomationChecks.BuildCommitMessage(main, "Check", "добавлено поле", "TASK-1", out var e3));
        Assert.Null(e3);
    }
}

public class AutomationFilesTests
{
    [Fact]
    public void PathsFollowRepositoryLayout()
    {
        Assert.Equal("Notify.json", AutomationFiles.JsonPath("Notify"));
        Assert.Equal("Notify/Notify.cs", AutomationFiles.CsPath("Notify"));
    }

    [Theory]
    [InlineData("NotifyOnApproval", true)]
    [InlineData("notify-on_approval.v2", true)]
    [InlineData("Уведомление", true)]
    [InlineData("Уведомление о задаче", true)]
    [InlineData("has space", true)]
    [InlineData("C++ helper", true)]
    [InlineData("-dash", false)]
    [InlineData(" leading", false)]
    [InlineData("with (parens)", false)]
    [InlineData("a/b", false)]
    [InlineData("repo.git", false)]
    public void RepositoryNameRules(string name, bool valid) =>
        Assert.Equal(valid, AutomationFiles.NameError(name) is null);

    [Theory]
    [InlineData("automation", "")]
    [InlineData("automation/notification", "notification")]
    [InlineData("automation/notification/mail", "notification/mail")]
    public void FolderIsSubgroupRelativeToRoot(string ns, string folder) =>
        Assert.Equal(folder, AutomationRepo.FolderOf(ns, "automation"));

    [Fact]
    public void RepoStateFindsJsonAndCs()
    {
        var repo = new AutomationRepo { Name = "A", ProjectPath = "automation/A", Folder = "", WebUrl = "" };
        var state = new AutomationCatalog.RepoState(repo, new List<GitLabTreeItem>
        {
            new() { Type = "blob", Path = "A.json", Id = "1" },
            new() { Type = "blob", Path = "A/A.cs", Id = "2" },
            new() { Type = "blob", Path = "README.md", Id = "3" },
        });
        Assert.Equal("1", state.JsonBlob);
        Assert.Equal("2", state.CsBlob);
        Assert.Null(new AutomationCatalog.RepoState(repo, null).JsonBlob);
    }

    [Fact]
    public void InitialJsonHasOnlyVersion()
    {
        var json = AutomationRepositories.InitialJson();
        Assert.Equal("ok", AutomationChecks.CheckJson(json).Level);
        Assert.Equal(new AutomationVersion(1, 0, 0), AutomationVersion.Find(json));
        Assert.Equal("v1.0.0", AutomationRepositories.TagName(new AutomationVersion(1, 0, 0)));
    }

    [Fact]
    public void CSharpOnlyChangeRequiresBump()
    {
        const string json = "[{\"_comment_\":\"Version: 1.0.1\"}]";
        Assert.Equal("ok", AutomationChecks.CheckVersion(json, json, "dev", csharpChanged: false).Level);
        Assert.Equal("error", AutomationChecks.CheckVersion(json, json, "dev", csharpChanged: true).Level);
    }

    [Theory]
    [InlineData("actualFor attribute management (Server and Client)", "actualFor attribute management Server and Client")]
    [InlineData("Clear dateOfCompletion by reassign task (Server)", "Clear dateOfCompletion by reassign task Server")]
    [InlineData("Уведомление о задаче", "Уведомление о задаче")]
    [InlineData("(Server) #1", "Server 1")]
    [InlineData("  ", "Automation")]
    public void SuggestedNameIsValid(string source, string expected)
    {
        var suggested = AutomationFiles.SuggestName(source);
        Assert.Equal(expected, suggested);
        Assert.Null(AutomationFiles.NameError(suggested));
    }

    [Fact]
    public void GitBlobShaMatchesGit() =>
        // git hash-object для "hello\n"
        Assert.Equal("ce013625030ba8dba906f756967f9e9ca394464a", AutomationFiles.GitBlobSha("hello\n"));

    [Fact]
    public void BlobMatchIgnoresLineEndings()
    {
        var sha = AutomationFiles.GitBlobSha("line1\nline2\n");
        Assert.True(AutomationFiles.MatchesBlob("line1\r\nline2", sha));
        Assert.False(AutomationFiles.MatchesBlob("line1\nline3", sha));
        Assert.True(AutomationFiles.MatchesBlob("", null));
    }

    [Fact]
    public void BranchSlugIsAscii()
    {
        Assert.Equal("notify-on-approval", AutomationFiles.BranchSlug("Notify On Approval"));
        Assert.Equal("uvedomlenie-o-zadache", AutomationFiles.BranchSlug("Уведомление о задаче"));
        Assert.Equal("automation", AutomationFiles.BranchSlug("###"));
    }
}

public class AddressTests
{
    [Theory]
    [InlineData("http://localhost:8080/automation", "http://localhost:8080", "automation")]
    [InlineData("http://gitlab.local/group/automation.git", "http://gitlab.local", "group/automation")]
    [InlineData("http://gitlab.local/group/automation/-/tree/dev", "http://gitlab.local", "group/automation")]
    public void ParsesGitLabLocation(string url, string baseUrl, string path)
    {
        Assert.True(GitLabLocation.TryParse(url, out var location, out _));
        Assert.Equal(baseUrl, location!.BaseUrl);
        Assert.Equal(path, location.Path);
    }

    [Theory]
    [InlineData("http://localhost:5547/pilot-ice_ru", "http://localhost:5547", "pilot-ice_ru")]
    [InlineData("http://localhost:5547", "http://localhost:5547", "")]
    [InlineData("http://srv:5545/%D0%B1%D0%B0%D0%B7%D0%B0", "http://srv:5545", "база")]
    public void ParsesPilotAddress(string url, string server, string database)
    {
        Assert.True(PilotAddress.TryParse(url, out var address, out _));
        Assert.Equal(server, address!.ServerUrl);
        Assert.Equal(database, address.Database);
    }

    [Fact]
    public void LocalhostIsMappedOnlyWhenAliasSet()
    {
        var mapper = new NetworkAddressMapper("host.docker.internal");
        Assert.Equal("http://host.docker.internal:5547/db", mapper.ToInternal("http://localhost:5547/db"));
        Assert.Equal("http://host.docker.internal:8080", mapper.ToInternal("http://127.0.0.1:8080"));
        Assert.Equal("http://localhostess:1", mapper.ToInternal("http://localhostess:1"));
        Assert.Equal("http://10.0.0.5:5545", mapper.ToInternal("http://10.0.0.5:5545"));
        Assert.Equal("http://localhost:1", new NetworkAddressMapper("").ToInternal("http://localhost:1"));
    }
}

public class RollbackVersionTests
{
    [Fact]
    public void WithVersionReplacesFirstVersion()
    {
        const string json = "[\n  { \"_comment_\": \"Version: 1.0.1. Старое описание\" },\n  { \"when\": \"ItemCreated\" }\n]";
        var result = AutomationVersion.WithVersion(json, new AutomationVersion(1, 0, 3));
        Assert.Equal(new AutomationVersion(1, 0, 3), AutomationVersion.Find(result));
        Assert.Contains("Старое описание", result);
        Assert.Contains("ItemCreated", result);
    }

    [Fact]
    public void WithVersionAddsEntryWhenMissing()
    {
        var result = AutomationVersion.WithVersion("[\n  {\n    \"when\": \"ItemCreated\"\n  }\n]", new AutomationVersion(2, 0, 0));
        Assert.Equal("ok", AutomationChecks.CheckJson(result).Level);
        Assert.Equal(new AutomationVersion(2, 0, 0), AutomationVersion.Find(result));
        Assert.Equal("[\n  {\n    \"_comment_\": \"Version: 2.0.0\",\n    \"when\": \"ItemCreated\"\n  }\n]", result);
    }

    [Fact]
    public void WithVersionMovesStandaloneCommentIntoTrigger()
    {
        var result = AutomationVersion.WithVersion(StandaloneJson, new AutomationVersion(1, 0, 3));
        Assert.Equal(new AutomationVersion(1, 0, 3), AutomationVersion.Find(result));
        AssertEveryElementHasWhen(result);
    }

    // Отдельная запись { "_comment_" } без "when" роняет клиент Pilot (ArgumentNullException в RebuildSnapshot).
    private const string StandaloneJson = "[\n  { \"_comment_\": \"Version: 1.0.0\" },\n  {\n    \"when\": \"AttributeChanged\",\n    \"then\": []\n  }\n]\n";

    [Fact]
    public void StorageLayoutMovesStandaloneComment()
    {
        var result = AutomationVersion.ToStorageLayout(StandaloneJson);
        Assert.Equal("[\n  {\n    \"_comment_\": \"Version: 1.0.0\",\n    \"when\": \"AttributeChanged\",\n    \"then\": []\n  }\n]\n", result);
        Assert.False(AutomationVersion.HasStandaloneComment(result));
        Assert.Equal(result, AutomationVersion.ToStorageLayout(result));
    }

    // Свой _comment_ с описанием у триггера - версия дописывается в него, второго ключа _comment_ нет.
    private const string DescribedJson = "[\n  {\n    \"_comment_\": \"Изменение статуса бланка задания\",\n    \"when\": \"StateAttributeChanged\",\n    \"params\": { \"_comment_\": \"вложенный\" }\n  }\n]\n";

    [Fact]
    public void WithVersionPrependsToExistingTriggerComment()
    {
        var result = AutomationVersion.WithVersion(DescribedJson, new AutomationVersion(1, 0, 1));
        Assert.Contains("\"_comment_\": \"Version: 1.0.1. Изменение статуса бланка задания\"", result);
        Assert.Contains("\"вложенный\"", result);
        Assert.Equal(2, result.Split("\"_comment_\"").Length - 1);
        AssertEveryElementHasWhen(result);

        var bumped = AutomationVersion.WithVersion(result, new AutomationVersion(1, 0, 2));
        Assert.Contains("\"Version: 1.0.2. Изменение статуса бланка задания\"", bumped);
    }

    [Fact]
    public void StorageLayoutMergesStandaloneIntoExistingTriggerComment()
    {
        var json = "[\n  { \"_comment_\": \"Version: 1.0.0\" },\n" + DescribedJson.TrimStart('[');
        var result = AutomationVersion.ToStorageLayout(json);
        Assert.Contains("\"_comment_\": \"Version: 1.0.0. Изменение статуса бланка задания\"", result);
        Assert.Equal(2, result.Split("\"_comment_\"").Length - 1);
        AssertEveryElementHasWhen(result);
    }

    [Fact]
    public void StorageLayoutWrapsRootObject()
    {
        var result = AutomationVersion.ToStorageLayout("{ \"when\": \"ItemCreated\", \"then\": [] }");
        AssertEveryElementHasWhen(result);
    }

    [Fact]
    public void StorageLayoutKeepsLoneVersionPlaceholder()
    {
        var json = AutomationRepositories.InitialJson();
        Assert.Equal(json, AutomationVersion.ToStorageLayout(json));
    }

    private static void AssertEveryElementHasWhen(string json)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal(System.Text.Json.JsonValueKind.Array, doc.RootElement.ValueKind);
        Assert.All(doc.RootElement.EnumerateArray(), e => Assert.True(e.TryGetProperty("when", out _)));
    }

    [Theory]
    [InlineData("v1.0.2", true)]
    [InlineData("v10.20.30", true)]
    [InlineData("prod-a1b2c3d", false)]
    [InlineData("v1.0", false)]
    public void TagParsing(string tag, bool ok) => Assert.Equal(ok, AutomationVersion.TryParseTag(tag, out _));
}
