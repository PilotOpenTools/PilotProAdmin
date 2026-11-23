using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using ProAdmin.GitLab;
using ProAdmin.Pilot;
using ProAdmin.Sessions;
using ProAdmin.Settings;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ProAdmin.Tests;

/// <summary>
/// Рендеринг страниц без живых Pilot/GitLab: вход подменён тестовой схемой,
/// серверы указывают на закрытый порт - страницы должны показать ошибки, а не упасть.
/// </summary>
public sealed partial class PagesTests : IClassFixture<PagesTests.Factory>
{
    private const string Unreachable = "http://127.0.0.1:9";
    private readonly Factory _factory;

    public PagesTests(Factory factory) => _factory = factory;

    [Fact]
    public async Task LoginPageIsAnonymous()
    {
        var response = await _factory.CreateClient().GetAsync("/Login");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("PilotProAdmin", html);
        Assert.DoesNotContain("id=\"main-navigation\"", html);
    }

    [Fact]
    public async Task TransferDownloadContainsTheWindowsScriptsAndInstructions()
    {
        using var client = ClientFor(_factory.NewSession(withGitLab: true));
        var page = await client.GetStringAsync("/Migration/Transfer");
        Assert.Contains("?handler=Download", page);

        using var response = await client.GetAsync("/Migration/Transfer?handler=Download");
        response.EnsureSuccessStatusCode();
        Assert.Equal("application/zip", response.Content.Headers.ContentType?.MediaType);
        using var zip = new System.IO.Compression.ZipArchive(await response.Content.ReadAsStreamAsync());
        Assert.Equal(new[] { "README.txt", "backup.ps1", "monitoring.ps1", "prepare-transfer.ps1" },
            zip.Entries.Select(entry => entry.Name).OrderBy(name => name, StringComparer.Ordinal));
        using var script = zip.GetEntry("prepare-transfer.ps1")!.Open();
        var bom = new byte[3];
        script.ReadExactly(bom);
        Assert.Equal(new byte[] { 0xef, 0xbb, 0xbf }, bom);
    }

    [Theory]
    [InlineData("/Modules", "/Modules")]
    [InlineData("/Modules?kind=server", "/Modules?kind=server")]
    [InlineData("/ServerModules", "/Modules?kind=server")]
    [InlineData("/Jobs", "/Jobs")]
    public async Task NavigationMarksCurrentSection(string url, string activeLink)
    {
        using var client = ClientFor(_factory.NewSession(withGitLab: true));
        var html = await client.GetStringAsync(url);

        Assert.Contains("aria-controls=\"workspace-menu\"", html);
        Assert.Contains("id=\"workspace-menu\"", html);
        Assert.Contains($"href=\"{activeLink}\" aria-current=\"page\"", html);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(html, "aria-current=\"page\""));
    }

    [Fact]
    public async Task AutomationCatalogKeepsActivityAndAccessDetailsCollapsed()
    {
        using var client = ClientFor(_factory.NewSession(withGitLab: true));
        var html = await client.GetStringAsync("/Automations");

        Assert.Contains("<details class=\"aw\" data-area=\"automations\">", html);
        Assert.Contains("<details class=\"access-notice\">", html);
        Assert.Contains("name=\"period\"", html);
        Assert.Contains("id=\"filter\"", html);
    }

    [Fact]
    public async Task WithoutGitLabAutomationsRedirectToGitLab()
    {
        var client = ClientFor(_factory.NewSession(withGitLab: false));
        var response = await client.GetAsync("/Automations/test");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("/Login", response.Headers.Location!.OriginalString);

        var gitlab = await client.GetStringAsync("/GitLab");
        Assert.Contains("Personal Access Token", gitlab);
    }

    [Theory]
    [InlineData("/Settings", "Серверы Pilot")]
    [InlineData("/Settings", "Библиотеки модулей")]
    [InlineData("/Jobs", "Операции")]
    [InlineData("/Process", "Выпустить в прод")]
    [InlineData("/Compare", "Сравнение контуров")]
    [InlineData("/Compare?kind=reports", "два разных контура")]
    [InlineData("/Compare/Automation?name=Notify", "Различия")]
    [InlineData("/Compare/Report?name=Notify", "Различия")]
    [InlineData("/Types/Compare", "Сравнение типов")]
    [InlineData("/Types", "Репозиторий не настроен")]
    [InlineData("/Types/Edit?kind=type", "не указан репозиторий типов")]
    [InlineData("/Types/Edit?kind=machine&name=Task", "не указан репозиторий типов")]
    [InlineData("/Types/Promote?target=main", "не указан репозиторий типов")]
    [InlineData("/Settings", "Репозиторий с типами")]
    [InlineData("/Modules/Assemblies", "Библиотеки клиентских модулей")]
    [InlineData("/Modules/Assemblies?env=prod", "Состав модулей на сервере")]
    [InlineData("/ServerModules", "ServerHost")]
    [InlineData("/ServerModules?env=prod", "Серверные модули")]
    [InlineData("/Types/Compare?kind=machines", "Группы состояний")]
    [InlineData("/Automations/test", "недоступен")]
    [InlineData("/Automations/Usage", "Результат неполный")]
    [InlineData("/Automations/test/Edit?name=Notify", "Notify")]
    [InlineData("/Automations", "сервер не задан")]
    [InlineData("/Settings", "Группа с отчётами")]
    [InlineData("/Reports", "недоступен")]
    [InlineData("/Settings", "Группа с клиентскими модулями")]
    [InlineData("/Modules", "недоступен")]
    [InlineData("/Modules", "Библиотеки: ")]
    [InlineData("/Modules?kind=server", "Библиотеки: ")]
    [InlineData("/Process", "Выборочный релиз")]
    [InlineData("/Modules/Module?name=OpenStoradge", "GitLab")]
    [InlineData("/Servers", "Ожидает перезапуска")]
    [InlineData("/Servers", "docker restart pilot-server-test")]
    [InlineData("/Servers", "<корень данных>/prod/server-settings")]
    public async Task PagesRender(string url, string expected)
    {
        var client = ClientFor(_factory.NewSession(withGitLab: true));
        var response = await client.GetAsync(url);
        var html = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{url}: {(int)response.StatusCode}\n{html}");
        var text = WebUtility.HtmlDecode(html);
        var start = Math.Max(0, text.IndexOf("<main", StringComparison.Ordinal));
        var visible = System.Text.RegularExpressions.Regex.Replace(text[start..], "<[^>]+>|\\s+", " ");
        Assert.True(text.Contains(expected), $"{url}: «{expected}» не найдено в: {visible}");
    }

    [Fact]
    public async Task SharedModuleLibrariesAreListed()
    {
        var store = _factory.Services.GetRequiredService<ProAdmin.Modules.AssemblySpecStore>();
        var spec = store.Current;
        spec.Mode = ProAdmin.Modules.AssemblySpecMode.SharedModule;
        store.Save(spec, "test");
        var client = ClientFor(_factory.NewSession(withGitLab: true));
        var html = WebUtility.HtmlDecode(await client.GetStringAsync("/Modules/Assemblies"));
        Assert.Contains("Библиотеки общего модуля «Pilot.ClientSdk»", html);
        Assert.Contains("<PackageReference Include=\"Humanizer.Core\" Version=\"2.14.1\" ExcludeAssets=\"runtime\" />", html);
    }

    [Fact]
    public async Task CiChecksLibrariesByMode()
    {
        var store = _factory.Services.GetRequiredService<ProAdmin.Modules.AssemblySpecStore>();
        var client = _factory.CreateClient();

        // GitLab «подтверждает» job-токен модулей из группы настроек: проверка библиотек идёт только по нему, ключ выкладки не нужен.
        _factory.GitLabJob = _ => GitLabAnswer(JobJson("feature/KTN-1", "modules/test"));

        async Task<(System.Net.HttpStatusCode Code, string Text)> Post(string? token)
        {
            using var zip = new MemoryStream();
            using (var archive = new System.IO.Compression.ZipArchive(zip, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (var (name, path) in new[] { ("114Test.ext2.dll", typeof(PagesTests).Assembly.Location), ("xunit.assert.dll", typeof(Assert).Assembly.Location) })
                {
                    using var s = archive.CreateEntry(name).Open();
                    s.Write(File.ReadAllBytes(path));
                }
            }
            using var form = new MultipartFormDataContent
            {
                { new ByteArrayContent(zip.ToArray()), "file", "114Test.ext2.zip" },
                { new StringContent("modules/test"), "project" },
            };
            using var request = new HttpRequestMessage(HttpMethod.Post, ProAdmin.Modules.ModuleCiLibrariesEndpoint.Route) { Content = form };
            if (token is not null)
                request.Headers.Add(ProAdmin.Modules.CiAuth.JobTokenHeader, token);
            var response = await client.SendAsync(request);
            return (response.StatusCode, await response.Content.ReadAsStringAsync());
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await Post(null)).Code);
        _factory.GitLabJob = _ => new HttpResponseMessage(HttpStatusCode.Unauthorized);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Post("wrong")).Code);
        _factory.GitLabJob = _ => GitLabAnswer(JobJson("feature/KTN-1", "modules/test"));

        var spec = store.Current;
        spec.Mode = ProAdmin.Modules.AssemblySpecMode.WarnList;
        store.Save(spec, "test");
        var warn = await Post("job-token");
        Assert.Equal(HttpStatusCode.OK, warn.Code);
        Assert.Contains("xunit.assert", warn.Text);
        Assert.EndsWith("RESULT: warn\n", warn.Text);

        spec.Mode = ProAdmin.Modules.AssemblySpecMode.AllowList;
        store.Save(spec, "test");
        Assert.EndsWith("RESULT: error\n", (await Post("job-token")).Text);
    }

    [Fact]
    public async Task UnknownJobIs404()
    {
        var client = ClientFor(_factory.NewSession(withGitLab: true));
        var response = await client.GetAsync($"/Jobs/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private HttpClient ClientFor(string sid)
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(TestAuthHandler.Header, sid);
        return client;
    }

    public sealed class Factory : WebApplicationFactory<Program>
    {
        public const string CiKey = "test-ci-key";
        private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "pwa-tests-" + Guid.NewGuid().ToString("N"));

        /// <summary>Проверка прав администратора GitLab без сервера GitLab: доверяет сессии, кроме отозванных.</summary>
        public FakeAdminCheck AdminCheck { get; } = new();

        public Factory()
        {
            Environment.SetEnvironmentVariable("App__DataDirectory", _dataDir);
            Environment.SetEnvironmentVariable("App__CiDeploy__ApiKey", CiKey);
            // «Серверы»: подключён только каталог теста, значение ещё не применено сервером.
            var servers = Path.Combine(_dataDir, "servers");
            Directory.CreateDirectory(Path.Combine(servers, "test"));
            File.WriteAllText(Path.Combine(servers, "test", "server-settings.conf"), "IsAutomationLoggingEnabled=true\n");
            File.WriteAllText(Path.Combine(servers, "test", "applied.conf"), "AppliedAt=2026-10-02T12:00:00+00:00\nIsAutomationLoggingEnabled=false\n");
            // Стейдж - кластер: узел отметил это в applied.conf (Mode=cluster).
            Directory.CreateDirectory(Path.Combine(servers, "stage"));
            File.WriteAllText(Path.Combine(servers, "stage", "server-settings.conf"), "CheckChangeCreator=true\n");
            File.WriteAllText(Path.Combine(servers, "stage", "applied.conf"), "AppliedAt=2026-10-02T12:00:00+00:00\nMode=cluster\nCheckChangeCreator=false\n");
            Environment.SetEnvironmentVariable("App__ServerSettings__Root", servers);
        }

        /// <summary>Ответ «GitLab» на проверку job-токена (GET /api/v4/job); null - GitLab не отвечает.</summary>
        public Func<HttpRequestMessage, HttpResponseMessage>? GitLabJob { get; set; }

        public string NewSession(bool withGitLab, bool prodAdmin = false, IServiceProvider? services = null)
        {
            services ??= Services;
            var store = services.GetRequiredService<SettingsStore>();
            var settings = store.Current;
            settings.GitLabGroupUrl = Unreachable + "/automation";
            settings.ReportsGitLabGroupUrl = Unreachable + "/reports";
            settings.ModulesGitLabGroupUrl = Unreachable + "/modules";
            settings.StagePilotUrl = Unreachable + "/pilot-stage";
            settings.Find(AdminSettings.Test)!.PilotUrl = Unreachable + "/pilot-ice_ru";
            store.Save(settings);

            var vault = services.GetRequiredService<SessionVault>();
            var session = vault.Create(Unreachable, new PilotLogin("admin", "secret"));
            if (prodAdmin)
                session.LoginAccess = new[] { new EnvironmentAccess(AdminSettings.Prod, "Прод", Unreachable + "/pilot-ice_ru", null) };
            if (withGitLab)
            {
                session.GitLabToken = new GitLabToken { AccessToken = "fake", BaseUrl = Unreachable };
                session.GitLabUser = new GitLabUser { Username = "tester", Name = "Tester" };
            }
            return session.Id;
        }

        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureTestServices(services =>
            {
                // Ограничение перебора в общих тестах не мешает (адрес у тестового сервера один); сам предел проверяют отдельные тесты.
                services.AddSingleton(new ProAdmin.Infrastructure.AuthFailureLimiter(limit: 100_000));
                services.AddSingleton<GitLabAdminCheck>(AdminCheck);
                services.AddHttpClient(ProAdmin.Modules.CiJobVerifier.ClientName)
                    .ConfigurePrimaryHttpMessageHandler(() => new GitLabJobHandler(this));
                services.AddAuthentication(TestAuthHandler.Scheme)
                    .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.Scheme, _ => { });
                services.PostConfigure<AuthenticationOptions>(o =>
                {
                    o.DefaultScheme = TestAuthHandler.Scheme;
                    o.DefaultAuthenticateScheme = TestAuthHandler.Scheme;
                    o.DefaultChallengeScheme = TestAuthHandler.Scheme;
                });
            });
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            try { Directory.Delete(_dataDir, true); } catch { /* временная папка */ }
        }
    }

    private sealed class GitLabJobHandler(Factory factory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(factory.GitLabJob?.Invoke(request) ?? throw new HttpRequestException("GitLab недоступен"));
    }

    private sealed class TestAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public new const string Scheme = "Test";
        public const string Header = "X-Test-Sid";

        public TestAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
            : base(options, logger, encoder) { }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue(Header, out var sid))
                return Task.FromResult(AuthenticateResult.NoResult());

            var identity = new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.Name, "admin"),
                new Claim(SessionVault.SidClaim, sid.ToString()),
            }, Scheme);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme)));
        }
    }

    /// <summary>Как <see cref="GitLabAdminCheck"/>, но вместо запроса в GitLab - список сессий, у которых роль отозвана.</summary>
    public sealed class FakeAdminCheck() : GitLabAdminCheck(null!, Microsoft.Extensions.Logging.Abstractions.NullLogger<GitLabAdminCheck>.Instance)
    {
        public HashSet<string> RevokedSessions { get; } = new();

        public override Task<bool> ConfirmAsync(UserSession session, AdminSettings settings, CancellationToken ct)
        {
            if (RevokedSessions.Contains(session.Id) && session.GitLabUser is { } user)
                user.IsAdmin = false;
            return Task.FromResult(session.IsAdmin(settings));
        }
    }
}
