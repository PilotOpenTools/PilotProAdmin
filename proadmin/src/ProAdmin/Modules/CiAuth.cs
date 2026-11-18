using System.Security.Cryptography;
using System.Text;
using ProAdmin.GitLab;
using ProAdmin.Infrastructure;
using ProAdmin.Settings;
using Microsoft.Extensions.Options;

namespace ProAdmin.Modules;

/// <summary>Итог проверки запроса из CI: либо подтверждённое задание (и контур выкладки), либо готовый ответ-отказ.</summary>
public sealed record CiAuthResult(CiJob? Job, string? Env, IResult? Rejection)
{
    public bool Ok => Rejection is null;
}

/// <summary>
/// Доказательства для эндпойнтов CI (они анонимны для cookie): X-Job-Token - job-токен, подтверждённый GitLab
/// (ветка, коммит, проект берутся из ответа GitLab), и для выкладки ещё X-Deploy-Key - ключ контура, выведенного из
/// подтверждённой ветки. Проект обязан лежать в группе модулей данного вида.
/// </summary>
public sealed class CiAuth(IOptions<AppOptions> options, SettingsStore settings, CiJobVerifier verifier,
    AuthFailureLimiter limiter, ILogger<CiAuth> logger)
{
    // Заблокированный адрес по-прежнему может прислать подтверждённый токен, а узнать это можно только у GitLab:
    // число таких проверок с адреса ограничено, чтобы блокировку нельзя было превратить в поток запросов к GitLab.
    private readonly AuthFailureLimiter _blockedVerifications = new(limit: 60);

    public const string JobTokenHeader = "X-Job-Token";
    public const string DeployKeyHeader = ModuleCiDeployEndpoint.KeyHeader;

    /// <param name="requireDeployKey">true - выкладка (нужен и ключ контура); false - проверка без изменений на серверах (только job-токен).</param>
    public async Task<CiAuthResult> AuthorizeAsync(HttpRequest request, ModuleKind kind, bool requireDeployKey, CancellationToken ct)
    {
        // Задания раннера приходят с одного адреса: блокировка адреса не касается запросов с подтверждённым job-токеном,
        // иначе десять отказов одного задания остановили бы выкладки всех проектов.
        var address = request.HttpContext.Connection.RemoteIpAddress?.ToString();
        var blocked = limiter.IsBlocked(AuthFailureLimiter.CiScope, address);
        const string blockedText = "Слишком много неверных токенов с этого адреса. Повторите через 5 минут.";

        var current = settings.Current;
        if (!GitLabLocation.TryParse(kind.GroupUrl(current), out var group, out _))
            return Reject(503, $"В настройках админки не задана группа GitLab {(kind == ModuleKind.Server ? "серверных " : "клиентских ")}модулей - проверить job-токен не по чему.");

        var token = request.Headers[JobTokenHeader].ToString();
        if (token.Length == 0)
            return blocked ? Reject(429, blockedText) : Fail(address, 401, $"Нужен заголовок {JobTokenHeader}: $CI_JOB_TOKEN задания (устаревший шаблон CI - обновите его кнопкой «Обновить CI» на странице модуля).");

        if (blocked && (_blockedVerifications.IsBlocked(AuthFailureLimiter.CiScope, address)
                        || _blockedVerifications.Fail(AuthFailureLimiter.CiScope, address)))
            return Reject(429, blockedText);

        var check = await verifier.VerifyAsync(group!.BaseUrl, token, ct);
        if (check.GitLabUnavailable)
            return Reject(503, "GitLab не ответил на проверку job-токена. Повторите позже.");
        var job = check.Job;
        if (job is null)
            return blocked ? Reject(429, blockedText) : Fail(address, 401, "GitLab не подтвердил job-токен: токен неверный или задание уже завершено.");

        if (!InGroup(job.ProjectPath, group.Path))
            return Reject(403, $"Проект «{job.ProjectPath}» не входит в группу модулей «{group.Path}» (Настройки админки).");

        if (!requireDeployKey)
            return new CiAuthResult(job, null, null);

        // Тег может называться как ветка (main, release/1.0.0): контур выводится только из ветки.
        if (job.IsTag)
            return Reject(400, $"Задание запущено на теге {job.Ref} - выкладка идёт только с веток dev, release/*, main/master.");
        var env = ModuleCiDeployEndpoint.EnvForRef(job.Ref);
        if (env is null)
            return Reject(400, $"Ветка {job.Ref} не выкладывается (только dev, release/*, main/master).");
        var expected = options.Value.CiDeploy.KeyFor(env);
        if (expected is null)
            return Reject(503, $"Выкладка на контур «{env}» не настроена: {CiDeployOptions.KeyHint(env)}.");
        // Неверные ключи считаются по проекту подтверждённого задания: перебор блокирует только выкладки этого проекта.
        var project = "project:" + job.ProjectPath;
        if (limiter.IsBlocked(AuthFailureLimiter.CiScope, project))
            return Reject(429, $"Слишком много неверных ключей выкладки из проекта «{job.ProjectPath}». Повторите через 5 минут.");
        if (!KeyMatches(request.Headers[DeployKeyHeader], expected))
            return Fail(project, 401, $"Неверный ключ выкладки контура «{env}» (переменная DEPLOY_KEY_{env.ToUpperInvariant()} в GitLab).");

        return new CiAuthResult(job, env, null);
    }

    /// <summary>Поля формы больше не источник истины: переданное и не совпавшее с подтверждённым GitLab значение - ошибка.</summary>
    public static IResult? CheckForm(IFormCollection form, CiJob job, bool checkRefAndSha = true)
    {
        foreach (var (name, verified) in new (string, string?)[]
                 {
                     ("project", job.ProjectPath),
                     ("ref", checkRefAndSha ? job.Ref : null),
                     ("sha", checkRefAndSha ? job.Sha : null),
                     ("user", job.User),
                     ("pipeline_url", job.PipelineUrl),
                     ("project_url", job.ProjectUrl),
                 })
        {
            var given = form[name].ToString();
            if (verified is not null && given.Length > 0 && !string.Equals(given.TrimEnd('/'), verified.TrimEnd('/'), StringComparison.Ordinal))
                return Text(400, $"Поле {name}={given} не совпадает со значением, подтверждённым GitLab ({verified}).");
        }
        return null;
    }

    internal static bool InGroup(string projectPath, string groupPath) =>
        projectPath.StartsWith(groupPath + "/", StringComparison.OrdinalIgnoreCase);

    internal static bool KeyMatches(string? given, string expected) =>
        given is not null && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(given), Encoding.UTF8.GetBytes(expected));

    private CiAuthResult Fail(string? source, int status, string text)
    {
        if (limiter.Fail(AuthFailureLimiter.CiScope, source))
            logger.LogWarning("CI-эндпойнты: {Source} заблокирован на 5 минут после серии неверных ключей или токенов", source);
        else
            logger.LogWarning("CI-эндпойнты: отказ ({Status}) для {Source}: {Reason}", status, source, text);
        return Reject(status, text);
    }

    private static IResult Text(int status, string text) => Results.Text(text + "\n", statusCode: status);

    private static CiAuthResult Reject(int status, string text) => new(null, null, Text(status, text));
}
