using System.Net;
using System.Text;
using System.Text.Json;
using ProAdmin.GitLab;
using ProAdmin.Infrastructure;
using ProAdmin.Jobs;
using ProAdmin.Modules;
using ProAdmin.Pilot;
using ProAdmin.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ProAdmin.Tests;

/// <summary>Подготовка релиза модуля на фейковом GitLab: какие ветки, cherry-pick и MR создаются.</summary>
public class ModuleWorkflowTests
{
    private const string Project = "modules/openstoradge";

    private static readonly object[] DevCommits =
    {
        new { id = "c1c1c1c1c1", short_id = "c1c1c1c1", title = "KTN-101: экспорт", parent_ids = new[] { "p" } },
        new { id = "c2c2c2c2c2", short_id = "c2c2c2c2", title = "KTN-102: отложенная фича", parent_ids = new[] { "c1c1c1c1c1" } },
        new { id = "m0m0m0m0m0", short_id = "m0m0m0m0", title = "Merge branch 'x' into dev", parent_ids = new[] { "a", "b" } },
        new { id = "c3c3c3c3c3", short_id = "c3c3c3c3", title = "KTN-103: фикс", parent_ids = new[] { "c2c2c2c2c2" } },
    };

    [Fact]
    public async Task Selective_release_cherry_picks_chosen_tasks_into_prep_branch()
    {
        var gitlab = new FakeGitLab();
        var job = await RunPrepareAsync(gitlab, new[] { "c1c1c1c1c1", "c3c3c3c3c3" });

        Assert.Null(job.Error);
        Assert.Equal(new[] { "release/1.4.0 / main", "release-prep/1.4.0 / main" }, gitlab.CreatedBranches);
        Assert.Equal(new[] { "c1c1c1c1c1 / release-prep/1.4.0", "c3c3c3c3c3 / release-prep/1.4.0" }, gitlab.CherryPicks);
        var mr = Assert.Single(gitlab.MergeRequests);
        Assert.Equal("release-prep/1.4.0", mr.GetProperty("source_branch").GetString());
        Assert.Equal("release/1.4.0", mr.GetProperty("target_branch").GetString());
        Assert.False(mr.GetProperty("squash").GetBoolean());
        var description = mr.GetProperty("description").GetString()!;
        Assert.Contains("KTN-101: экспорт", description);
        Assert.Contains("Не входят в релиз", description);
        Assert.Contains("KTN-102", description);
    }

    [Fact]
    public async Task Full_release_uses_merge_request_from_dev()
    {
        var gitlab = new FakeGitLab();
        var job = await RunPrepareAsync(gitlab, new[] { "c1c1c1c1c1", "c2c2c2c2c2", "c3c3c3c3c3" });

        Assert.Null(job.Error);
        // Снимок головы dev: задачи, влитые в dev во время Code Review, в релиз не попадут.
        Assert.Equal(new[] { "release/1.4.0 / main", "release-prep/1.4.0 / devhead0000" }, gitlab.CreatedBranches);
        Assert.Empty(gitlab.CherryPicks);
        var mr = Assert.Single(gitlab.MergeRequests);
        Assert.Equal("release-prep/1.4.0", mr.GetProperty("source_branch").GetString());
        Assert.True(mr.GetProperty("remove_source_branch").GetBoolean());
    }

    [Fact]
    public async Task Tasks_released_as_cherry_pick_copies_are_not_released_again()
    {
        // Прошлый выборочный релиз перенёс KTN-101 в main копией: исходный коммит в dev остаётся «впереди» main.
        var gitlab = new FakeGitLab
        {
            MainCommits = new object[]
            {
                new { id = "k1k1k1k1k1", short_id = "k1k1k1k1", title = "KTN-101: экспорт", parent_ids = new[] { "p" },
                      message = "KTN-101: экспорт\n\n(cherry picked from commit c1c1c1c1c1)" },
            },
        };
        var job = await RunPrepareAsync(gitlab, new[] { "c1c1c1c1c1", "c2c2c2c2c2", "c3c3c3c3c3" });

        Assert.Null(job.Error);
        // KTN-101 уже на проде: остальные две - это все задачи релиза (снимок dev, содержимое KTN-101 в main уже есть),
        // в описании и CHANGELOG её нет.
        Assert.Empty(gitlab.CherryPicks);
        Assert.Contains("release-prep/1.4.0 / devhead0000", gitlab.CreatedBranches);
        var description = Assert.Single(gitlab.MergeRequests).GetProperty("description").GetString()!;
        Assert.DoesNotContain("KTN-101", description);
        Assert.Contains("KTN-102", description);
    }

    [Fact]
    public async Task Cherry_pick_conflict_removes_release_branches()
    {
        var gitlab = new FakeGitLab { ConflictOn = "c3c3c3c3c3" };
        var ex = await Assert.ThrowsAsync<WorkflowException>(() => RunPrepareAsync(gitlab, new[] { "c1c1c1c1c1", "c3c3c3c3c3" }));

        Assert.Contains("KTN-103", ex.Message);
        Assert.Equal(new[] { "release-prep/1.4.0", "release/1.4.0" }, gitlab.DeletedBranches);
        Assert.Empty(gitlab.MergeRequests);
    }

    [Fact]
    public async Task Replaced_release_is_kept_when_new_merge_request_fails()
    {
        var gitlab = new FakeGitLab { MergeRequestStatus = HttpStatusCode.Forbidden };
        var ex = await Assert.ThrowsAsync<WorkflowException>(() => RunPrepareAsync(gitlab, null, replaces: "release/1.3.0"));

        Assert.Contains("release/1.3.0 сохранён", ex.Message);
        Assert.DoesNotContain("release/1.3.0", gitlab.DeletedBranches);
        Assert.Contains("release/1.4.0", gitlab.DeletedBranches);
        Assert.Contains("release-prep/1.4.0", gitlab.DeletedBranches);
    }

    [Fact]
    public async Task Replaced_release_is_removed_after_replacement_is_ready()
    {
        var gitlab = new FakeGitLab();
        await RunPrepareAsync(gitlab, null, replaces: "release/1.3.0");

        Assert.Single(gitlab.MergeRequests);
        Assert.Equal(new[] { "release-prep/1.3.0", "release/1.3.0" }, gitlab.DeletedBranches);
    }

    [Fact]
    public async Task Stage_failure_before_deploy_removes_replacement_and_keeps_old_release()
    {
        var gitlab = new FakeGitLab();
        var ex = await Assert.ThrowsAsync<WorkflowException>(() => RunPrepareAsync(gitlab, null, replaces: "release/1.3.0",
            stage: (_, _) => throw new WorkflowException("Pipeline упал.")));

        Assert.Contains("release/1.3.0 сохранён", ex.Message);
        Assert.Contains("Ветки нового релиза удалены", ex.Message);
        Assert.DoesNotContain("release/1.3.0", gitlab.DeletedBranches);
        Assert.Contains("release/1.4.0", gitlab.DeletedBranches);
        Assert.Contains(5L, gitlab.ClosedMergeRequests);
    }

    [Fact]
    public async Task Stage_failure_after_deploy_keeps_deployed_release_and_retires_old()
    {
        // Сборка уже на стейдже, упал следующий шаг (список тегов - 503): ветку выложенной версии удалять нельзя.
        var gitlab = new FakeGitLab();
        var ex = await Assert.ThrowsAsync<WorkflowException>(() => RunPrepareAsync(gitlab, null, replaces: "release/1.3.0",
            stage: (s, _) => { s.Deployed = true; throw new WorkflowException("GitLab 503: теги недоступны."); }));

        Assert.Contains("release/1.4.0 уже выложен на стейдж", ex.Message);
        Assert.DoesNotContain("release/1.4.0", gitlab.DeletedBranches);
        Assert.Contains("release/1.3.0", gitlab.DeletedBranches);
        Assert.DoesNotContain(5L, gitlab.ClosedMergeRequests);
    }

    [Fact]
    public async Task Ci_deploy_stage_then_artifact_download_failure_keeps_release()
    {
        // deploy_stage в CI уже записал релиз на стейдж, а админка не скачала артефакт (503): Deployed ещё false,
        // но на стейдже коммит релиза - ветку не удаляем, старый релиз закрываем.
        var gitlab = new FakeGitLab();
        var ex = await Assert.ThrowsAsync<WorkflowException>(() => RunPrepareAsync(gitlab, null, replaces: "release/1.3.0",
            stage: (_, _) => throw new WorkflowException("GitLab 503: артефакты job недоступны."),
            probe: (_, _) => Task.FromResult<bool?>(true)));

        Assert.Contains("release/1.4.0 уже выложен на стейдж", ex.Message);
        Assert.DoesNotContain("release/1.4.0", gitlab.DeletedBranches);
        Assert.Contains("release/1.3.0", gitlab.DeletedBranches);
    }

    [Theory]
    [InlineData("aa11bb22cc33dd44", true)]   // deploy_stage в CI выложил голову релиза
    [InlineData("ee55ff66aa77bb88", true)]   // коммит релиза (не голова)
    [InlineData("0123456789abcdef", false)]   // на стейдже другая версия
    public async Task Stage_probe_reads_commit_actually_deployed_on_stage(string deployedSha, bool expected)
    {
        var stage = new StageTarget { Deployed = deployedSha };
        Assert.Equal(expected, await ProbeAsync(stage));
    }

    [Fact]
    public async Task Stage_probe_is_unknown_when_stage_server_does_not_answer()
    {
        Assert.Null(await ProbeAsync(new StageTarget { Fail = true }));
    }

    private static Task<bool?> ProbeAsync(StageTarget stage)
    {
        var gitlab = new FakeGitLab { ReleaseExists = true };
        var network = new NetworkAddressMapper(null);
        var auth = new GitLabAuthService(new NoFactory(), network, Options.Create(new AppOptions()));
        var token = new GitLabToken { AccessToken = "pat", BaseUrl = "http://gitlab.test" };
        var settings = new AdminSettings { StagePilotUrl = "http://pilot-stage.test/pilot-ice_ru" };
        settings.Normalize();
        var ctx = new ModuleContext
        {
            Settings = settings,
            GitLabToken = token,
            ProjectPath = Project,
            RepoName = "OpenStoradge",
            Logins = new Dictionary<string, PilotLogin> { [ModuleEnv.Stage] = new("svc", "pw") },
        };
        return new ModuleReleaseToStageWorkflow(stage, new GitLabClient(new HttpClient(gitlab), auth, network, token),
            new PublishJob { PilotUser = "tester" }, ctx, "release/1.4.0", 5).IsOnStageAsync(CancellationToken.None);
    }

    /// <summary>Сервер стейджа: модуль с причиной версии «… @ sha», как его пишет выкладка (админка или deploy_stage в CI).</summary>
    private sealed class StageTarget : IModuleTarget
    {
        public string Deployed { get; init; } = "";
        public bool Fail { get; init; }
        public ModuleKind Kind => ModuleKind.Client;
        public string AfterUpdateNote => "";
        public string? AfterCreateWarning(string extensionName, string envTitle) => null;

        public Task<PilotModuleList> ListAsync(string url, PilotLogin login, int licenseType, CancellationToken ct)
        {
            if (Fail)
                throw new HttpRequestException("сервер стейджа недоступен");
            var list = new PilotModuleList();
            list.Modules.Add(new PilotModule(Guid.NewGuid(), "114 - Хранилище - (OpenStoradge) - (CICD)", "Версия 1.4.0", "", Guid.Empty,
                "OpenStoradge.zip", null, 0, ModuleDeploy.Reason("1.4.0", "release/1.4.0", Deployed, null), null));
            return Task.FromResult(list);
        }

        public Task<byte[]?> DownloadAsync(string url, PilotLogin login, int licenseType, PilotModule module, CancellationToken ct) => throw new NotSupportedException();
        public Task UpdateAsync(string url, PilotLogin login, int licenseType, Guid moduleId, ModulePackageInfo package, string reason, CancellationToken ct) => throw new NotSupportedException();
        public Task<Guid> CreateAsync(string url, PilotLogin login, int licenseType, Guid folderId, ModulePackageInfo package, string reason, CancellationToken ct) => throw new NotSupportedException();
        public Task RestoreAsync(string url, PilotLogin login, int licenseType, PilotModule previous, byte[] zip, string reason, CancellationToken ct) => throw new NotSupportedException();
        public Task MoveToRecycleBinAsync(string url, PilotLogin login, int licenseType, Guid moduleId, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<PackageAssembly>?> AssembliesAsync(string url, PilotLogin login, int licenseType, PilotModule module, CancellationToken ct) => throw new NotSupportedException();
    }

    [Fact]
    public async Task Unknown_stage_state_keeps_both_releases()
    {
        // Ни сервер стейджа, ни GitLab не ответили: что выложено - неизвестно, ничего не удаляем.
        var gitlab = new FakeGitLab();
        var ex = await Assert.ThrowsAsync<WorkflowException>(() => RunPrepareAsync(gitlab, null, replaces: "release/1.3.0",
            stage: (_, _) => throw new WorkflowException("GitLab 503: артефакты job недоступны."),
            probe: (_, _) => Task.FromResult<bool?>(null)));

        Assert.Contains("установить не удалось", ex.Message);
        Assert.Empty(gitlab.DeletedBranches);
        Assert.DoesNotContain(5L, gitlab.ClosedMergeRequests);
    }

    [Fact]
    public async Task Failed_cleanup_is_reported_instead_of_claiming_branch_removed()
    {
        var gitlab = new FakeGitLab { FailDelete = { "release/1.4.0" } };
        var ex = await Assert.ThrowsAsync<WorkflowException>(() => RunPrepareAsync(gitlab, null, replaces: "release/1.3.0",
            stage: (_, _) => throw new WorkflowException("Pipeline упал.")));

        Assert.Contains("Не удалось удалить ветки release/1.4.0", ex.Message);
        Assert.DoesNotContain("удалены", ex.Message);
        Assert.DoesNotContain("сохранён - его можно продолжить", ex.Message);
    }

    private static async Task<PublishJob> RunPrepareAsync(FakeGitLab gitlab, IReadOnlyCollection<string>? selected, string? replaces = null,
        Func<ModuleReleaseToStageWorkflow, CancellationToken, Task>? stage = null,
        Func<ModuleReleaseToStageWorkflow, CancellationToken, Task<bool?>>? probe = null)
    {
        var network = new NetworkAddressMapper(null);
        var auth = new GitLabAuthService(new NoFactory(), network, Options.Create(new AppOptions()));
        var token = new GitLabToken { AccessToken = "pat", BaseUrl = "http://gitlab.test" };
        var git = new GitLabClient(new HttpClient(gitlab), auth, network, token);
        var pilot = new PilotModuleGateway(new PilotReportGateway(network, NullLogger<PilotReportGateway>.Instance));

        var settings = new AdminSettings();
        settings.Normalize();
        var ctx = new ModuleContext
        {
            Settings = settings,
            GitLabToken = token,
            ProjectPath = Project,
            RepoName = "OpenStoradge",
            Logins = new Dictionary<string, PilotLogin>(),
        };
        var job = new PublishJob { PilotUser = "tester" };
        // Без stage - только ветки и MR; со stage - подменённый шаг «стейдж» (влитие, сборка, выкладка - ModuleReleaseToStageWorkflow).
        await new ModulePrepareReleaseWorkflow(pilot, git, job, ctx, new ModuleVersion(1, 4, 0), selected, Array.Empty<string>(),
                deployToStage: stage is not null, replaces: replaces)
            {
                RunStage = stage ?? ((w, ct) => w.RunAsync(ct)),
                // Без подмены - настоящая проверка: сервер стейджа в тестовых настройках не задан, значит «не выложен».
                ProbeStage = probe ?? ((w, ct) => w.IsOnStageAsync(ct)),
            }
            .RunAsync(CancellationToken.None);
        return job;
    }

    private sealed class NoFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new InvalidOperationException("не нужен для PAT");
    }

    private sealed class FakeGitLab : HttpMessageHandler
    {
        public string? ConflictOn { get; init; }
        public HttpStatusCode MergeRequestStatus { get; init; } = HttpStatusCode.Created;
        /// <summary>Ветки, удаление которых GitLab отклоняет (503).</summary>
        public HashSet<string> FailDelete { get; } = new();
        public List<long> ClosedMergeRequests { get; } = new();
        /// <summary>Ветка release/1.4.0 уже есть (проверка стейджа после сбоя).</summary>
        public bool ReleaseExists { get; init; }
        /// <summary>История main (для копий задач, выпущенных выборочным релизом).</summary>
        public object[] MainCommits { get; init; } = Array.Empty<object>();
        public List<string> CreatedBranches { get; } = new();
        public List<string> DeletedBranches { get; } = new();
        public List<string> CherryPicks { get; } = new();
        public List<JsonElement> MergeRequests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = Uri.UnescapeDataString(request.RequestUri!.AbsolutePath).Replace("/api/v4/projects/" + Project, "");
            var query = Uri.UnescapeDataString(request.RequestUri.Query);
            var body = request.Content is null ? default : JsonDocument.Parse(await request.Content.ReadAsStringAsync(ct)).RootElement.Clone();
            var method = request.Method.Method;

            if (ReleaseExists && method == "GET" && path == "/repository/branches/release/1.4.0")
                return Json(HttpStatusCode.OK, new { name = "release/1.4.0", commit = new { id = "aa11bb22cc33dd44", short_id = "aa11bb22" } });
            if (method == "GET" && path == "/repository/compare" && query.Contains("to=release/1.4.0"))
                return Json(HttpStatusCode.OK, new { commits = new[] { new { id = "ee55ff66aa77bb88", short_id = "ee55ff66", title = "Релиз 1.4.0" } } });
            if (method == "GET" && path == "/repository/branches/dev")
                return Json(HttpStatusCode.OK, new { name = "dev", commit = new { id = "devhead0000", short_id = "devhead0" } });
            if (method == "GET" && path.StartsWith("/repository/branches/"))
                return Json(HttpStatusCode.NotFound, new { message = "404 Branch Not Found" });
            if (method == "GET" && path == "/repository/compare")
            {
                Assert.Contains("from=main", query);
                Assert.Contains("to=devhead0000", query);
                return Json(HttpStatusCode.OK, new { commits = DevCommits });
            }
            if (method == "GET" && path == "/repository/commits")
            {
                Assert.Contains("ref_name=main", query);
                return Json(HttpStatusCode.OK, MainCommits);
            }
            if (method == "POST" && path == "/repository/branches")
            {
                CreatedBranches.Add($"{body.GetProperty("branch").GetString()} / {body.GetProperty("ref").GetString()}");
                return Json(HttpStatusCode.Created, new { name = body.GetProperty("branch").GetString() });
            }
            if (method == "DELETE" && path.StartsWith("/repository/branches/"))
            {
                if (FailDelete.Contains(path["/repository/branches/".Length..]))
                    return Json(HttpStatusCode.ServiceUnavailable, new { message = "503 Service Unavailable" });
                DeletedBranches.Add(path["/repository/branches/".Length..]);
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }
            if (method == "POST" && path.StartsWith("/repository/commits/") && path.EndsWith("/cherry_pick"))
            {
                var sha = path.Split('/')[3];
                if (sha == ConflictOn)
                    return Json(HttpStatusCode.BadRequest, new { message = "Sorry, we cannot cherry-pick this commit automatically." });
                CherryPicks.Add($"{sha} / {body.GetProperty("branch").GetString()}");
                return Json(HttpStatusCode.Created, new { id = "new" + sha, short_id = "new" + sha[..4] });
            }
            if (method == "GET" && path == "/merge_requests/5")
                return Json(HttpStatusCode.OK, new { id = 1, iid = 5, state = "opened", sha = "prephead0000", target_branch = "release/1.4.0", web_url = "http://gitlab.test/mr/5" });
            if (method == "PUT" && path == "/merge_requests/5")
            {
                ClosedMergeRequests.Add(5);
                return Json(HttpStatusCode.OK, new { id = 1, iid = 5, state = "closed" });
            }
            if (method == "GET" && path == "/merge_requests")
                return Json(HttpStatusCode.OK, Array.Empty<object>());
            if (method == "POST" && path == "/merge_requests")
            {
                if (MergeRequestStatus != HttpStatusCode.Created)
                    return Json(MergeRequestStatus, new { message = "403 Forbidden" });
                MergeRequests.Add(body);
                return Json(HttpStatusCode.Created, new { id = 1, iid = 5, state = "opened", web_url = "http://gitlab.test/mr/5" });
            }
            return Json(HttpStatusCode.NotImplemented, new { message = $"не ожидался {method} {path}{query}" });
        }

        private static HttpResponseMessage Json(HttpStatusCode code, object value) =>
            new(code) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    }
}
