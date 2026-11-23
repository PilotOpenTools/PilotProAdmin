using ProAdmin.Automations;
using ProAdmin.GitLab;
using ProAdmin.Infrastructure;
using ProAdmin.Jobs;
using ProAdmin.Pilot;
using ProAdmin.Reports;
using ProAdmin.Sessions;
using ProAdmin.Settings;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;

var builder = WebApplication.CreateBuilder(args);

var appSection = builder.Configuration.GetSection("App");
builder.Services.Configure<AppOptions>(appSection);
var appOptions = appSection.Get<AppOptions>() ?? new AppOptions();
var dataDirectory = Path.GetFullPath(appOptions.DataDirectory);
Directory.CreateDirectory(dataDirectory);

// Secure: когда запрос пришёл по HTTPS; если публичный адрес админки https - всегда.
var secureCookies = appOptions.PublicUrl?.StartsWith("https://", StringComparison.OrdinalIgnoreCase) == true
    ? CookieSecurePolicy.Always
    : CookieSecurePolicy.SameAsRequest;
builder.Services.AddAntiforgery(options => options.Cookie.SecurePolicy = secureCookies);

builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataDirectory, "keys")))
    .SetApplicationName("ProAdmin");

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/Login";
        options.LogoutPath = "/Logout";
        options.AccessDeniedPath = "/Login";
        options.Cookie.Name = "pwa.auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = secureCookies;
        options.ExpireTimeSpan = TimeSpan.FromHours(10);
        options.SlidingExpiration = true;
        options.Events.OnValidatePrincipal = async context =>
        {
            // Сессии GitLab переживают перезапуск (sessions.dat); после выхода или простоя cookie недействительна.
            var vault = context.HttpContext.RequestServices.GetRequiredService<SessionVault>();
            var sid = context.Principal?.FindFirst(SessionVault.SidClaim)?.Value;
            if (sid is null || !vault.Touch(sid))
            {
                context.RejectPrincipal();
                await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            }
        };
    });

builder.Services.AddAuthorization(options =>
    options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());

builder.Services.AddRazorPages(options =>
{
    options.Conventions.AllowAnonymousToPage("/Login");
    options.Conventions.AllowAnonymousToPage("/Setup");
    options.Conventions.AllowAnonymousToPage("/GitLab/Callback");
    if (appOptions.GitHubTypes.IsConfigured)
        options.Conventions.AllowAnonymousToPage("/PilotConnection");
    options.Conventions.ConfigureFilter(new ProAdmin.Pages.PilotCredentialsFilter());
    options.Conventions.AllowAnonymousToPage("/Error");
    options.Conventions.AllowAnonymousToPage("/Process");
    // Запись в общих настройках, миграции и настройках серверов - только администратору админки.
    options.Conventions.AddFolderApplicationModelConvention("/Migration", model => model.Filters.Add(new ProAdmin.Pages.ProdAdminOnlyFilter()));
    options.Conventions.AddPageApplicationModelConvention("/Servers/Index", model => model.Filters.Add(new ProAdmin.Pages.ProdAdminOnlyFilter()));
    options.Conventions.AddPageApplicationModelConvention("/Settings", model => model.Filters.Add(
        new ProAdmin.Pages.ProdAdminOnlyFilter("Libraries")));
});

builder.Services.AddHttpClient("gitlab", client => client.Timeout = TimeSpan.FromSeconds(60))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
// Проверка job-токена выкладки: тайм-аут 15 с и без редиректов (токен не должен уйти на другой хост).
builder.Services.AddHttpClient(ProAdmin.Modules.CiJobVerifier.ClientName, client => client.Timeout = TimeSpan.FromSeconds(15))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
// Наличие Pilot.ClientSdk в nuget.org при создании модуля: в закрытом контуре без интернета - быстрый отказ.
builder.Services.AddHttpClient(ProAdmin.Modules.ModuleCreation.NuGetProbeClient, client => client.Timeout = TimeSpan.FromSeconds(8));
builder.Services.AddHttpClient("gitlab-probe", client => client.Timeout = TimeSpan.FromSeconds(10))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddHttpClient<ProAdmin.Types.GitHubTypesClient>(client =>
    {
        client.Timeout = TimeSpan.FromSeconds(30);
        client.MaxResponseContentBufferSize = ProAdmin.Types.GitHubTypesClient.MaxArchiveBytes;
    })
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });

// Релизы github.com/PilotOpenTools без токена; файлы релиза - только с хранилища GitHub по https.
builder.Services.AddHttpClient(ProAdmin.Modules.OpenToolsReleases.ClientName, client =>
    {
        // Три попытки по 20 с (GitHubRedirectHandler) на запрос и на каждый редирект файла релиза.
        client.Timeout = TimeSpan.FromSeconds(150);
        client.MaxResponseContentBufferSize = ProAdmin.Modules.OpenToolsReleases.MaxAssetBytes;
        client.DefaultRequestHeaders.UserAgent.ParseAdd("PilotProAdmin");
    })
    // Прокси - из HTTPS_PROXY / NO_PROXY окружения (по умолчанию у SocketsHttpHandler).
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(10) })
    .AddHttpMessageHandler(() => new ProAdmin.Modules.GitHubRedirectHandler());

builder.Services.AddHttpClient("migration", client => client.Timeout = TimeSpan.FromMinutes(5));
builder.Services.AddSingleton<ProAdmin.Migration.MigrationClient>();
builder.Services.AddSingleton(new NetworkAddressMapper(appOptions.LocalhostAlias));
builder.Services.AddSingleton(new ProAdmin.Infrastructure.AuthFailureLimiter());
builder.Services.AddSingleton<ProAdmin.Modules.CiJobVerifier>();
builder.Services.AddSingleton<ProAdmin.Modules.CiAuth>();
builder.Services.AddSingleton<SettingsStore>();
builder.Services.AddSingleton<DeployAccounts>();
builder.Services.AddSingleton<ProAdmin.Servers.ServerSettingsStore>();
builder.Services.AddSingleton<SessionVault>();
builder.Services.AddSingleton<PilotGateway>();
builder.Services.AddSingleton<GitLabAuthService>();
builder.Services.AddSingleton<GitLabSignIn>();
builder.Services.AddSingleton<GitLabClientFactory>();
builder.Services.AddSingleton<ProAdmin.Sessions.GitLabAdminCheck>();
builder.Services.AddSingleton<AutomationCatalog>();
builder.Services.AddSingleton<AutomationOperations>();
builder.Services.AddSingleton<JobStore>();
builder.Services.AddSingleton<PublishJobRunner>();
builder.Services.AddSingleton<PilotReportGateway>();
builder.Services.AddSingleton<ReportCatalog>();
builder.Services.AddSingleton<ReportJobRunner>();
builder.Services.AddSingleton<ProAdmin.Content.ContentReleaseRunner>();
builder.Services.AddSingleton<PilotModuleGateway>();
builder.Services.AddSingleton<ProAdmin.Pilot.ServerModules.PilotServerModuleGateway>();
builder.Services.AddSingleton<ProAdmin.Pilot.ServerModules.ServerModuleTarget>();
builder.Services.AddSingleton<ProAdmin.Modules.ModuleTargets>();
builder.Services.AddSingleton<ProAdmin.Modules.DisabledModuleStore>();
builder.Services.AddSingleton<ProAdmin.Modules.ModuleAvailabilityService>();
builder.Services.AddSingleton<ProAdmin.Modules.ModuleCatalog>();
builder.Services.AddSingleton<ProAdmin.Modules.AssemblySpecStore>();
builder.Services.AddSingleton<ProAdmin.Modules.AssemblyScanService>();
builder.Services.AddSingleton<ProAdmin.Modules.ModuleJobRunner>();
builder.Services.AddSingleton(sp => new ProAdmin.Modules.OpenToolsReleases(
    sp.GetRequiredService<IHttpClientFactory>(), sp.GetRequiredService<ILogger<ProAdmin.Modules.OpenToolsReleases>>(),
    Path.Combine(dataDirectory, ProAdmin.Modules.OpenToolsReleases.LocalFolder)));
builder.Services.AddSingleton<ProAdmin.Modules.OpenToolsService>();
builder.Services.AddSingleton<ProAdmin.Compare.CompareService>();
builder.Services.AddSingleton<ProAdmin.Types.TypesStore>();
builder.Services.AddSingleton<ProAdmin.Types.TypesJobRunner>();
builder.Services.AddSingleton<ProAdmin.ActiveWork.ActiveWorkService>();
builder.Services.AddHostedService<SessionCleanupService>();

// X-Forwarded-* принимаются только по явному App__TrustForwardedHeaders: иначе клиент подделает адрес и схему.
if (appOptions.TrustForwardedHeaders)
    builder.Services.Configure<ForwardedHeadersOptions>(options =>
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
        options.KnownIPNetworks.Clear();
        options.KnownProxies.Clear();
    });

var app = builder.Build();

// Создаёт data/settings.json из .env при первом запуске.
app.Services.GetRequiredService<SettingsStore>();

if (appOptions.TrustForwardedHeaders)
    app.UseForwardedHeaders();
// Публичный адрес https: TLS заканчивается на прокси перед админкой, а сюда запрос приходит по http.
// Считаем схему https, иначе antiforgery с Secure=Always не работает, а cookie не получили бы флаг Secure.
if (secureCookies == CookieSecurePolicy.Always)
    app.Use((context, next) =>
    {
        context.Request.Scheme = "https";
        return next();
    });
app.UseSecurityHeaders();
if (!app.Environment.IsDevelopment())
    app.UseExceptionHandler("/Error");
// Ответы без тела (404, 403, 429) получают страницу с понятным текстом вместо пустого окна.
app.UseStatusCodePagesWithReExecute("/Error", "?code={0}");

// Статика отдаётся до входа (стили нужны странице входа), кроме скачиваемых файлов.
app.UseWhen(context => context.Request.Path.StartsWithSegments("/downloads"), branch =>
{
    branch.UseAuthentication();
    branch.Use(async (context, next) =>
    {
        if (context.User.Identity?.IsAuthenticated != true)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }
        await next();
    });
});
app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/health", () => Results.Text("ok")).AllowAnonymous();
app.MapRazorPages();
ProAdmin.Modules.ModuleCiDeployEndpoint.Map(app);
ProAdmin.Modules.ModuleCiLibrariesEndpoint.Map(app);

app.Run();

/// <summary>Точка входа - публичная для интеграционных тестов (WebApplicationFactory).</summary>
public partial class Program;
