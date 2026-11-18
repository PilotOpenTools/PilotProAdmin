using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using Ascon.Pilot.DataClasses;
using ProAdmin.GitLab;
using ProAdmin.Infrastructure;
using ProAdmin.Jobs;
using ProAdmin.Modules;
using ProAdmin.Pilot;
using ProAdmin.Settings;
using ProAdmin.Types;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ProAdmin.Tests;

/// <summary>Перенос коммитов типов release / main на фейковом GitLab: выбор, пометки cherry-pick, версия; связи изменений с модулями.</summary>
public class TypesWorkflowTests : IDisposable
{
    private const string Project = "pilot-config/types";
    private readonly string _data = Path.Combine(Path.GetTempPath(), "types-wf-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_data, true); } catch { /* временная папка */ }
    }

    [Fact]
    public async Task Prepare_to_main_picks_selected_and_stops_before_server()
    {
        var gitlab = new FakeGitLab();
        var store = new TypesStore(Options.Create(new AppOptions { DataDirectory = _data }));

        var (job, prepared) = await PrepareAsync(gitlab, store, new[] { "r1r1r1r1r1", "r3r3r3r3r3" });

        Assert.Null(job.Error);
        // r2 уже перенесён раньше (пометка в main) и не предлагается; r9 не выбран.
        Assert.Equal(new[] { "r1r1r1r1r1", "r3r3r3r3r3" }, gitlab.CherryPicks.Select(p => p.Sha));
        Assert.All(gitlab.CherryPicks, p => Assert.Contains($"(cherry picked from commit {p.Sha})", p.Message));
        Assert.Contains("(cherry picked from commit d1d1d1d1d1)", gitlab.CherryPicks[0].Message); // пометка из dev сохраняется

        var commit = Assert.Single(gitlab.Commits);
        Assert.StartsWith(TypesRepository.ReleaseCommitPrefix + "1.3.0", commit.GetProperty("commit_message").GetString());
        var files = commit.GetProperty("actions").EnumerateArray().Select(a => a.GetProperty("file_path").GetString()).ToList();
        Assert.Equal(new[] { "config.json", "CHANGELOG.md" }, files);
        var changelog = commit.GetProperty("actions")[1].GetProperty("content").GetString()!;
        Assert.Contains("[1.3.0]", changelog);
        Assert.Contains("- KTN-1: тип contract", changelog);

        var mr = Assert.Single(gitlab.MergeRequests);
        Assert.Equal("main", mr.GetProperty("target_branch").GetString());
        Assert.Empty(gitlab.Merges); // вливает следующий шаг (после записи на сервер / выпуска модуля)
        Assert.Empty(gitlab.DeletedBranches); // ветка переноса живёт до merge

        Assert.Equal("promotehead", prepared.Head);
        Assert.Equal("mainhead00", prepared.BaseSha);
        Assert.Equal(5, prepared.Mr.Iid);
        Assert.Contains("contract", prepared.Snapshot.Types.Keys);
    }

    [Fact]
    public async Task Cherry_pick_conflict_cleans_up_and_keeps_server()
    {
        var gitlab = new FakeGitLab { ConflictOn = "r3r3r3r3r3" };
        var store = new TypesStore(Options.Create(new AppOptions { DataDirectory = _data }));
        var ex = await Assert.ThrowsAsync<WorkflowException>(() => PrepareAsync(gitlab, store, new[] { "r1r1r1r1r1", "r3r3r3r3r3" }));

        Assert.Contains("KTN-3", ex.Message);
        Assert.Single(gitlab.DeletedBranches);
        Assert.StartsWith(TypesBranches.PromotePrefix + "main-", gitlab.DeletedBranches[0]);
        Assert.Empty(gitlab.MergeRequests);
    }

    [Fact]
    public void Links_follow_branch_merge_and_target()
    {
        var store = new TypesStore(Options.Create(new AppOptions { DataDirectory = _data }));
        store.AddLink(new TypesChangeLink
        {
            TypesBranch = "types/KTN-5-dogovor", TypesTitle = "ветка", TargetKind = TypesTargetKind.Client,
            TargetProject = "client-modules/M", TargetName = "M", TargetSha = "34331ec0aa", TargetTitle = "KTN-5: модуль",
        });
        store.AddLink(new TypesChangeLink { TypesSha = "d2", TargetKind = TypesTargetKind.Automation, TargetProject = "automation/a", ProdAt = DateTimeOffset.UtcNow });

        Assert.Single(store.OpenLinksFor(TypesTargetKind.Client, "client-modules/m")); // проект без учёта регистра
        Assert.Empty(store.OpenLinksFor(TypesTargetKind.Automation, "automation/a")); // выпущенная не открыта
        Assert.Empty(store.OpenLinksFor(TypesTargetKind.Server, "client-modules/m"));

        store.BranchMerged("types/KTN-5-dogovor", "squash1234567", "KTN-5: Договор");
        var link = Assert.Single(store.OpenLinksFor(TypesTargetKind.Client, "client-modules/M"));
        Assert.Equal("squash1234567", link.TypesSha);
        Assert.Equal("KTN-5: Договор", link.TypesTitle);
        Assert.True(link.IsChange("squash1234567", null));
        Assert.True(link.IsChange("squash12", null)); // короткий sha
        Assert.False(link.IsChange(null, "types/KTN-5-dogovor")); // ветка влита - связь теперь у коммита
        Assert.Equal("34331ec0", link.TargetRef);

        // Переживает перезапуск.
        var reloaded = new TypesStore(Options.Create(new AppOptions { DataDirectory = _data }));
        Assert.Equal(2, reloaded.ChangeLinks().Count);
        Assert.True(reloaded.RemoveLink(link.Id));
        Assert.Single(reloaded.ChangeLinks());
    }

    private static async Task<(PublishJob Job, TypesPromoteWorkflow.Prepared Prepared)> PrepareAsync(FakeGitLab gitlab, TypesStore store,
        IReadOnlyCollection<string> selected)
    {
        var network = new NetworkAddressMapper(null);
        var auth = new GitLabAuthService(new NoFactory(), network, Options.Create(new AppOptions()));
        var token = new GitLabToken { AccessToken = "pat", BaseUrl = "http://gitlab.test" };
        var git = new GitLabClient(new HttpClient(gitlab), auth, network, token);
        var pilot = new PilotGateway(network, NullLogger<PilotGateway>.Instance);

        var settings = new AdminSettings { AllowPublishWithoutCi = true }; // у фейкового GitLab нет файла CI, проверяется другое
        settings.Normalize();
        settings.Find(AdminSettings.Prod)!.PilotUrl = "http://127.0.0.1:9/pilot"; // сервер в подготовке не трогается
        var ctx = new TypesContext
        {
            Settings = settings,
            GitLabToken = token,
            ProjectPath = Project,
            Logins = new Dictionary<string, PilotLogin> { [AdminSettings.Prod] = new("admin", "x") },
        };
        var job = new PublishJob { PilotUser = "tester" };
        var prepared = await new TypesPromoteWorkflow(pilot, git, job, ctx, store, TypesBranches.Main, selected, new ModuleVersion(1, 3, 0),
                new[] { "KTN-1: тип contract", "KTN-3: состояние" }, null)
            .PrepareAsync(CancellationToken.None);
        return (job, prepared);
    }

    private sealed class NoFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new InvalidOperationException("не нужен для PAT");
    }

    private static byte[] Archive()
    {
        var m = new DMetadata
        {
            Types = { new() { Id = 1, Name = "contract", Title = "Договор" } },
        };
        var files = TypesExport.FromMetadata(m, "1.3.0").Render();
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (path, text) in files)
            {
                using var w = new StreamWriter(zip.CreateEntry("types-promotehead/" + path).Open(), new UTF8Encoding(false));
                w.Write(text);
            }
        return buffer.ToArray();
    }

    private sealed class FakeGitLab : HttpMessageHandler
    {
        public string? ConflictOn { get; init; }
        public List<string> CreatedBranches { get; } = new();
        public List<string> DeletedBranches { get; } = new();
        public List<(string Sha, string Message)> CherryPicks { get; } = new();
        public List<JsonElement> Commits { get; } = new();
        public List<JsonElement> MergeRequests { get; } = new();
        public List<string> Merges { get; } = new();

        private static readonly object[] ReleaseCommits =
        {
            new { id = "r1r1r1r1r1", short_id = "r1r1r1r1", title = "KTN-1: тип contract", message = "KTN-1: тип contract\n\n(cherry picked from commit d1d1d1d1d1)", parent_ids = new[] { "p" } },
            new { id = "r2r2r2r2r2", short_id = "r2r2r2r2", title = "KTN-2: уже в main", message = "KTN-2: уже в main", parent_ids = new[] { "r1" } },
            new { id = "mmmmmmmmmm", short_id = "mmmmmmmm", title = "Merge branch 'types-promote/release-1' into 'release'", parent_ids = new[] { "a", "b" } },
            new { id = "r3r3r3r3r3", short_id = "r3r3r3r3", title = "KTN-3: состояние", message = "KTN-3: состояние", parent_ids = new[] { "r2" } },
            new { id = "r9r9r9r9r9", short_id = "r9r9r9r9", title = "KTN-9: не выбран", message = "KTN-9: не выбран", parent_ids = new[] { "r3" } },
        };

        private static readonly object[] MainOnly =
        {
            new { id = "x2x2x2x2x2", short_id = "x2x2x2x2", title = "KTN-2: уже в main", message = "KTN-2: уже в main\n\n(cherry picked from commit r2r2r2r2r2)", parent_ids = new[] { "p" } },
        };

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = Uri.UnescapeDataString(request.RequestUri!.AbsolutePath).Replace("/api/v4/projects/" + Project, "");
            var query = Uri.UnescapeDataString(request.RequestUri.Query);
            var body = request.Content is null ? default : JsonDocument.Parse(await request.Content.ReadAsStringAsync(ct)).RootElement.Clone();
            var method = request.Method.Method;

            if (method == "GET" && path == "/repository/compare")
                return Json(HttpStatusCode.OK, new { commits = query.Contains("from=main") ? ReleaseCommits : MainOnly });
            if (method == "GET" && path == "/repository/branches/main")
                return Json(HttpStatusCode.OK, new { name = "main", commit = new { id = "mainhead00", short_id = "mainhead" } });
            if (method == "GET" && path.StartsWith("/repository/branches/" + TypesBranches.PromotePrefix))
                return Json(HttpStatusCode.OK, new { name = "p", commit = new { id = "promotehead", short_id = "promoteh" } });
            if (method == "POST" && path == "/repository/branches")
            {
                CreatedBranches.Add($"{body.GetProperty("branch").GetString()} / {body.GetProperty("ref").GetString()}");
                return Json(HttpStatusCode.Created, new { name = body.GetProperty("branch").GetString() });
            }
            if (method == "DELETE" && path.StartsWith("/repository/branches/"))
            {
                DeletedBranches.Add(path["/repository/branches/".Length..]);
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }
            if (method == "POST" && path.EndsWith("/cherry_pick"))
            {
                var sha = path.Split('/')[3];
                if (sha == ConflictOn)
                    return Json(HttpStatusCode.BadRequest, new { message = "Sorry, we cannot cherry-pick this commit automatically." });
                CherryPicks.Add((sha, body.GetProperty("message").GetString()!));
                return Json(HttpStatusCode.Created, new { id = "new" + sha, short_id = "new" + sha[..4] });
            }
            if (method == "GET" && path == "/repository/files/config.json")
                return Json(HttpStatusCode.OK, new { file_path = "config.json", encoding = "base64", content = Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"version\":\"1.2.0\"}")) });
            if (method == "GET" && path.StartsWith("/repository/files/"))
                return Json(HttpStatusCode.NotFound, new { message = "404 File Not Found" });
            if (method == "POST" && path == "/repository/commits")
            {
                Commits.Add(body);
                return Json(HttpStatusCode.Created, new { id = "vercommit", short_id = "vercommi" });
            }
            if (method == "GET" && path == "/repository/archive.zip")
            {
                Assert.Contains("sha=promotehead", query);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Archive()) };
            }
            if (method == "GET" && path == "/repository/tree")
                return Json(HttpStatusCode.OK, Array.Empty<object>());
            if (method == "POST" && path == "/merge_requests")
            {
                MergeRequests.Add(body);
                return Json(HttpStatusCode.Created, new { id = 1, iid = 5, state = "opened", web_url = "http://gitlab.test/mr/5", source_branch = body.GetProperty("source_branch").GetString() });
            }
            if (method == "PUT" && path.StartsWith("/merge_requests/"))
            {
                Merges.Add(path);
                return Json(HttpStatusCode.OK, new { iid = 5, state = "closed" });
            }
            return Json(HttpStatusCode.NotImplemented, new { message = $"не ожидался {method} {path}{query}" });
        }

        private static HttpResponseMessage Json(HttpStatusCode code, object value) =>
            new(code) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    }
}
