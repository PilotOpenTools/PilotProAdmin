using System.Net;
using System.Text;
using System.Text.Json;
using ProAdmin.Infrastructure;
using ProAdmin.Modules;
using ProAdmin.Settings;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ProAdmin.Tests;

/// <summary>
/// Выкладка из CI: два доказательства (job-токен, подтверждённый GitLab, и ключ контура), ветка и проект берутся только
/// из ответа GitLab, проект обязан лежать в группе модулей, ограничение перебора.
/// </summary>
public sealed class CiAuthTests : IDisposable
{
    private const string ClientGroup = "client-modules";
    private const string ServerGroup = "server-modules";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ciauth-" + Guid.NewGuid().ToString("N"));
    private readonly FakeGitLab _gitlab = new();
    private readonly AppOptions _options = new();
    private readonly AuthFailureLimiter _limiter = new();
    private readonly CiAuth _auth;

    public CiAuthTests()
    {
        _options.DataDirectory = _dir;
        _options.CiDeploy.Keys["test"] = "key-test";
        _options.CiDeploy.Keys["stage"] = "key-stage";
        _options.CiDeploy.Keys["prod"] = "key-prod";
        var store = new SettingsStore(Options.Create(_options), NullLogger<SettingsStore>.Instance);
        var settings = store.Current;
        settings.ModulesGitLabGroupUrl = "http://gitlab.test/" + ClientGroup;
        settings.ServerModulesGitLabGroupUrl = "http://gitlab.test/" + ServerGroup;
        store.Save(settings);
        var verifier = new CiJobVerifier(new SingleClient(_gitlab), new NetworkAddressMapper(null), NullLogger<CiJobVerifier>.Instance);
        _auth = new CiAuth(Options.Create(_options), store, verifier, _limiter, NullLogger<CiAuth>.Instance);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { /* временная папка */ }
    }

    [Theory]
    [InlineData("dev", "test", "key-test")]
    [InlineData("release/1.2.0", "stage", "key-stage")]
    [InlineData("main", "prod", "key-prod")]
    [InlineData("master", "prod", "key-prod")]
    public async Task Accepts_token_and_key_of_the_contour_of_the_verified_branch(string branch, string env, string key)
    {
        _gitlab.Job = JobJson(branch);
        var result = await _auth.AuthorizeAsync(Request("tok", key), ModuleKind.Client, requireDeployKey: true, default);

        Assert.True(result.Ok);
        Assert.Equal(env, result.Env);
        Assert.Equal(branch, result.Job!.Ref);
        Assert.Equal(ClientGroup + "/mod1", result.Job.ProjectPath);
        Assert.Equal("abc123", result.Job.Sha);
        Assert.Equal("dev1", result.Job.User);
        Assert.Equal(7, result.Job.PipelineId);
        Assert.Equal("http://gitlab.test/client-modules/mod1/-/pipelines/7", result.Job.PipelineUrl);
        Assert.Equal("http://gitlab.test/client-modules/mod1", result.Job.ProjectUrl);
    }

    [Fact]
    public async Task Asks_gitlab_for_the_job_with_the_job_token_header()
    {
        _gitlab.Job = JobJson("dev");
        await _auth.AuthorizeAsync(Request("secret-token", "key-test"), ModuleKind.Client, true, default);

        Assert.Equal(HttpMethod.Get, _gitlab.Last!.Method);
        Assert.Equal("http://gitlab.test/api/v4/job", _gitlab.Last.RequestUri!.ToString());
        Assert.Equal("secret-token", Assert.Single(_gitlab.Last.Headers.GetValues("JOB-TOKEN")));
    }

    [Fact]
    public async Task Job_token_is_required()
    {
        var (status, text) = Describe(await _auth.AuthorizeAsync(Request(null, "key-test"), ModuleKind.Client, true, default));
        Assert.Equal(401, status);
        Assert.Contains("X-Job-Token", text);
        Assert.Null(_gitlab.Last); // GitLab без токена не спрашиваем
    }

    [Fact]
    public async Task Token_gitlab_does_not_confirm_is_401()
    {
        _gitlab.Status = HttpStatusCode.Unauthorized;
        var (status, _) = Describe(await _auth.AuthorizeAsync(Request("bad", "key-test"), ModuleKind.Client, true, default));
        Assert.Equal(401, status);
    }

    [Theory]
    [InlineData(HttpStatusCode.Found)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task Client_side_non_200_from_gitlab_is_401(HttpStatusCode code)
    {
        _gitlab.Job = JobJson("main");
        _gitlab.Status = code;
        Assert.Equal(401, Describe(await _auth.AuthorizeAsync(Request("t", "key-prod"), ModuleKind.Client, true, default)).Status);
    }

    [Fact]
    public async Task Gitlab_outage_is_503_and_not_counted_as_a_failed_attempt()
    {
        _gitlab.Throw = new HttpRequestException("нет связи");
        for (var i = 0; i < 12; i++)
            Assert.Equal(503, Describe(await _auth.AuthorizeAsync(Request("t", "key-test"), ModuleKind.Client, true, default)).Status);

        _gitlab.Throw = null;
        _gitlab.Status = HttpStatusCode.InternalServerError;
        Assert.Equal(503, Describe(await _auth.AuthorizeAsync(Request("t", "key-test"), ModuleKind.Client, true, default)).Status);

        _gitlab.Status = HttpStatusCode.OK;
        _gitlab.Job = JobJson("dev");
        Assert.True((await _auth.AuthorizeAsync(Request("t", "key-test"), ModuleKind.Client, true, default)).Ok);
    }

    [Fact]
    public async Task Broken_job_answer_is_rejected()
    {
        _gitlab.Job = "не json {";
        Assert.Equal(503, Describe(await _auth.AuthorizeAsync(Request("t", "key-test"), ModuleKind.Client, true, default)).Status);

        _gitlab.Job = """{"ref":"dev"}""";
        Assert.Equal(401, Describe(await _auth.AuthorizeAsync(Request("t", "key-test"), ModuleKind.Client, true, default)).Status);
    }

    [Theory]
    [InlineData("other-group/mod1")]
    [InlineData("client-modules")]
    [InlineData("client-modules-evil/mod1")]
    [InlineData("server-modules/mod1")]
    public async Task Project_must_be_inside_the_client_modules_group(string project)
    {
        _gitlab.Job = JobJson("dev", project);
        var (status, text) = Describe(await _auth.AuthorizeAsync(Request("t", "key-test"), ModuleKind.Client, true, default));
        Assert.Equal(403, status);
        Assert.Contains("не входит в группу", text);
    }

    [Fact]
    public async Task Server_modules_use_the_server_group()
    {
        _gitlab.Job = JobJson("dev", ServerGroup + "/ext");
        Assert.True((await _auth.AuthorizeAsync(Request("t", "key-test"), ModuleKind.Server, true, default)).Ok);

        _gitlab.Job = JobJson("dev", ClientGroup + "/mod1");
        Assert.Equal(403, Describe(await _auth.AuthorizeAsync(Request("t", "key-test"), ModuleKind.Server, true, default)).Status);
    }

    [Fact]
    public async Task Contour_key_must_match_the_verified_branch_not_another_contour()
    {
        // Ветка main подтверждена GitLab: ключ теста не открывает прод, как бы ни назвали ref в форме.
        _gitlab.Job = JobJson("main");
        var (status, text) = Describe(await _auth.AuthorizeAsync(Request("t", "key-test"), ModuleKind.Client, true, default));
        Assert.Equal(401, status);
        Assert.Contains("DEPLOY_KEY_PROD", text);
    }

    [Fact]
    public async Task Missing_contour_key_names_the_variable_to_set()
    {
        _options.CiDeploy.Keys["stage"] = "";
        _gitlab.Job = JobJson("release/1.0.0");
        var (status, text) = Describe(await _auth.AuthorizeAsync(Request("t", "anything"), ModuleKind.Client, true, default));
        Assert.Equal(503, status);
        Assert.Contains("MODULES_DEPLOY_KEY_STAGE", text);
        Assert.Contains("App__CiDeploy__Keys__stage", text);
    }

    [Fact]
    public async Task Legacy_shared_key_is_accepted_only_for_test_and_only_without_test_key()
    {
        _options.CiDeploy.Keys.Clear();
        _options.CiDeploy.ApiKey = "legacy";

        _gitlab.Job = JobJson("dev");
        Assert.True((await _auth.AuthorizeAsync(Request("t", "legacy"), ModuleKind.Client, true, default)).Ok);

        foreach (var branch in new[] { "release/1.0.0", "main" })
        {
            _gitlab.Job = JobJson(branch);
            var (status, text) = Describe(await _auth.AuthorizeAsync(Request("t", "legacy"), ModuleKind.Client, true, default));
            Assert.Equal(503, status);
            Assert.Contains("не принимается", text);
        }

        _options.CiDeploy.Keys["test"] = "key-test";
        _gitlab.Job = JobJson("dev");
        Assert.Equal(401, Describe(await _auth.AuthorizeAsync(Request("t", "legacy"), ModuleKind.Client, true, default)).Status);
        Assert.True((await _auth.AuthorizeAsync(Request("t", "key-test"), ModuleKind.Client, true, default)).Ok);
    }

    [Fact]
    public async Task Branches_that_are_not_deployed_are_400()
    {
        _gitlab.Job = JobJson("feature/KTN-1");
        Assert.Equal(400, Describe(await _auth.AuthorizeAsync(Request("t", "key-test"), ModuleKind.Client, true, default)).Status);
    }

    [Theory]
    [InlineData("main", "key-prod")]
    [InlineData("release/1.2.0", "key-stage")]
    [InlineData("dev", "key-test")]
    public async Task Tag_named_like_a_branch_is_not_deployed(string tag, string key)
    {
        _gitlab.Job = JobJson(tag).Replace("\"ref\"", "\"tag\":true,\"ref\"");
        Assert.Contains("\"tag\":true", _gitlab.Job);
        var (status, text) = Describe(await _auth.AuthorizeAsync(Request("t", key), ModuleKind.Client, true, default));
        Assert.Equal(400, status);
        Assert.Contains("на теге", text);
    }

    [Fact]
    public async Task Checks_need_only_the_job_token()
    {
        _gitlab.Job = JobJson("feature/KTN-1");
        var result = await _auth.AuthorizeAsync(Request("t", null), ModuleKind.Client, requireDeployKey: false, default);
        Assert.True(result.Ok);
        Assert.Null(result.Env);

        // Но проект и группа проверяются так же.
        _gitlab.Job = JobJson("feature/KTN-1", "other/x");
        Assert.Equal(403, Describe(await _auth.AuthorizeAsync(Request("t", null), ModuleKind.Client, false, default)).Status);
    }

    [Fact]
    public async Task Ten_wrong_keys_block_deploys_of_that_project_only()
    {
        _gitlab.Job = JobJson("dev");
        for (var i = 0; i < 10; i++)
            Assert.Equal(401, Describe(await _auth.AuthorizeAsync(Request("t", "wrong" + i), ModuleKind.Client, true, default)).Status);

        // Даже верный ключ из этого проекта больше не принимается, с любого адреса...
        Assert.Equal(429, Describe(await _auth.AuthorizeAsync(Request("t", "key-test", "10.0.0.99"), ModuleKind.Client, true, default)).Status);
        // ...а другой проект с того же адреса (общий раннер) не затронут, как и проверки без ключа.
        Assert.True((await _auth.AuthorizeAsync(Request("t", null), ModuleKind.Client, false, default)).Ok);
        _gitlab.Job = JobJson("dev", ClientGroup + "/other");
        Assert.True((await _auth.AuthorizeAsync(Request("t", "key-test"), ModuleKind.Client, true, default)).Ok);
    }

    [Fact]
    public async Task Ten_unconfirmed_tokens_block_the_address_but_not_confirmed_jobs()
    {
        _gitlab.Status = HttpStatusCode.Unauthorized;
        for (var i = 0; i < 10; i++)
            Assert.Equal(401, Describe(await _auth.AuthorizeAsync(Request("bad" + i, "key-test"), ModuleKind.Client, true, default)).Status);
        Assert.Equal(429, Describe(await _auth.AuthorizeAsync(Request("bad", "key-test"), ModuleKind.Client, true, default)).Status);
        Assert.Equal(429, Describe(await _auth.AuthorizeAsync(Request(null, "key-test"), ModuleKind.Client, true, default)).Status);

        _gitlab.Status = HttpStatusCode.OK;
        _gitlab.Job = JobJson("dev");
        Assert.True((await _auth.AuthorizeAsync(Request("t", "key-test"), ModuleKind.Client, true, default)).Ok);
    }

    [Fact]
    public async Task Wrong_keys_do_not_block_the_address_and_blocked_address_still_counts_wrong_keys()
    {
        _gitlab.Job = JobJson("dev");
        for (var i = 0; i < 10; i++)
            Assert.Equal(401, Describe(await _auth.AuthorizeAsync(Request("t", "wrong" + i), ModuleKind.Client, true, default)).Status);
        // Адрес не заблокирован: запрос без токена получает обычный отказ, а не 429.
        Assert.Equal(401, Describe(await _auth.AuthorizeAsync(Request(null, "key-test"), ModuleKind.Client, true, default)).Status);

        _gitlab.Status = HttpStatusCode.Unauthorized;
        for (var i = 0; i < 10; i++)
            await _auth.AuthorizeAsync(Request("bad" + i, null, "10.0.0.7"), ModuleKind.Client, true, default);
        _gitlab.Status = HttpStatusCode.OK;
        _gitlab.Job = JobJson("dev", ClientGroup + "/other");
        // С заблокированного адреса подтверждённое задание проходит проверку, но неверный ключ остаётся отказом.
        Assert.Equal(401, Describe(await _auth.AuthorizeAsync(Request("t", "wrong", "10.0.0.7"), ModuleKind.Client, true, default)).Status);
    }

    [Fact]
    public async Task Blocked_address_gets_a_limited_number_of_gitlab_verifications()
    {
        _gitlab.Status = HttpStatusCode.Unauthorized;
        for (var i = 0; i < 10; i++)
            await _auth.AuthorizeAsync(Request("bad" + i, null), ModuleKind.Client, false, default);
        _gitlab.Status = HttpStatusCode.OK;
        _gitlab.Job = JobJson("dev");
        for (var i = 0; i < 59; i++)
            Assert.True((await _auth.AuthorizeAsync(Request("t", null), ModuleKind.Client, false, default)).Ok);

        _gitlab.Last = null;
        Assert.Equal(429, Describe(await _auth.AuthorizeAsync(Request("t", null), ModuleKind.Client, false, default)).Status);
        Assert.Equal(429, Describe(await _auth.AuthorizeAsync(Request("t", null), ModuleKind.Client, false, default)).Status);
        Assert.Null(_gitlab.Last);
    }

    [Fact]
    public async Task Confirmed_job_of_a_foreign_project_is_not_counted_as_a_failed_attempt()
    {
        _gitlab.Job = JobJson("dev", "other-group/mod1");
        for (var i = 0; i < 12; i++)
            Assert.Equal(403, Describe(await _auth.AuthorizeAsync(Request("t", "key-test"), ModuleKind.Client, true, default)).Status);

        _gitlab.Status = HttpStatusCode.Unauthorized;
        Assert.Equal(401, Describe(await _auth.AuthorizeAsync(Request("bad", "key-test"), ModuleKind.Client, true, default)).Status);
    }

    [Fact]
    public void Form_fields_that_differ_from_verified_values_are_rejected()
    {
        var job = CiJobVerifier.Parse(JobJson("dev"), "http://gitlab.test")!;

        Assert.Null(CiAuth.CheckForm(Form(), job)); // поля необязательны
        Assert.Null(CiAuth.CheckForm(Form(("ref", "dev"), ("sha", "abc123"), ("project", "client-modules/mod1"), ("user", "dev1")), job));
        Assert.Null(CiAuth.CheckForm(Form(("pipeline_url", "http://gitlab.test/client-modules/mod1/-/pipelines/7/")), job));

        foreach (var (name, value) in new[] { ("ref", "main"), ("sha", "deadbeef"), ("project", "client-modules/other"), ("user", "admin"), ("pipeline_url", "http://evil/") })
        {
            var rejection = Assert.IsType<ContentHttpResult>(CiAuth.CheckForm(Form((name, value)), job));
            Assert.Equal(400, rejection.StatusCode);
            Assert.Contains(name, rejection.ResponseContent);
            Assert.Contains("не совпадает", rejection.ResponseContent);
        }

        // Проверки библиотек идут и в pipeline merge request, где ref называется иначе: ref и sha там не сверяются.
        Assert.Null(CiAuth.CheckForm(Form(("ref", "feature/KTN-1")), job, checkRefAndSha: false));
        Assert.NotNull(CiAuth.CheckForm(Form(("project", "client-modules/other")), job, checkRefAndSha: false));
    }

    [Fact]
    public void Job_response_is_parsed_with_project_path_from_the_job_url()
    {
        var job = CiJobVerifier.Parse(JobJson("dev", "client-modules/team/mod1"), "http://gitlab.test")!;
        Assert.Equal("client-modules/team/mod1", job.ProjectPath);
        Assert.Equal(3, job.ProjectId);

        // GitLab под относительным корнем: префикс входит в базу, но не в путь проекта.
        var rooted = CiJobVerifier.Parse(JobJson("dev", "gitlab/client-modules/mod1", host: "http://host/"), "http://host/gitlab")!;
        Assert.Equal("client-modules/mod1", rooted.ProjectPath);

        Assert.Null(CiJobVerifier.Parse(JobJson("dev").Replace("/-/jobs/12", "/x/12"), "http://gitlab.test"));
        Assert.Null(CiJobVerifier.Parse("[]", "http://gitlab.test"));
    }

    [Fact]
    public void Legacy_key_is_only_a_test_fallback()
    {
        var ci = new CiDeployOptions { ApiKey = "legacy" };
        Assert.Equal("legacy", ci.KeyFor("test"));
        Assert.Null(ci.KeyFor("stage"));
        Assert.Null(ci.KeyFor("prod"));

        ci.Keys["test"] = "own";
        ci.Keys["prod"] = "p";
        Assert.Equal("own", ci.KeyFor("test"));
        Assert.Equal("p", ci.KeyFor("PROD"));
        Assert.Null(ci.KeyFor("stage"));
    }

    // ---------------------------------------------------------------- вспомогательное

    private static string JobJson(string reference, string project = ClientGroup + "/mod1", string host = "http://gitlab.test/") =>
        JsonSerializer.Serialize(new
        {
            id = 12,
            status = "running",
            @ref = reference,
            web_url = $"{host}{project}/-/jobs/12",
            commit = new { id = "abc123" },
            pipeline = new { id = 7, project_id = 3, web_url = $"{host}{project}/-/pipelines/7" },
            user = new { username = "dev1" },
        });

    private static HttpRequest Request(string? token, string? key, string address = "10.0.0.5")
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse(address);
        if (token is not null)
            context.Request.Headers[CiAuth.JobTokenHeader] = token;
        if (key is not null)
            context.Request.Headers[CiAuth.DeployKeyHeader] = key;
        return context.Request;
    }

    private static FormCollection Form(params (string Name, string Value)[] fields) =>
        new(fields.ToDictionary(f => f.Name, f => new Microsoft.Extensions.Primitives.StringValues(f.Value)));

    private static (int Status, string Text) Describe(CiAuthResult result)
    {
        Assert.False(result.Ok);
        var content = Assert.IsType<ContentHttpResult>(result.Rejection);
        return (content.StatusCode ?? 200, content.ResponseContent ?? "");
    }

    private sealed class SingleClient(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class FakeGitLab : HttpMessageHandler
    {
        public string Job { get; set; } = "{}";
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public Exception? Throw { get; set; }
        public HttpRequestMessage? Last { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Last = request;
            if (Throw is not null)
                throw Throw;
            return Task.FromResult(new HttpResponseMessage(Status) { Content = new StringContent(Job, Encoding.UTF8, "application/json") });
        }
    }
}
