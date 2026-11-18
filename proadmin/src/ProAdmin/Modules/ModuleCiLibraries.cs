using System.Text;
using ProAdmin.Infrastructure;
using ProAdmin.Pilot;
using ProAdmin.Settings;
using Microsoft.Extensions.Options;

namespace ProAdmin.Modules;

/// <summary>
/// Проверка библиотек модуля из GitLab CI (job check_libraries шаблона): zip из build_module сверяется с режимом
/// библиотек (Настройки / «Библиотеки модулей») - без выкладки. Последняя строка ответа - «RESULT: ok | warn | error»:
/// error - режимы 1 и 2 (job красный), warn - режим 3 или проверка неполная (job жёлтый).
/// Общий модуль (режим 1) читается с контура ветки: dev и ветки задач - тест, release/* - стейдж, main - прод.
/// Ничего не меняет на серверах Pilot, поэтому хватает одного X-Job-Token (см. <see cref="CiAuth"/>): ключ выкладки не нужен.
/// </summary>
public static class ModuleCiLibrariesEndpoint
{
    public const string Route = "/api/ci/modules/check-libraries";
    public const string ServerRoute = "/api/ci/server-modules/check-libraries";

    public static void Map(WebApplication app)
    {
        foreach (var (route, kind) in new[] { (Route, ModuleKind.Client), (ServerRoute, ModuleKind.Server) })
            app.MapPost(route, (HttpRequest request, CiAuth auth, IOptions<AppOptions> options, SettingsStore settings, AssemblySpecStore libraries,
                    AssemblyScanService scan, IHostApplicationLifetime lifetime) =>
                HandleAsync(request, auth, kind, options.Value, settings.Current, libraries, scan, lifetime.ApplicationStopping))
                .AllowAnonymous().DisableAntiforgery();
    }

    private static async Task<IResult> HandleAsync(HttpRequest request, CiAuth auth, ModuleKind kind, AppOptions options, AdminSettings settings,
        AssemblySpecStore libraries, AssemblyScanService scan, CancellationToken ct)
    {
        var ci = options.CiDeploy;
        var authorized = await auth.AuthorizeAsync(request, kind, requireDeployKey: false, ct);
        if (authorized.Rejection is { } rejection)
            return rejection;
        var verified = authorized.Job!;
        if (!request.HasFormContentType)
            return Results.Text("Ожидается multipart/form-data: file (zip модуля).\n", statusCode: 400);

        var form = await request.ReadFormAsync(ct);
        // В pipeline merge request GitLab называет ref иначе (refs/merge-requests/N/head), чем CI_COMMIT_REF_NAME: сверяем только проект.
        if (CiAuth.CheckForm(form, verified, checkRefAndSha: false) is { } mismatch)
            return mismatch;
        var file = form.Files.GetFile("file");
        if (file is null || file.Length == 0)
            return Results.Text("Нужно поле file (zip модуля).\n", statusCode: 400);
        var project = verified.ProjectPath;
        var reference = verified.Ref;

        byte[] zip;
        await using (var stream = file.OpenReadStream())
        using (var buffer = new MemoryStream())
        {
            await stream.CopyToAsync(buffer, ct);
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

        var spec = libraries.For(kind);
        var text = new StringBuilder();
        var incomplete = new List<string>();
        text.Append($"Режим библиотек ({(kind == ModuleKind.Server ? "серверные" : "клиентские")} модули): ")
            .Append(AssemblySpec.ModeTitle(spec.Mode)).Append(spec.IsShared ? $", общий модуль «{spec.SharedModule}»" : "").Append('\n');

        SharedModuleState? shared = null;
        if (spec.IsShared)
        {
            var target = form["target"].ToString();
            // Контур - только чтение общего модуля: по целевой ветке MR (если есть), иначе по подтверждённой ветке.
            var envKey = ModuleCiDeployEndpoint.EnvForRef(target.Length > 0 ? target : reference) ?? ModuleEnv.Test;
            var env = ModuleEnv.Find(settings, envKey);
            if (env is null)
                incomplete.Add($"в настройках нет сервера контура «{envKey}»");
            else if (ci.Login(envKey) is not { } login)
                incomplete.Add($"нет служебной учётки Pilot контура «{envKey}» (App__CiDeploy__Logins__{envKey}__…)");
            else
            {
                try
                {
                    shared = await scan.SharedOnEnvAsync(spec, env, login, settings.ReportsLicenseType, ct, kind);
                    text.Append(shared is { Found: true }
                        ? $"Общий модуль на сервере {env.Title}: {shared.Name} {shared.Version}, библиотек: {shared.Assemblies.Count(a => !a.IsMain)}.\n"
                        : $"Общего модуля нет на сервере {env.Title}.\n");
                }
                catch (PilotGatewayException ex)
                {
                    incomplete.Add($"общий модуль на сервере {env.Title} не прочитан: {ex.Message}");
                }
            }
            if (incomplete.Count > 0)
                incomplete.Add("ссылки модуля на библиотеки не проверены");
        }

        var check = AssemblyChecker.Check(spec, package, project.Split('/').Last(), shared, kind);
        text.Append($"Модуль {package.AssemblyName} {package.Version}:\n").Append(check.Details).Append('\n');
        foreach (var i in incomplete)
            text.Append("ВНИМАНИЕ: ").Append(i).Append('\n');
        text.Append("Итог: ").Append(check.Summary).Append('\n');

        var result = check.HasProblems ? (spec.Blocks ? "error" : "warn") : incomplete.Count > 0 ? "warn" : "ok";
        if (result == "error")
            text.Append("Режим «").Append(AssemblySpec.ModeTitle(spec.Mode)).Append("»: нарушения запрещают выкладку - уберите лишние dll и зависимости или дополните список библиотек.\n");
        text.Append("RESULT: ").Append(result).Append('\n');
        return Results.Text(text.ToString(), "text/plain; charset=utf-8");
    }
}
