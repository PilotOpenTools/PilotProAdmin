using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ProAdmin.Infrastructure;
using ProAdmin.Settings;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace ProAdmin.Tests;

/// <summary>Права, заголовки, выкладка из CI и вход на живом приложении (WebApplicationFactory).</summary>
public sealed partial class PagesTests
{
    internal static string JobJson(string reference, string project = "modules/mod1") =>
        JsonSerializer.Serialize(new
        {
            id = 12,
            status = "running",
            @ref = reference,
            web_url = $"{Unreachable}/{project}/-/jobs/12",
            commit = new { id = "abc123" },
            pipeline = new { id = 7, project_id = 3, web_url = $"{Unreachable}/{project}/-/pipelines/7" },
            user = new { username = "dev1" },
        });

    internal static HttpResponseMessage GitLabAnswer(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    // ---------------------------------------------------------------- выкладка из CI

    private static MultipartFormDataContent DeployForm(params (string Name, string Value)[] fields)
    {
        using var zip = new MemoryStream();
        using (var archive = new System.IO.Compression.ZipArchive(zip, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            using var s = archive.CreateEntry("114Test.ext2.dll").Open();
            s.Write(File.ReadAllBytes(typeof(PagesTests).Assembly.Location));
        }
        var form = new MultipartFormDataContent { { new ByteArrayContent(zip.ToArray()), "file", "114Test.ext2.zip" } };
        foreach (var (name, value) in fields)
            form.Add(new StringContent(value), name);
        return form;
    }

    private static async Task<(HttpStatusCode Code, string Text)> PostDeploy(HttpClient client, string? token, string? key, params (string, string)[] fields)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, ProAdmin.Modules.ModuleCiDeployEndpoint.Route) { Content = DeployForm(fields) };
        if (token is not null)
            request.Headers.Add(ProAdmin.Modules.CiAuth.JobTokenHeader, token);
        if (key is not null)
            request.Headers.Add(ProAdmin.Modules.ModuleCiDeployEndpoint.KeyHeader, key);
        var response = await client.SendAsync(request);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task CiDeployNeedsJobTokenAndTrustsOnlyGitLab()
    {
        _factory.NewSession(withGitLab: true); // настройки: группа модулей и сервер теста
        var client = _factory.CreateClient();
        var gitlabCalls = 0;
        _factory.GitLabJob = _ => { gitlabCalls++; return GitLabAnswer(JobJson("dev")); };

        // Один ключ без job-токена не принимается, GitLab при этом не спрашивается.
        var noToken = await PostDeploy(client, null, Factory.CiKey);
        Assert.Equal(HttpStatusCode.Unauthorized, noToken.Code);
        Assert.Contains("X-Job-Token", noToken.Text);
        Assert.Equal(0, gitlabCalls);

        // Подделанные поля формы (ref=main при подтверждённой ветке dev) - 400, выкладки нет.
        var forged = await PostDeploy(client, "tok", Factory.CiKey, ("ref", "main"), ("project", "modules/mod1"));
        Assert.Equal(HttpStatusCode.BadRequest, forged.Code);
        Assert.Contains("не совпадает", forged.Text);

        // Токен и ключ верны, поля совпадают: авторизация пройдена и дело доходит до служебной учётки Pilot (её в тесте нет).
        var ok = await PostDeploy(client, "tok", Factory.CiKey, ("ref", "dev"), ("sha", "abc123"), ("project", "modules/mod1"));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, ok.Code);
        Assert.Contains("служебной учётки Pilot", ok.Text);
        Assert.True(gitlabCalls >= 2);
    }

    [Fact]
    public async Task CiDeployProdNeedsItsOwnKey()
    {
        _factory.NewSession(withGitLab: true);
        _factory.GitLabJob = _ => GitLabAnswer(JobJson("main"));

        // Прежний общий ключ (он же ключ теста) прод не открывает никогда.
        var result = await PostDeploy(_factory.CreateClient(), "tok", Factory.CiKey);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, result.Code);
        Assert.Contains("MODULES_DEPLOY_KEY_PROD", result.Text);
    }

    [Fact]
    public async Task CiDeployRejectsProjectOutsideTheGroup()
    {
        _factory.NewSession(withGitLab: true);
        _factory.GitLabJob = _ => GitLabAnswer(JobJson("dev", "someone-else/mod1"));
        var result = await PostDeploy(_factory.CreateClient(), "tok", Factory.CiKey);
        Assert.Equal(HttpStatusCode.Forbidden, result.Code);
    }

    [Fact]
    public async Task CiEndpointsAreRateLimitedByAddress()
    {
        _factory.NewSession(withGitLab: true);
        _factory.GitLabJob = _ => GitLabAnswer(JobJson("dev"));
        using var limited = _factory.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddSingleton(new AuthFailureLimiter(limit: 3))));
        var client = limited.CreateClient();

        for (var i = 0; i < 3; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await PostDeploy(client, "tok", "wrong-" + i)).Code);
        var blocked = await PostDeploy(client, "tok", Factory.CiKey);
        Assert.Equal((HttpStatusCode)429, blocked.Code);
    }

    // ---------------------------------------------------------------- заголовки и страницы

    [Fact]
    public async Task ResponsesCarrySecurityHeadersAndScriptsCarryTheNonce()
    {
        var response = await _factory.CreateClient().GetAsync("/Login");
        var csp = string.Join(";", response.Headers.GetValues("Content-Security-Policy"));
        var nonce = Regex.Match(csp, "'nonce-([^']+)'").Groups[1].Value;

        Assert.NotEmpty(nonce);
        Assert.DoesNotContain("unsafe-inline'", csp.Split(';').Single(p => p.TrimStart().StartsWith("script-src")));
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("same-origin", response.Headers.GetValues("Referrer-Policy").Single());
        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());

        // Razor кодирует «+» в значении атрибута, браузер декодирует его обратно.
        var html = System.Net.WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
        Assert.Contains($"nonce=\"{nonce}\"", html);
        Assert.Empty(Regex.Matches(html, "<script(?![^>]*nonce=)[^>]*>"));
    }

    [Fact]
    public async Task NonceChangesWithEveryRequest()
    {
        var client = _factory.CreateClient();
        string Nonce(HttpResponseMessage r) => Regex.Match(string.Join(";", r.Headers.GetValues("Content-Security-Policy")), "'nonce-([^']+)'").Groups[1].Value;
        Assert.NotEqual(Nonce(await client.GetAsync("/Login")), Nonce(await client.GetAsync("/Login")));
    }

    [Fact]
    public async Task UnknownPageShowsAReadableNotFoundPage()
    {
        var response = await ClientFor(_factory.NewSession(withGitLab: true)).GetAsync("/NoSuchPage");
        var html = await Text(response);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("Страница не найдена", html);
        Assert.Contains("На главную", html);
    }

    [Fact]
    public async Task DownloadsNeedLogin()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateClient().GetAsync("/downloads/debug.zip")).StatusCode);
        // После входа дело доходит до статических файлов (в тесте файла нет), а не до отказа.
        Assert.Equal(HttpStatusCode.NotFound, (await ClientFor(_factory.NewSession(withGitLab: true)).GetAsync("/downloads/debug.zip")).StatusCode);
        // Стили нужны странице входа и остаются открытыми.
        Assert.Equal(HttpStatusCode.OK, (await _factory.CreateClient().GetAsync("/css/site.css")).StatusCode);
    }

    [Fact]
    public void PagesHaveNoInlineEventHandlersOrRawUserHtml()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "ProAdmin", "Pages")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        var pages = Path.Combine(dir!.FullName, "src", "ProAdmin", "Pages");

        var handler = new Regex(@"\s(on(?:click|submit|change|input|load|error|keydown|keyup|focus|blur|mouse\w+))\s*=", RegexOptions.IgnoreCase);
        var problems = new List<string>();
        foreach (var file in Directory.EnumerateFiles(pages, "*.cshtml", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);
            foreach (Match m in handler.Matches(text))
                problems.Add($"{Path.GetRelativePath(pages, file)}: inline-обработчик {m.Groups[1].Value}");
            if (text.Contains("javascript:", StringComparison.OrdinalIgnoreCase))
                problems.Add($"{Path.GetRelativePath(pages, file)}: javascript:");
        }
        Assert.True(problems.Count == 0, string.Join("\n", problems));

        // Html.Raw допустим только для данных, собранных кодом (JSON в <script type="application/json">, статический текст), но не для значений из GitLab.
        var raw = Directory.EnumerateFiles(pages, "*.cshtml", SearchOption.AllDirectories)
            .SelectMany(f => File.ReadAllLines(f).Select((l, i) => (File: Path.GetRelativePath(pages, f), Line: l, No: i + 1)))
            .Where(x => x.Line.Contains("Html.Raw(") && !x.Line.Contains("application/json") && !x.File.StartsWith("Process.cshtml"))
            .ToList();
        Assert.True(raw.Count == 0, string.Join("\n", raw.Select(x => $"{x.File}:{x.No}: {x.Line.Trim()}")));
    }

    // ---------------------------------------------------------------- права

    [Theory]
    [InlineData("/Automations/Create", "/Automations")]
    [InlineData("/Automations/Create?copyFrom=Notify", "/Automations")]
    [InlineData("/Automations/Rename?name=Notify", "/Automations")]
    [InlineData("/Automations/Delete?name=Notify", "/Automations")]
    [InlineData("/Automations/Promote?name=Notify", "/Automations")]
    [InlineData("/Automations/Rollback?name=Notify", "/Automations")]
    [InlineData("/Reports/Create?name=Notify", "/Reports")]
    [InlineData("/Reports/Promote?name=Notify", "/Reports")]
    [InlineData("/Reports/Rollback?name=Notify", "/Reports")]
    public async Task ChangeFormsAreNotShownWithoutProvenRights(string url, string listPage)
    {
        // GitLab недоступен, роль определить нельзя: формы изменения не открываются, пользователя возвращают к списку.
        var client = ClientFor(_factory.NewSession(withGitLab: true));
        var response = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith(listPage, response.Headers.Location!.OriginalString);
    }

    /// <summary>Razor кодирует кириллицу числовыми сущностями: тексты проверяются после декодирования.</summary>
    private static async Task<string> Text(HttpResponseMessage response) =>
        WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());

    private static async Task<string> AntiforgeryToken(HttpClient client, string page) =>
        Regex.Match(await client.GetStringAsync(page), "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;

    private static async Task<HttpResponseMessage> PostForm(HttpClient client, string tokenPage, string url, params (string Name, string Value)[] fields)
    {
        var token = await AntiforgeryToken(client, tokenPage);
        return await client.PostAsync(url, new FormUrlEncodedContent(
            fields.Append((Name: "__RequestVerificationToken", Value: token)).Select(f => KeyValuePair.Create(f.Name, f.Value))));
    }

    [Theory]
    [InlineData("/Settings")]
    [InlineData("/Settings?handler=Check")]
    [InlineData("/Migration?handler=Start")]
    [InlineData("/Migration?handler=Plan")]
    [InlineData("/Migration?handler=DeleteSchedule&id=00000000-0000-0000-0000-000000000001")]
    [InlineData("/Migration/Connections")]
    [InlineData("/Migration/Connections?handler=Check")]
    [InlineData("/Migration/Schedule")]
    [InlineData("/Migration/Run/00000000-0000-0000-0000-000000000001?handler=Cancel")]
    [InlineData("/Servers")]
    public async Task WritingSharedSettingsMigrationAndServersNeedsProdAdmin(string url)
    {
        var client = ClientFor(_factory.NewSession(withGitLab: true));
        var response = await PostForm(client, "/Jobs", url, ("env", "test"));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("Доступ запрещён", await Text(response));
    }

    [Fact]
    public async Task ProdAdminSessionPassesTheGate()
    {
        var client = ClientFor(_factory.NewSession(withGitLab: true, prodAdmin: true));
        foreach (var url in new[] { "/Settings", "/Migration?handler=Plan", "/Servers" })
        {
            // Неверный адрес группы: настройки не сохраняются, страница показывает ошибку.
            var response = await PostForm(client, "/Jobs", url, ("env", "test"), ("Form.GitLabGroupUrl", "не адрес"));
            Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
        }
    }

    [Fact]
    public async Task SettingsLibrariesStayAvailableWithoutProdAdmin()
    {
        var client = ClientFor(_factory.NewSession(withGitLab: true));
        var libraries = await PostForm(client, "/Jobs", "/Settings?handler=Libraries");
        Assert.NotEqual(HttpStatusCode.Forbidden, libraries.StatusCode);
    }

    [Fact]
    public async Task ReadOnlySessionsSeeTheBannerOnWritePages()
    {
        var readOnly = ClientFor(_factory.NewSession(withGitLab: true));
        var admin = ClientFor(_factory.NewSession(withGitLab: true, prodAdmin: true));
        foreach (var url in new[] { "/Settings", "/Servers", "/Migration" })
        {
            Assert.Contains("Только просмотр", WebUtility.HtmlDecode(await readOnly.GetStringAsync(url)));
            Assert.DoesNotContain("Только просмотр.", WebUtility.HtmlDecode(await admin.GetStringAsync(url)));
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ServerModuleChangesAreDeniedWithoutProvenGitLabRights(bool gitLabConnected)
    {
        // GitLab подключён, но недоступен (нет роли), либо не подключён вовсе: установка кода в Pilot-Server запрещена.
        var client = ClientFor(_factory.NewSession(gitLabConnected));
        foreach (var handler in new[] { "Install", "Disable", "Enable", "Reload", "Rollback", "Remove", "Validate" })
        {
            var response = await PostForm(client, "/Jobs", $"/ServerModules?handler={handler}", ("Name", "Some.Module"), ("env", "test"));
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            var location = response.Headers.Location!.OriginalString;
            Assert.StartsWith("/Modules", location);
            Assert.DoesNotContain("ServerModules", location);
        }
    }

    // ---------------------------------------------------------------- вход

    private static HttpClient ClientFor(WebApplicationFactory<Program> factory, string sid)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(TestAuthHandler.Header, sid);
        return client;
    }

    [Fact]
    public async Task FailedContourCredentialsCountTowardsTheSameBlock()
    {
        // Ввод логина администратора для контура проверяется на сервере Pilot, то есть тоже позволяет перебор.
        _factory.NewSession(withGitLab: true); // настройки читает и производный хост
        using var limited = _factory.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddSingleton(new AuthFailureLimiter(limit: 2))));
        // У производного хоста своё хранилище сессий.
        var session = limited.Services.GetRequiredService<ProAdmin.Sessions.SessionVault>().Create(Unreachable, new ProAdmin.Pilot.PilotLogin("admin", "secret"));
        session.GitLabToken = new ProAdmin.GitLab.GitLabToken { AccessToken = "fake", BaseUrl = Unreachable };
        session.GitLabUser = new ProAdmin.GitLab.GitLabUser { Username = "tester", Name = "Tester" };
        var client = ClientFor(limited, session.Id);

        for (var i = 0; i < 2; i++)
        {
            var failed = await PostForm(client, "/Jobs", "/Automations?handler=Credentials", ("CredEnv", "test"), ("EnvLogin", "admin"), ("EnvPassword", "wrong" + i));
            Assert.Equal(HttpStatusCode.OK, failed.StatusCode);
        }
        var blocked = await PostForm(client, "/Jobs", "/Automations?handler=Credentials", ("CredEnv", "test"), ("EnvLogin", "admin"), ("EnvPassword", "right"));
        Assert.Equal((HttpStatusCode)429, blocked.StatusCode);
    }

    [Fact]
    public void FirstSetupLoginNeedsTheSetupKey()
    {
        // Пока серверы не настроены, адрес Pilot вводит посетитель: без ключа установки такой вход закрыт.
        Assert.Contains("ADMIN_SETUP_KEY", ProAdmin.Pages.LoginModel.SetupKeyError("", "anything"));
        Assert.Contains("ADMIN_SETUP_KEY", ProAdmin.Pages.LoginModel.SetupKeyError(null, null));
        Assert.Equal("Неверный ключ установки.", ProAdmin.Pages.LoginModel.SetupKeyError("setup-key", "wrong"));
        Assert.Equal("Неверный ключ установки.", ProAdmin.Pages.LoginModel.SetupKeyError("setup-key", null));
        Assert.Null(ProAdmin.Pages.LoginModel.SetupKeyError("setup-key", "setup-key"));
    }

    [Fact]
    public async Task LoginDoesNotLetTheVisitorChooseThePilotServer()
    {
        _factory.NewSession(withGitLab: true); // серверы заданы в настройках
        var html = WebUtility.HtmlDecode(await _factory.CreateClient().GetStringAsync("/Login?manual=true"));
        Assert.DoesNotContain("name=\"ServerUrl\"", html);
        Assert.DoesNotContain("Указать адрес сервера вручную", html);
        Assert.Contains("Вход будет проверен на серверах", html);
    }

    [Fact]
    public async Task FailedLoginsBlockTheAddress()
    {
        _factory.NewSession(withGitLab: true);
        // Предел 10 за 5 минут проверен в AccessTests; здесь - что вход подключён к счётчику (одна попытка идёт секунды).
        using var limited = _factory.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddSingleton(new AuthFailureLimiter(limit: 2))));
        var client = limited.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        HttpResponseMessage last = null!;
        for (var i = 0; i < 2; i++)
        {
            last = await PostForm(client, "/Login", "/Login", ("Login", "admin"), ("Password", "wrong" + i), ("AccessToken", "glpat-x"));
            Assert.Equal(HttpStatusCode.OK, last.StatusCode);
        }
        Assert.Contains("Не удалось войти ни на один сервер Pilot", await Text(last));

        var blocked = await PostForm(client, "/Login", "/Login", ("Login", "admin"), ("Password", "right"), ("AccessToken", "glpat-x"));
        Assert.Equal((HttpStatusCode)429, blocked.StatusCode);
        Assert.Contains("Слишком много попыток", await Text(blocked));
    }
}
