using ProAdmin.Jobs;
using ProAdmin.Modules;
using ProAdmin.Pilot;
using ProAdmin.Pilot.ServerModules;
using ProAdmin.Sessions;
using Microsoft.AspNetCore.Mvc;

namespace ProAdmin.Pages.ServerModules;

/// <summary>
/// Серверные модули Pilot-Server через центральный модуль ServerHost: список, загрузка архива с проверкой сборок,
/// горячее обновление, отключение, откат, удаление. Контуры - как у клиентских модулей (тест / стейдж / прод).
/// </summary>
[RequestSizeLimit(210 * 1024 * 1024)]
[RequestFormLimits(MultipartBodyLengthLimit = 210 * 1024 * 1024)]
public class IndexModel : AppPageModel
{
    private readonly PilotServerModuleGateway _gateway;
    private readonly PilotGateway _automations;
    private readonly PilotModuleGateway _extensions;
    private readonly JobStore _jobs;
    private readonly IWebHostEnvironment _web;
    private readonly ILogger<IndexModel> _logger;

    public IndexModel(PilotServerModuleGateway gateway, PilotGateway automations, PilotModuleGateway extensions, JobStore jobs,
        IWebHostEnvironment web, ILogger<IndexModel> logger)
    {
        _gateway = gateway;
        _automations = automations;
        _extensions = extensions;
        _jobs = jobs;
        _web = web;
        _logger = logger;
    }

    /// <summary>Права на изменение - по группе серверных модулей GitLab (если не задана - по группе модулей).</summary>
    public override string? ModulesGroupPath => AppSettings.ServerModulesGroupPath ?? AppSettings.ModulesGroupPath;
    protected override object? ModulesListRoute => new { kind = "server" };

    /// <summary>Сборка debug для разработчиков лежит в wwwroot/downloads (кладёт publish проекта Debug).</summary>
    public bool DebugAvailable => _web.WebRootFileProvider.GetFileInfo("downloads/debug.zip").Exists;

    [BindProperty(SupportsGet = true)] public string? Env { get; set; }
    [BindProperty] public IFormFile? Archive { get; set; }
    [BindProperty] public bool Strip { get; set; }
    [BindProperty] public bool AllowDowngrade { get; set; }
    [BindProperty] public string? UploadId { get; set; }
    [BindProperty] public string? Reason { get; set; }
    [BindProperty] public string? Name { get; set; }
    [BindProperty] public string? EnvLogin { get; set; }
    [BindProperty] public string? EnvPassword { get; set; }

    public List<ModuleEnv> Envs { get; private set; } = new();
    public ModuleEnv? Current { get; private set; }
    public HostState? State { get; private set; }
    public PilotGatewayException? Error { get; private set; }
    public ValidationReport? Report { get; private set; }
    public string? CredentialError { get; private set; }

    /// <summary>Объект ServerHost в базе ($PilotServerExtension); null - не найден или не удалось прочитать (<see cref="HostLookupError"/>).</summary>
    public PilotModule? HostExtension { get; private set; }
    public string? HostLookupError { get; private set; }

    public bool IsProd => Current?.Key == ModuleEnv.Prod;

    public async Task OnGetAsync(CancellationToken ct)
    {
        Prepare();
        await LoadModulesAccessAsync(ct);
        await LoadAsync(ct);
    }

    /// <summary>Загрузка архива на сервер и проверка (сборки, которые уже есть у сервера, MEF-активности).</summary>
    public async Task<IActionResult> OnPostValidateAsync(CancellationToken ct)
    {
        if (await GuardAsync(ct) is { } deny)
            return deny;
        if (Archive is null || Archive.Length == 0)
        {
            TempData["FlashError"] = "Выберите zip-архив модуля.";
            return Back();
        }

        // Архив уходит на сервер кусками прямо из потока формы - в памяти целиком он не нужен.
        await using var zip = Archive.OpenReadStream();
        return await ShowReportAsync(() => _gateway.UploadAndValidateAsync(Current!.PilotUrl, Login(), AppSettings.ReportsLicenseType,
            Path.GetFileName(Archive.FileName), zip, Archive.Length, Strip, ct), ct);
    }

    /// <summary>
    /// Новая версия самого ServerHost: zip записывается в объект хоста в базе, как при ручной установке. Pilot-Server
    /// читает его только при старте, поэтому версия вступает в силу после перезапуска. Только администратору админки.
    /// </summary>
    public async Task<IActionResult> OnPostUpdateHostAsync(CancellationToken ct)
    {
        Prepare();
        if (Current is not null && !await ConfirmAdminAsync(ct))
        {
            _logger.LogWarning("Отказ (403): обновление ServerHost - {Login} не администратор админки, адрес {Address}",
                CurrentSession.Actor, HttpContext.Connection.RemoteIpAddress);
            return StatusCode(StatusCodes.Status403Forbidden);
        }
        if (await GuardAsync(ct) is { } deny)
            return deny;
        if (Archive is null || Archive.Length == 0)
        {
            TempData["FlashError"] = "Выберите zip-архив ServerHost.";
            return Back();
        }

        var fileName = Path.GetFileName(Archive.FileName);
        PilotModule? host;
        ModulePackageInfo package;
        bool downgrade;
        try
        {
            ServerHostPackage.CheckSize(Archive.Length, fileName);
            host = ServerHostPackage.Find(await _extensions.ListServerExtensionsAsync(Current!.PilotUrl, Login(), AppSettings.ReportsLicenseType, ct));

            var zip = new byte[Archive.Length];
            await using (var stream = Archive.OpenReadStream())
                await stream.ReadExactlyAsync(zip, ct);
            (package, downgrade) = ServerHostPackage.Check(zip, fileName, host?.Version);
        }
        catch (Exception ex) when (ex is InvalidDataException or PilotGatewayException)
        {
            TempData["FlashError"] = $"{Current!.Title}: {ex.Message}";
            return Back();
        }

        if (host is null)
            return await InstallHostAsync(package, ct);

        if (downgrade && !AllowDowngrade)
        {
            TempData["FlashError"] = $"{Current.Title}: версия в архиве {package.Version} ниже записанной в базе ({host.Version}). Если это намеренно, отметьте «Понизить версию».";
            return Back();
        }

        var actor = CurrentSession.Actor;
        var job = new PublishJob
        {
            Operation = "обновление ServerHost",
            Env = Current.Key,
            EnvTitle = Current.Title,
            Branch = "расширение сервера",
            AutomationName = ServerHostPackage.ExtensionName,
            CommitMessage = $"{host.Version} / {package.Version}{(downgrade ? " (понижение версии)" : "")}",
            PilotUser = CurrentSession.PilotLogin,
            GitLabUser = CurrentSession.GitLabUser?.Username,
        };
        job.Add("info", job.CommitMessage);
        _jobs.Add(job);

        try
        {
            await _extensions.UpdateServerExtensionAsync(Current.PilotUrl, Login(), AppSettings.ReportsLicenseType, host.Id,
                host.FileName ?? ServerHostPackage.ExtensionName + ".zip", package.Zip, package.ExtensionAdditional,
                $"{package.Version} · загружена из админки, {actor}", ct);
        }
        catch (Exception ex)
        {
            FinishJob(job, ex.Message);
            if (ex is not PilotGatewayException)
                throw;
            TempData["FlashError"] = $"{Current.Title}: {ex.Message}";
            return Back();
        }

        job.Add("ok", $"Версия {package.Version} записана в базу {Current.Title}; вступит в силу после перезапуска сервера Pilot.");
        FinishJob(job, null);
        TempData["Flash"] = $"{Current.Title}: ServerHost {package.Version} записан в базу. " +
                            "Новая версия хоста вступит в силу после перезапуска сервера Pilot (всех узлов в кластере).";
        return Back();
    }

    /// <summary>Первая установка хоста: объект $PilotServerExtension в папке расширений; работает после перезапуска сервера.</summary>
    private async Task<IActionResult> InstallHostAsync(ModulePackageInfo package, CancellationToken ct)
    {
        var job = new PublishJob
        {
            Operation = "установка ServerHost",
            Env = Current!.Key,
            EnvTitle = Current.Title,
            Branch = "расширение сервера",
            AutomationName = ServerHostPackage.ExtensionName,
            CommitMessage = package.Version,
            PilotUser = CurrentSession.PilotLogin,
            GitLabUser = CurrentSession.GitLabUser?.Username,
        };
        _jobs.Add(job);
        try
        {
            await _extensions.CreateServerExtensionAsync(Current.PilotUrl, Login(), AppSettings.ReportsLicenseType, ServerHostPackage.ExtensionName,
                ServerHostPackage.ExtensionName + ".zip", package.Zip, package.ExtensionAdditional,
                $"{package.Version} · установлен из админки, {CurrentSession.Actor}", ct);
        }
        catch (Exception ex)
        {
            FinishJob(job, ex.Message);
            if (ex is not PilotGatewayException)
                throw;
            TempData["FlashError"] = $"{Current.Title}: {ex.Message}";
            return Back();
        }
        job.Add("ok", $"ServerHost {package.Version} установлен в базу {Current.Title}; заработает после перезапуска сервера Pilot.");
        FinishJob(job, null);
        TempData["Flash"] = $"{Current.Title}: ServerHost {package.Version} установлен в базу. Перезапустите сервер Pilot (все узлы в кластере) - " +
                            "это последний перезапуск, дальше серверные модули обновляются отсюда.";
        return Back();
    }

    /// <summary>Повторная проверка того же архива - обычно чтобы выбросить лишние сборки.</summary>
    public async Task<IActionResult> OnPostRevalidateAsync(CancellationToken ct)
    {
        if (await GuardAsync(ct) is { } deny)
            return deny;
        return await ShowReportAsync(() => _gateway.RevalidateAsync(Current!.PilotUrl, Login(), AppSettings.ReportsLicenseType, UploadId ?? "", Strip, ct), ct);
    }

    public async Task<IActionResult> OnPostInstallAsync(CancellationToken ct)
    {
        if (await GuardAsync(ct) is { } deny)
            return deny;
        return await ModuleOpAsync(new HostRequest { Op = HostOps.Install, UploadId = UploadId, Reason = Reason?.Trim() }, "установлен", ct);
    }

    public async Task<IActionResult> OnPostEnableAsync(CancellationToken ct) =>
        await GuardAsync(ct) ?? await ModuleOpAsync(new HostRequest { Op = HostOps.SetEnabled, Name = Name, Enabled = true }, "включён", ct);

    public async Task<IActionResult> OnPostDisableAsync(CancellationToken ct) =>
        await GuardAsync(ct) ?? await ModuleOpAsync(new HostRequest { Op = HostOps.SetEnabled, Name = Name, Enabled = false }, "отключён", ct);

    public async Task<IActionResult> OnPostReloadAsync(CancellationToken ct) =>
        await GuardAsync(ct) ?? await ModuleOpAsync(new HostRequest { Op = HostOps.Reload, Name = Name }, "перезагружен", ct);

    public async Task<IActionResult> OnPostRollbackAsync(CancellationToken ct) =>
        await GuardAsync(ct) ?? await ModuleOpAsync(new HostRequest { Op = HostOps.Rollback, Name = Name }, "откачен на предыдущую версию", ct);

    public async Task<IActionResult> OnPostRemoveAsync(CancellationToken ct) =>
        await GuardAsync(ct) ?? await ModuleOpAsync(new HostRequest { Op = HostOps.Remove, Name = Name }, "удалён", ct);

    /// <summary>
    /// debug.json для локальной отладки активности в debug: сервер контура, логин разработчика,
    /// событие/sourceTypes/params из автоматизаций контура. Только чтение - права на изменение модулей не нужны.
    /// </summary>
    public async Task<IActionResult> OnGetDebugCaseAsync(string activity, string? module, string? sourceId, bool apply, CancellationToken ct)
    {
        Prepare();
        if (Current is null || string.IsNullOrWhiteSpace(activity))
            return Back();
        Guid? source = Guid.TryParse(sourceId?.Trim().Trim('{', '}'), out var g) ? g : null;

        IReadOnlyList<AutomationSnapshot> automations;
        try
        {
            automations = await _automations.GetAutomationsAsync(Current.PilotUrl, Vault.GetPilotLogin(CurrentSession, Current.Key), ct);
        }
        catch (PilotGatewayException ex)
        {
            // Без автоматизаций кейс всё равно полезен: сервер, логин, активность.
            automations = Array.Empty<AutomationSnapshot>();
            TempData["FlashError"] = $"{Current.Title}: автоматизации не прочитаны ({ex.Message}) - params в кейсе пустые.";
        }

        var json = DebugCaseBuilder.Build(Current.PilotUrl, CurrentSession.PilotLogin, AppSettings.ReportsLicenseType,
            activity, module, source, automations, apply, Current.Key);
        return File(System.Text.Encoding.UTF8.GetBytes(json), "application/json", "debug.json");
    }

    /// <summary>Пользователь базы (администратор) для контура - та же учётка, что для клиентских модулей.</summary>
    public async Task<IActionResult> OnPostCredentialsAsync(CancellationToken ct)
    {
        Prepare();
        if (Current is null)
            return Back();
        if (UsesServiceAccounts)
            return NotFound(); // при входе через GitLab учётки Pilot - только служебные из «Настроек»
        if (BlockedCredentialAttempt() is { } blocked)
            return blocked;
        var login = new PilotLogin(EnvLogin?.Trim() ?? "", EnvPassword ?? "");
        try
        {
            await _gateway.GetStateAsync(Current.PilotUrl, login, AppSettings.ReportsLicenseType, ct);
            Vault.SetEnvironmentCredential(CurrentSession, SessionVault.ModulesPrefix + Current.Key, login);
            return Back();
        }
        catch (PilotGatewayException ex) when (ex.Kind != PilotErrorKind.Configuration)
        {
            CredentialAttemptFailed(Current.Key);
            CredentialError = ex.Message;
            await LoadAsync(ct);
            return Page();
        }
        catch (PilotGatewayException)
        {
            // Вход удался, но хоста нет - учётку всё равно запоминаем, страница покажет, что делать дальше.
            Vault.SetEnvironmentCredential(CurrentSession, SessionVault.ModulesPrefix + Current.Key, login);
            return Back();
        }
    }

    // ------------------------------------------------------------------

    private void Prepare()
    {
        ViewData["Section"] = "servermodules";
        Envs = ModuleEnv.All(AppSettings);
        Current = Envs.FirstOrDefault(e => e.Key == Env) ?? Envs.FirstOrDefault();
        Env = Current?.Key;
    }

    private async Task<IActionResult?> GuardAsync(CancellationToken ct)
    {
        Prepare();
        if (Current is null)
            return Back();
        return await DenyModulesChangeAsync(ct);
    }

    private PilotLogin Login() => ModuleLogin(Current!.Key);

    private async Task LoadAsync(CancellationToken ct)
    {
        if (Current is null)
            return;
        try
        {
            State = await _gateway.GetStateAsync(Current.PilotUrl, Login(), AppSettings.ReportsLicenseType, ct);
        }
        catch (PilotGatewayException ex)
        {
            Error = ex;
        }

        // Версия хоста в базе нужна и когда сервер его ещё не загрузил; без входа или при сбое связи искать нечем.
        if (Error is not null && Error.Kind != PilotErrorKind.Configuration)
            return;
        try
        {
            HostExtension = ServerHostPackage.Find(await _extensions.ListServerExtensionsAsync(Current.PilotUrl, Login(), AppSettings.ReportsLicenseType, ct));
        }
        catch (PilotGatewayException ex)
        {
            HostLookupError = ex.Message;
        }
    }

    private void FinishJob(PublishJob job, string? error)
    {
        job.Update(j =>
        {
            j.Status = error is null ? JobStatus.Succeeded : JobStatus.Failed;
            j.Error = error;
            j.FinishedAt = DateTimeOffset.UtcNow;
        });
        if (error is not null)
            job.Add("error", error);
        _jobs.Persist();
    }

    private async Task<IActionResult> ShowReportAsync(Func<Task<HostResponse>> call, CancellationToken ct)
    {
        try
        {
            var response = await call();
            if (response.Report is null)
            {
                TempData["FlashError"] = $"{Current!.Title}: {response.Error ?? "проверка не выполнена"}";
                return Back();
            }
            Report = response.Report;
            UploadId = Report.UploadId;
        }
        catch (PilotGatewayException ex)
        {
            TempData["FlashError"] = $"{Current!.Title}: {ex.Message}";
            return Back();
        }
        await LoadModulesAccessAsync(ct);
        await LoadAsync(ct);
        return Page();
    }

    private async Task<IActionResult> ModuleOpAsync(HostRequest request, string done, CancellationToken ct)
    {
        request.Actor = CurrentSession.Actor;
        try
        {
            var response = await _gateway.InvokeAsync(Current!.PilotUrl, Login(), AppSettings.ReportsLicenseType, request, ct);
            var name = response.Module?.Name ?? request.Name ?? "модуль";
            if (response.Error is not null)
            {
                TempData["FlashError"] = $"{Current.Title}: {name} - {response.Error}";
                return Back();
            }

            var message = $"{Current.Title}: {name}{(response.Module is { } m ? " " + m.Version : "")} {done}.";
            if (response.RebindRequired)
            {
                // Новые активности: Pilot привяжет их к сценариям после изменения метаданных базы.
                var rebind = await _gateway.RebindAutomationsAsync(Current.PilotUrl, Vault.GetPilotLogin(CurrentSession, Current.Key), ct);
                message += rebind is null
                    ? " Новые активности подключены к автоматизациям."
                    : $" Новые активности заработают после ближайшего изменения автоматизаций базы (не удалось обновить метаданные: {rebind}).";
            }
            TempData["Flash"] = message;
        }
        catch (PilotGatewayException ex)
        {
            TempData["FlashError"] = $"{Current!.Title}: {ex.Message}";
        }
        return Back();
    }

    private IActionResult Back() => RedirectToPage(new { env = Env });

    public static string FileStatusLabel(string status) => status switch
    {
        FileStatus.Main => "главная",
        FileStatus.Private => "своя",
        FileStatus.Duplicate => "уже на сервере",
        FileStatus.VersionMismatch => "другая версия на сервере",
        FileStatus.Native => "не .NET",
        _ => "файл",
    };

    public static string FileStatusCss(string status) => status switch
    {
        FileStatus.Main => "badge-onlypilot",
        FileStatus.Private => "badge-synced",
        FileStatus.Duplicate => "badge-failed",
        FileStatus.VersionMismatch => "badge-differs",
        _ => "",
    };
}
