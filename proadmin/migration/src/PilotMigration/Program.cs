using System.Security.Cryptography;
using System.Text;
using Ascon.Pilot.Common;
using Ascon.Pilot.DataClasses;
using PilotMigration;
using PilotMigration.Archives;
using PilotMigration.Config;
using PilotMigration.Database;
using PilotMigration.Engine;
using PilotMigration.Pilot;
using PilotMigration.Scheduling;

if (args is ["--cluster-readiness"])
{
    await ClusterReadiness.RunAsync();
    return;
}

// Для установщика: секрет S3 в том виде, в каком Pilot-Server хранит его в settings.xml (EncryptedSecretAccessKey).
// Значение - из переменной окружения, чтобы не светить его в аргументах процесса.
if (args is ["--encrypt-aes"])
{
    var secret = Environment.GetEnvironmentVariable("PILOT_SECRET");
    if (string.IsNullOrEmpty(secret))
    {
        Console.Error.WriteLine("Задайте секрет в переменной окружения PILOT_SECRET.");
        Environment.Exit(2);
    }
    Console.Write(secret.EncryptAes());
    return;
}

var builder = WebApplication.CreateBuilder(args);

var options = builder.Configuration.GetSection("Migration").Get<ServiceOptions>() ?? new ServiceOptions();
options.DataDirectory = Path.GetFullPath(options.DataDirectory);
Directory.CreateDirectory(options.DataDirectory);

builder.Services.AddSingleton(options);
builder.Services.AddSingleton<ConfigStore>();
builder.Services.AddSingleton<RunStore>();
builder.Services.AddSingleton<PilotAdmin>();
builder.Services.AddSingleton<MigrationEngine>();
builder.Services.AddSingleton<ScheduleStore>();
builder.Services.AddHostedService<SchedulerService>();

var app = builder.Build();

app.MapGet("/health", () => Results.Text("ok"));

var throttle = new AuthThrottle();

// Всё под /api - только с ключом, который знает админка.
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/api"))
    {
        var expected = options.ApiKey;
        var actual = context.Request.Headers["X-Api-Key"].ToString();
        var client = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        if (throttle.BlockedFor(client) is { } wait)
        {
            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            context.Response.Headers.RetryAfter = ((int)Math.Ceiling(wait.TotalSeconds)).ToString();
            await context.Response.WriteAsJsonAsync(new { error = "Слишком много неверных ключей API: повторите позже." });
            return;
        }
        if (string.IsNullOrEmpty(expected) || !ApiSecurity.KeyEquals(actual, expected))
        {
            if (!string.IsNullOrEmpty(expected))
                throttle.Failure(client);
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { error = string.IsNullOrEmpty(expected) ? "В сервисе миграции не задан ключ API (Migration__ApiKey)." : "Неверный ключ API." });
            return;
        }
    }
    await next();
});

static string UserOf(HttpContext context) => ApiSecurity.CleanUser(context.Request.Headers["X-User"].ToString());

var api = app.MapGroup("/api");

api.MapGet("/status", (MigrationEngine engine, ConfigStore config) => Results.Ok(new
{
    version = typeof(Program).Assembly.GetName().Version?.ToString(3),
    active = engine.Active?.Snapshot(),
    environments = config.Current.Environments.Select(e => new { e.Key, e.Title, configured = e.IsConfigured, e.PilotUrl }),
}));

api.MapGet("/config", (ConfigStore config) => Results.Ok(config.Current.Masked()));

api.MapPut("/config", (MigrationConfig body, ConfigStore config, HttpContext context) =>
{
    config.Save(body, UserOf(context));
    return Results.Ok(config.Current.Masked());
});

// Проверка подключений каждого контура: Pilot, PostgreSQL, settings.xml, архивы.
api.MapPost("/config/check", async (ConfigStore store, PilotAdmin pilot, ServiceOptions opts, CancellationToken ct) =>
{
    var results = new List<object>();
    foreach (var env in store.Current.Environments.Where(e => e.IsConfigured))
    {
        void Add(string level, string text) => results.Add(new { env = env.Key, title = env.Title, level, text });
        AdminDatabaseInfo db;
        try
        {
            db = await pilot.GetDatabaseAsync(env, ct);
            Add("ok", $"Pilot: база «{db.Name}» - {PilotAdmin.StateTitle(db.State)}, версия {db.Version}.");
            if (env.HasWeakAdminPassword)
                Add("warn", "Пароль администратора Pilot слабый (короче 8 символов или равен логину): смените его на сервере и в подключениях.");
        }
        catch (MigrationException ex)
        {
            Add("error", ex.Message);
            continue;
        }

        var cluster = SettingsSource.IsCluster(db);
        if (cluster)
            Add("ok", $"Кластер Pilot-Server (задан Redis): настройки сервера читаются из базы конфигурации {opts.ConfigurationDatabase}, а не из settings.xml.");
        ServerDatabaseSettings? settings = null;
        if (cluster || !string.IsNullOrEmpty(env.SettingsXmlPath))
        {
            settings = await SettingsSource.ReadAsync(env, db, opts, true, true,
                (level, text) => Add(level switch { CheckLevel.Blocker => "error", CheckLevel.Warning => "warn", _ => "ok" }, text), ct);
            if (settings is not null)
                Add("ok", $"{(cluster ? "Конфигурация кластера" : "settings.xml")}: {settings.Archives.Count} файловых архивов.");
        }
        else
        {
            Add("warn", "Путь к settings.xml не задан: S3-архивы и пароль PostgreSQL из него не прочитать.");
        }

        try
        {
            var cs = PgConnections.Build(env, settings?.PgConnectionString ?? db.PostgresConnectionString, opts.PgPassword(env.Key), cluster);
            await using var connection = await PgConnections.OpenAsync(cs, env.Title, ct);
            var schema = await PilotPgSchema.LoadAsync(connection, ct);
            var objects = await TableCopier.CountAsync(connection, schema.Q("objects"), ct);
            Add("ok", $"PostgreSQL {connection.Host}:{connection.Port}/{connection.Database}: схема {schema.Schema}, {objects} объектов.");
        }
        catch (MigrationException ex)
        {
            Add("error", ex.Message);
        }

        var archiveSettings = settings ?? new ServerDatabaseSettings(db.Name, db.Id, null,
            db.FileArchives.Select(a => new ServerArchiveSettings(a.Id, a.FileArchiveType == FileArchiveTypeEnum.S3, a.IsWritable, a.Folder ?? "", null, null, null)).ToList());
        foreach (var archive in ArchiveResolver.Resolve(env, archiveSettings, Path.Combine(opts.DataDirectory, "tmp")))
        {
            using (archive)
            {
                if (archive.Problem is not null)
                {
                    Add("warn", $"Архив {archive.Title}: {archive.Problem}.");
                    continue;
                }
                var problem = await archive.Store!.CheckReadableAsync(ct);
                Add(problem is null ? "ok" : "error", $"Архив {archive.Title}: {(problem is null ? $"доступен ({archive.Store.Describe})" : problem)}.");
            }
        }
    }
    return Results.Ok(results);
});

// Присланные файлы баз: что лежит в каталоге импорта и чем каждый файл оказался.
api.MapGet("/import/sources", (ServiceOptions opts) => Results.Ok(new
{
    directory = opts.ImportDirectory,
    files = ImportFiles.List(opts.ImportDirectory),
}));

// Готовые выгрузки базы в файлы.
api.MapGet("/export/list", (ServiceOptions opts) => Results.Ok(new
{
    directory = opts.ExportDirectory,
    exports = ExportFiles.List(opts.ExportDirectory),
}));

api.MapPost("/plan", async (MigrationRequest request, MigrationEngine engine, CancellationToken ct) =>
    Results.Ok(await engine.PlanAsync(request, ct)));

api.MapPost("/runs", (StartRunRequest body, MigrationEngine engine, HttpContext context) =>
{
    var run = engine.TryStart(body.Request, UserOf(context), "вручную", null);
    return run is null
        ? Results.Conflict(new { error = "Уже идёт другой перенос - дождитесь его завершения.", active = engine.Active?.Id })
        : Results.Accepted($"/api/runs/{run.Id}", run.Snapshot());
});

api.MapGet("/runs", (RunStore runs, int? limit) =>
    Results.Ok(runs.List(Math.Clamp(limit ?? 50, 1, 500)).Select(r =>
    {
        r.Log = r.Log.TakeLast(1).ToList();
        return r;
    })));

api.MapGet("/runs/{id:guid}", (Guid id, RunStore runs) =>
    runs.Get(id)?.Snapshot() is { } run ? Results.Ok(run) : Results.NotFound());

api.MapPost("/runs/{id:guid}/cancel", (Guid id, MigrationEngine engine) =>
    engine.Cancel(id) ? Results.Ok(new { canceled = true }) : Results.NotFound(new { error = "Перенос не выполняется." }));

api.MapGet("/schedules", (ScheduleStore schedules) => Results.Ok(schedules.List().Select(ScheduleView)));

api.MapGet("/schedules/{id:guid}", (Guid id, ScheduleStore schedules) =>
    schedules.Get(id) is { } s ? Results.Ok(ScheduleView(s)) : Results.NotFound());

api.MapPost("/schedules", (ScheduleEntry entry, ScheduleStore schedules, HttpContext context) =>
{
    if (entry.Id == Guid.Empty)
        entry.Id = Guid.NewGuid();
    if (schedules.Get(entry.Id) is null)
        entry.CreatedBy = UserOf(context);
    entry.Request.Normalize();
    if (entry.Validate() is { } error)
        return Results.BadRequest(new { error });
    return Results.Ok(ScheduleView(schedules.Upsert(entry)));
});

api.MapDelete("/schedules/{id:guid}", (Guid id, ScheduleStore schedules) =>
    schedules.Delete(id) ? Results.Ok() : Results.NotFound());

app.Run();

static object ScheduleView(ScheduleEntry s) => new
{
    s.Id, s.Name, s.Enabled, s.Request, s.Kind, s.IntervalMinutes, s.TimeOfDay, s.Days, s.CreatedBy, s.UpdatedAt,
    s.LastRunAt, s.LastRunId, s.LastResult, s.NextRunAt,
    Description = s.Describe(),
    RequestDescription = s.Request.Describe(),
};

public sealed record StartRunRequest(MigrationRequest Request);

public partial class Program;
