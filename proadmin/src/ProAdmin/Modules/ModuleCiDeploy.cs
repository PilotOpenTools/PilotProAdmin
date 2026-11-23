using System.Security.Cryptography;
using System.Text;
using ProAdmin.Infrastructure;
using ProAdmin.Jobs;
using ProAdmin.Pilot;
using ProAdmin.Settings;
using ProAdmin.Types;
using Microsoft.Extensions.Options;

namespace ProAdmin.Modules;

/// <summary>
/// Выкладка модуля из GitLab CI: job deploy_* шаблона модуля отправляет сюда zip, собранный job build_module,
/// а админка пишет его на сервер Pilot контура служебной учёткой - как кнопка «Выложить», с тем же журналом и откатом.
///   dev / тест,  release/* / стейдж,  main / master / прод.
/// Запрос доказывает себя дважды (<see cref="CiAuth"/>): X-Job-Token - job-токен, который проверяет GitLab (ветка, коммит,
/// проект и автор берутся из его ответа, а не из полей формы), и X-Deploy-Key - ключ контура, выведенного из этой ветки.
/// GitLab Release (тег vX.Y.Z) на main создаёт сам pipeline модуля, а не админка.
/// </summary>
public static class ModuleCiDeployEndpoint
{
    public const string Route = "/api/ci/modules/deploy";

    /// <summary>Серверные модули: zip из CI ставится в ServerHost контура.</summary>
    public const string ServerRoute = "/api/ci/server-modules/deploy";
    public const string KeyHeader = "X-Deploy-Key";

    /// <summary>Контур по ветке; null - ветка не выкладывается.</summary>
    public static string? EnvForRef(string? reference) => reference switch
    {
        ModuleBranches.Dev => ModuleEnv.Test,
        ModuleBranches.Main or "master" => ModuleEnv.Prod,
        _ when ModuleBranches.IsRelease(reference) => ModuleEnv.Stage,
        _ => null,
    };

    public static void Map(WebApplication app)
    {
        app.MapPost(Route, (HttpRequest request, CiAuth auth, IOptions<AppOptions> options, DeployAccounts accounts, SettingsStore settings, JobStore store, ModuleTargets targets,
                TypesStore types, AssemblySpecStore assemblies, DisabledModuleStore disabled, IHostApplicationLifetime lifetime, ILogger<ModuleCiDeployWorkflow> logger) =>
            HandleAsync(request, auth, options.Value.PublicUrl, accounts, settings, store, targets.Get(ModuleKind.Client), types, assemblies, disabled, lifetime, logger))
            .AllowAnonymous().DisableAntiforgery();
        app.MapPost(ServerRoute, (HttpRequest request, CiAuth auth, IOptions<AppOptions> options, DeployAccounts accounts, SettingsStore settings, JobStore store, ModuleTargets targets,
                TypesStore types, AssemblySpecStore assemblies, DisabledModuleStore disabled, IHostApplicationLifetime lifetime, ILogger<ModuleCiDeployWorkflow> logger) =>
            HandleAsync(request, auth, options.Value.PublicUrl, accounts, settings, store, targets.Get(ModuleKind.Server), types, assemblies, disabled, lifetime, logger))
            .AllowAnonymous().DisableAntiforgery();
    }

    private static async Task<IResult> HandleAsync(HttpRequest request, CiAuth auth, string? publicUrl, DeployAccounts accounts, SettingsStore settings,
        JobStore store, IModuleTarget pilot, TypesStore types, AssemblySpecStore assemblies, DisabledModuleStore disabled, IHostApplicationLifetime lifetime,
        ILogger<ModuleCiDeployWorkflow> logger)
    {
        var kind = pilot.Kind;
        var authorized = await auth.AuthorizeAsync(request, kind, requireDeployKey: true, lifetime.ApplicationStopping);
        if (authorized.Rejection is { } rejection)
            return rejection;
        var verified = authorized.Job!;
        var envKey = authorized.Env!;
        if (!request.HasFormContentType)
            return Results.Text("Ожидается multipart/form-data: file (zip модуля).\n", statusCode: 400);

        var form = await request.ReadFormAsync(lifetime.ApplicationStopping);
        if (CiAuth.CheckForm(form, verified) is { } mismatch)
            return mismatch;
        var file = form.Files.GetFile("file");
        if (file is null || file.Length == 0)
            return Results.Text("Нужно поле file (zip модуля).\n", statusCode: 400);

        // Дальше - только значения, подтверждённые GitLab.
        var project = verified.ProjectPath;
        var reference = verified.Ref;
        var sha = verified.Sha;
        var current = settings.Current;
        var env = ModuleEnv.Find(current, envKey);
        if (env is null)
            return Results.Text($"В настройках админки нет сервера Pilot для контура «{envKey}».\n", statusCode: 503);
        if (accounts.Get(current, envKey) is not { } login)
            return Results.Text($"Нет служебной учётки Pilot для контура «{envKey}». Задайте её в настройках админки.\n", statusCode: 503);

        byte[] zip;
        await using (var stream = file.OpenReadStream())
        using (var buffer = new MemoryStream())
        {
            await stream.CopyToAsync(buffer, lifetime.ApplicationStopping);
            zip = buffer.ToArray();
        }

        ModulePackageInfo package;
        try
        {
            package = ModulePackage.Analyze(zip, Path.GetFileName(file.FileName), kind);
        }
        catch (InvalidDataException ex)
        {
            return Results.Text($"Архив модуля не подходит: {ex.Message}\n", statusCode: 400);
        }

        var repoName = project.Split('/').Last();
        var stageProjectPath = envKey == ModuleEnv.Stage ? project : null;
        var notes = form["notes"].ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(20).ToList();
        var job = new PublishJob
        {
            Operation = $"выкладка {(kind == ModuleKind.Server ? "серверного модуля" : "модуля")} из CI",
            Env = kind.JobEnv(envKey),
            EnvTitle = kind.JobEnvTitle(env.Title),
            Branch = $"{reference} / {env.Title}",
            AutomationName = repoName,
            ModuleProjectPath = stageProjectPath,
            CommitMessage = "выкладка из CI",
            ChangeNotes = notes,
            PilotUser = login.Login,
            GitLabUser = verified.User ?? "GitLab CI",
            PipelineUrl = verified.PipelineUrl,
        };
        if (store.FindActive(job.Env, repoName, stageProjectPath) is { } active)
        {
            // Операция выпуска сама запишет сборку после pipeline. Отклонение её deploy_stage заблокировало бы весь pipeline.
            if (envKey == ModuleEnv.Stage && active.ManagedStageRelease == reference &&
                string.Equals(active.ModuleProjectPath, project, StringComparison.OrdinalIgnoreCase))
                return Results.Text($"Релиз {reference} выложит операция админки после завершения pipeline.\n" +
                                    $"Операция в админке: {JobLink(publicUrl, active.Id)}\n", "text/plain; charset=utf-8");
            return Results.Text($"По модулю «{repoName}» на контуре {env.Title} уже идёт операция «{active.Operation}». Повторите job позже.\n", statusCode: 409);
        }
        job.Add("info", $"Модуль «{repoName}»: выкладка из CI ({reference} @ {ModuleDeploy.Short(sha)}).", job.PipelineUrl);
        store.Add(job);

        var ctx = new ModuleContext
        {
            Settings = settings.Current,
            GitLabToken = null!, // в выкладке из CI GitLab не нужен: zip пришёл из job
            ProjectPath = project,
            RepoName = repoName,
            ProjectWebUrl = verified.ProjectUrl,
            Logins = new Dictionary<string, PilotLogin> { [envKey] = login },
            Kind = kind,
            Assemblies = assemblies.For(kind),
            Disabled = kind == ModuleKind.Client ? disabled : null,
        };

        // Связанные изменения типов выпускаются только из админки (нужен GitLab): выкладка из CI их не пропускает молча.
        var waiting = envKey == ModuleEnv.Prod
            ? types.OpenLinksFor(kind == ModuleKind.Server ? TypesTargetKind.Server : TypesTargetKind.Client, project).Where(l => l.StageAt is not null).ToList()
            : new List<TypesChangeLink>();

        var ok = false;
        try
        {
            if (waiting.Count > 0)
                throw new WorkflowException($"С релизом связаны изменения типов ({string.Join("; ", waiting.Select(l => l.TypesTitle))}) - " +
                                            "выпускайте его кнопкой «Выпустить в прод» на странице модуля: типы запишутся на прод вместе с модулем. Модуль НЕ выложен.");
            await new ModuleCiDeployWorkflow(pilot, job, ctx, env, reference, sha, package, notes).RunAsync(lifetime.ApplicationStopping);
            job.Update(j => j.Status = JobStatus.Succeeded);
            job.Add("ok", $"Готово: {package.ExtensionName} {package.Version} на сервере {env.Title}.");
            ok = true;
        }
        catch (Exception ex) when (ex is WorkflowException or PilotGatewayException)
        {
            job.Update(j => { j.Status = JobStatus.Failed; j.Error = ex.Message; });
            job.Add("error", ex.Message, details: (ex as WorkflowException)?.Details);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Сбой выкладки модуля из CI {JobId}", job.Id);
            job.Update(j => { j.Status = JobStatus.Failed; j.Error = ex.Message; });
            job.Add("error", $"Непредвиденная ошибка: {ex.Message}");
        }
        finally
        {
            job.Update(j => j.FinishedAt = DateTimeOffset.UtcNow);
            store.Persist();
        }

        // Журнал операции - прямо в лог job GitLab.
        var text = new StringBuilder();
        foreach (var line in job.Log)
            text.Append('[').Append(line.Level).Append("] ").Append(line.Text).Append('\n');
        text.Append("Операция в админке: ").Append(JobLink(publicUrl, job.Id)).Append('\n');
        return Results.Text(text.ToString(), "text/plain; charset=utf-8", statusCode: ok ? 200 : 502);
    }

    /// <summary>Ссылка на страницу операции; без публичного адреса админки - только путь.</summary>
    internal static string JobLink(string? publicUrl, Guid id) => $"{publicUrl?.Trim().TrimEnd('/')}/Jobs/{id}";

    internal static bool KeyMatches(string? given, string expected) => CiAuth.KeyMatches(given, expected);
}

/// <summary>Zip из CI / сервер контура (новая версия объекта Extension или новый объект).</summary>
internal sealed class ModuleCiDeployWorkflow : ModuleWorkflowBase
{
    private readonly ModuleEnv _env;
    private readonly string _reference;
    private readonly string _sha;
    private readonly ModulePackageInfo _package;
    private readonly IReadOnlyList<string> _notes;

    public ModuleCiDeployWorkflow(IModuleTarget pilot, PublishJob job, ModuleContext ctx, ModuleEnv env,
        string reference, string sha, ModulePackageInfo package, IReadOnlyList<string> notes)
        : base(pilot, null!, job, ctx) // GitLab-клиент не используется
    {
        _env = env;
        _reference = reference;
        _sha = sha;
        _package = package;
        _notes = notes;
    }

    public override async Task RunAsync(CancellationToken ct)
    {
        Ok($"Модуль из CI: {_package.AssemblyName} {_package.Version} ({ModulePackage.FormatSize(_package.Zip.LongLength)}).");
        await DeployAsync(_env, _reference, _sha, _package, _notes, ct);
    }
}
