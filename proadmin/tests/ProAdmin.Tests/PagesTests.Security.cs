using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ProAdmin.Infrastructure;
using Microsoft.AspNetCore.DataProtection;
using ProAdmin.Sessions;
using ProAdmin.Settings;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
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

    /// <summary>Сессия только с входом через GitLab (без входа в Pilot), как в обычной работе админки.</summary>
    private (string Id, UserSession Session) GitLabOnlySession(bool admin)
    {
        var id = _factory.NewSession(withGitLab: true);
        var session = _factory.Services.GetRequiredService<SessionVault>().Get(id)!;
        session.PilotConnection = null;
        session.GitLabUser!.IsAdmin = admin;
        return (id, session);
    }

    [Fact]
    public async Task GitLabAdminAdministersWithoutPilotLogin()
    {
        var (id, session) = GitLabOnlySession(admin: true);
        var client = ClientFor(id);
        foreach (var url in new[] { "/Settings", "/Servers", "/Migration" })
            Assert.DoesNotContain("Только просмотр.", WebUtility.HtmlDecode(await client.GetStringAsync(url)));
        foreach (var url in new[] { "/Settings", "/Settings?handler=Deploy", "/Servers", "/Migration?handler=Plan" })
        {
            var response = await PostForm(client, "/Jobs", url, ("env", "test"), ("Form.GitLabGroupUrl", "не адрес"));
            Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
        }

        session.GitLabUser!.IsAdmin = false;
        Assert.Contains("администратор GitLab", WebUtility.HtmlDecode(await client.GetStringAsync("/Settings")));
        foreach (var url in new[] { "/Settings", "/Servers", "/Migration?handler=Plan" })
            Assert.Equal(HttpStatusCode.Forbidden, (await PostForm(client, "/Jobs", url, ("env", "test"))).StatusCode);
    }

    [Fact]
    public async Task RevokedGitLabAdminLosesWriteAccessInActiveSession()
    {
        var (id, session) = GitLabOnlySession(admin: true);
        var client = ClientFor(id);
        Assert.NotEqual(HttpStatusCode.Forbidden, (await PostForm(client, "/Jobs", "/Settings", ("Form.GitLabGroupUrl", "не адрес"))).StatusCode);

        // Роль отозвали в GitLab, сессия жива: запись запрещена, роль в сессии сброшена.
        _factory.AdminCheck.RevokedSessions.Add(id);
        try
        {
            foreach (var url in new[] { "/Settings", "/Settings?handler=Deploy", "/Servers", "/Migration?handler=Plan" })
                Assert.Equal(HttpStatusCode.Forbidden, (await PostForm(client, "/Jobs", url, ("env", "test"))).StatusCode);
            Assert.False(session.GitLabUser!.IsAdmin);
        }
        finally
        {
            _factory.AdminCheck.RevokedSessions.Remove(id);
        }
    }

    [Fact]
    public async Task GitLabSessionHasNoPilotLoginForms()
    {
        var (id, _) = GitLabOnlySession(admin: false);
        var client = ClientFor(id);
        var page = await client.GetAsync("/PilotConnection");
        Assert.Equal(HttpStatusCode.Redirect, page.StatusCode);
        Assert.StartsWith("/Settings", page.Headers.Location!.OriginalString);
        Assert.DoesNotContain("Подключить Pilot", WebUtility.HtmlDecode(await client.GetStringAsync("/Jobs")));
        foreach (var url in new[] { "/Automations?handler=Credentials", "/Reports?handler=Credentials", "/Modules?handler=Credentials" })
        {
            var response = await PostForm(client, "/Jobs", url, ("CredEnv", "test"), ("EnvLogin", "admin"), ("EnvPassword", "x"));
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
    }

    [Fact]
    public void GitLabSessionWorksWithPilotThroughServiceAccounts()
    {
        var (_, session) = GitLabOnlySession(admin: false);
        var vault = _factory.Services.GetRequiredService<SessionVault>();
        var store = _factory.Services.GetRequiredService<SettingsStore>();
        var protector = _factory.Services.GetRequiredService<Microsoft.AspNetCore.DataProtection.IDataProtectionProvider>()
            .CreateProtector("ProAdmin.CiDeployAccounts");
        var settings = store.Current;
        var before = settings.Clone().DeployLogins;
        try
        {
            settings.DeployLogins["test"] = new StoredDeployLogin("svc-test", protector.Protect("pw"));
            settings.DeployLogins["prod"] = new StoredDeployLogin("", ""); // отключена
            store.Save(settings);
            foreach (var env in new[] { "test", SessionVault.ReportsPrefix + "test", SessionVault.ModulesPrefix + "test" })
                Assert.Equal(new Pilot.PilotLogin("svc-test", "pw"), vault.GetPilotLogin(session, env));
            Assert.Throws<Pilot.PilotCredentialsRequiredException>(() => vault.GetPilotLogin(session, "prod"));
        }
        finally
        {
            settings = store.Current;
            settings.DeployLogins = before;
            store.Save(settings);
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
        Assert.Contains("ADMIN_SETUP_KEY", ProAdmin.Pages.PilotConnectionModel.SetupKeyError("", "anything"));
        Assert.Contains("ADMIN_SETUP_KEY", ProAdmin.Pages.PilotConnectionModel.SetupKeyError(null, null));
        Assert.Equal("Неверный ключ установки.", ProAdmin.Pages.PilotConnectionModel.SetupKeyError("setup-key", "wrong"));
        Assert.Equal("Неверный ключ установки.", ProAdmin.Pages.PilotConnectionModel.SetupKeyError("setup-key", null));
        Assert.Null(ProAdmin.Pages.PilotConnectionModel.SetupKeyError("setup-key", "setup-key"));
    }

    [Fact]
    public async Task LoginOnlyAsksForGitLab()
    {
        var html = WebUtility.HtmlDecode(await _factory.CreateClient().GetStringAsync("/Login?manual=true"));
        Assert.Contains("name=\"AccessToken\"", html);
        Assert.DoesNotContain("name=\"Login\"", html);
        Assert.DoesNotContain("name=\"Password\"", html);
        Assert.DoesNotContain("name=\"ServerUrl\"", html);
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
            last = await PostForm(client, "/Login", "/Login?handler=Token", ("AccessToken", "glpat-wrong" + i));
            Assert.Equal(HttpStatusCode.OK, last.StatusCode);
        }
        Assert.Contains("alert-error", await Text(last));

        var blocked = await PostForm(client, "/Login", "/Login?handler=Token", ("AccessToken", "glpat-x"));
        Assert.Equal((HttpStatusCode)429, blocked.StatusCode);
        Assert.Contains("Слишком много попыток", await Text(blocked));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GitLabSignsInWithoutPilot(bool oauth)
    {
        using var host = GitLabLoginHost();
        _factory.NewSession(withGitLab: false, services: host.Services);
        using var client = host.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        HttpResponseMessage login;
        if (oauth)
        {
            var request = await PostForm(client, "/Login", "/Login?handler=GitLab", ("ReturnUrl", "/Jobs"));
            Assert.Equal(HttpStatusCode.Redirect, request.StatusCode);
            var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(request.Headers.Location!.Query);
            var state = query["state"].ToString();

            using var otherBrowser = host.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            var callback = "/GitLab/Callback?code=test-code&state=" + state;
            Assert.Contains("Сеанс входа устарел", await Text(await otherBrowser.GetAsync(callback)));
            Assert.Contains("Сеанс входа устарел", await Text(await client.GetAsync(callback + "wrong")));
            login = await client.GetAsync(callback);
        }
        else
        {
            login = await PostForm(client, "/Login", "/Login?handler=Token",
                ("ReturnUrl", "/Jobs"), ("AccessToken", "glpat-test"));
        }

        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        Assert.Equal("/Jobs", login.Headers.Location?.OriginalString);
        var jobs = await client.GetAsync("/Jobs");
        Assert.Equal(HttpStatusCode.OK, jobs.StatusCode);
        Assert.DoesNotContain("Подключить Pilot", await Text(jobs));
        // Входа в Pilot нет: учётки Pilot - служебные из «Настроек».
        var connection = await client.GetAsync("/PilotConnection");
        Assert.StartsWith("/Settings", connection.Headers.Location?.OriginalString);
        var automations = await client.GetAsync("/Automations");
        Assert.Equal(HttpStatusCode.OK, automations.StatusCode);
        Assert.DoesNotContain("name=\"EnvPassword\"", await Text(automations));
        Assert.Equal(HttpStatusCode.Forbidden, (await PostForm(client, "/Jobs", "/Settings")).StatusCode);
    }

    [Fact]
    public async Task GitLabAccountWithoutGroupAccessDoesNotSignIn()
    {
        using var host = GitLabLoginHost(member: false);
        _factory.NewSession(withGitLab: false, services: host.Services);
        using var client = host.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var login = await PostForm(client, "/Login", "/Login?handler=Token", ("AccessToken", "glpat-test"));
        Assert.Contains("не подтверждён доступ", await Text(login));
        var jobs = await client.GetAsync("/Jobs");
        Assert.Equal(HttpStatusCode.Redirect, jobs.StatusCode);
        Assert.StartsWith("/Login", jobs.Headers.Location?.PathAndQuery);
    }

    [Fact]
    public async Task FirstRunRequiresSetupKeyAndSavesSettingsBeforeRegularLogin()
    {
        using var loginHost = GitLabLoginHost();
        using var host = loginHost.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.PostConfigure<AppOptions>(options =>
            {
                options.FirstRunSetup = true;
                options.SetupKey = "test-setup-key";
            })));
        _factory.NewSession(withGitLab: false, services: host.Services);
        using var client = host.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var settings = host.Services.GetRequiredService<SettingsStore>();

        Assert.Equal("/Setup", (await client.GetAsync("/Login")).Headers.Location?.OriginalString);
        var wrong = await PostForm(client, "/Setup", "/Setup?handler=Connect",
            ("SetupKey", "wrong"), ("GitLabUrl", "https://git.example"), ("Token", "glpat-test"));
        Assert.Contains("Неверный ключ установки", await Text(wrong));
        Assert.False(settings.Current.FirstRunCompleted);

        var connected = await PostForm(client, "/Setup", "/Setup?handler=Connect",
            ("SetupKey", "test-setup-key"), ("GitLabUrl", "https://git.example"), ("Token", "glpat-test"));
        Assert.Equal(HttpStatusCode.Redirect, connected.StatusCode);
        var next = await Text(await client.GetAsync("/Setup"));
        Assert.Contains("name=\"Deploy[0].Login\"", next);
        Assert.Contains("name=\"Deploy[2].Password\"", next);
        Assert.DoesNotContain("glpat-test", next);

        var saved = await PostForm(client, "/Setup", "/Setup?handler=Finish",
            ("Input.Test", "http://pilot.example:5547"), ("Input.Grafana", "https://grafana.example"),
            ("Input.Automation", "https://git.example/automation"), ("Deploy[0].Login", ""),
            ("Deploy[1].Login", ""), ("Deploy[2].Login", ""));
        Assert.Equal(HttpStatusCode.Redirect, saved.StatusCode);
        Assert.True(settings.Current.FirstRunCompleted);
        Assert.Equal("https://grafana.example", settings.Current.GrafanaUrl);
        Assert.Equal("/Login", (await client.GetAsync("/Setup")).Headers.Location?.OriginalString);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/Jobs")).StatusCode);
    }

    [Fact]
    public async Task FirstRunCanSignInThroughGitLabInTheBrowser()
    {
        using var loginHost = GitLabLoginHost();
        using var host = loginHost.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.PostConfigure<AppOptions>(options =>
            {
                options.FirstRunSetup = true;
                options.SetupKey = "test-setup-key";
            })));
        _factory.NewSession(withGitLab: false, services: host.Services);
        using var client = host.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var settings = host.Services.GetRequiredService<SettingsStore>();

        Assert.Contains("Войти через GitLab в браузере", await Text(await client.GetAsync("/Setup")));
        var wrong = await PostForm(client, "/Setup", "/Setup?handler=Browser",
            ("SetupKey", "wrong"), ("GitLabUrl", "https://git.example"));
        Assert.Contains("Неверный ключ установки", await Text(wrong));

        var start = await PostForm(client, "/Setup", "/Setup?handler=Browser",
            ("SetupKey", "test-setup-key"), ("GitLabUrl", "https://git.example"));
        Assert.Equal(HttpStatusCode.Redirect, start.StatusCode);
        Assert.StartsWith("https://git.example/oauth/authorize?", start.Headers.Location!.OriginalString);
        var state = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(start.Headers.Location.Query)["state"].ToString();

        using var otherBrowser = host.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        Assert.Contains("Сеанс входа устарел", await Text(await otherBrowser.GetAsync("/GitLab/Callback?code=c&state=" + state)));

        var back = await client.GetAsync("/GitLab/Callback?code=test-code&state=" + state);
        Assert.Equal("/Setup", back.Headers.Location?.OriginalString);
        var next = await Text(await client.GetAsync("/Setup"));
        Assert.Contains("tester", next);
        Assert.Contains("name=\"Deploy[0].Login\"", next);

        var saved = await PostForm(client, "/Setup", "/Setup?handler=Finish",
            ("Input.Test", "http://pilot.example:5547"), ("Input.Automation", "https://git.example/automation"),
            ("Deploy[0].Login", ""), ("Deploy[1].Login", ""), ("Deploy[2].Login", ""));
        Assert.Equal(HttpStatusCode.Redirect, saved.StatusCode);
        Assert.True(settings.Current.FirstRunCompleted);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/Jobs")).StatusCode);

        // После мастера вход из мастера закрыт.
        Assert.Equal("/Login", (await PostForm(client, "/Jobs", "/Setup?handler=Browser",
            ("SetupKey", "test-setup-key"), ("GitLabUrl", "https://git.example"))).Headers.Location?.OriginalString);
    }

    private WebApplicationFactory<Program> GitLabLoginHost(bool member = true) =>
        _factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.PostConfigure<AuthenticationOptions>(options =>
            {
                options.DefaultAuthenticateScheme = CookieAuthenticationDefaults.AuthenticationScheme;
                options.DefaultChallengeScheme = CookieAuthenticationDefaults.AuthenticationScheme;
            });
            services.PostConfigure<AppOptions>(options =>
            {
                options.GitLab.ClientId = "test-client";
                options.GitLab.ClientSecret = "test-secret";
            });
            services.AddHttpClient("gitlab").ConfigurePrimaryHttpMessageHandler(() => new LoginGitLabHandler(member));
            services.AddHttpClient("gitlab-probe").ConfigurePrimaryHttpMessageHandler(() => new LoginGitLabHandler(member));
        }));

    private sealed class LoginGitLabHandler(bool member) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(request.RequestUri?.AbsolutePath switch
            {
                "/oauth/token" => GitLabAnswer("""{"access_token":"glpat-test","token_type":"Bearer"}"""),
                "/api/v4/user" => GitLabAnswer("""{"id":42,"username":"tester","name":"Tester"}"""),
                "/api/v4/groups/automation" => GitLabAnswer("""{"id":1,"full_path":"automation"}"""),
                "/api/v4/groups/automation/members/all/42" when member => GitLabAnswer("""{"access_level":30}"""),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            });
    }
}
