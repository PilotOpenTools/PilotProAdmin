using Ascon.Pilot.DataClasses;
using Npgsql;
using PilotMigration.Archives;
using PilotMigration.Config;
using PilotMigration.Database;
using PilotMigration.Objects;
using PilotMigration.Pilot;

namespace PilotMigration.Engine;

/// <summary>Всё, что известно об источнике и приёмнике перед переносом. Собирается одинаково для проверки и для запуска.</summary>
public sealed class MigrationContext : IDisposable
{
    public required MigrationRequest Request { get; init; }
    public required MigrationConfig Config { get; init; }
    public required EnvConfig SourceEnv { get; init; }
    public required EnvConfig TargetEnv { get; init; }
    public required ServiceOptions Options { get; init; }
    public AdminDatabaseInfo? SourceDb { get; set; }
    public AdminDatabaseInfo? TargetDb { get; set; }
    public ServerDatabaseSettings? SourceSettings { get; set; }
    public ServerDatabaseSettings? TargetSettings { get; set; }
    public string? SourcePg { get; set; }
    public string? TargetPg { get; set; }
    public PilotPgSchema? SourceSchema { get; set; }
    public PilotPgSchema? TargetSchema { get; set; }
    public ArchiveTopology? Archives { get; set; }
    public DMetadata? SourceMetadata { get; set; }
    public DMetadata? TargetMetadata { get; set; }

    /// <summary>Присланная файловая база - при импорте вместо источника-контура.</summary>
    public DbpFileSet? Dbp { get; set; }

    /// <summary>Папка присланной базы, как её видит сервис.</summary>
    public string? DbpFolder { get; set; }

    /// <summary>Идентификатор присланной базы (таблица databaseid во всех её файлах).</summary>
    public Guid DbpDatabaseId { get; set; }

    /// <summary>Каталог выгрузок, как его видит сервис, - при выгрузке в файлы.</summary>
    public string? ExportDirectory { get; set; }

    public string TargetDatabaseName => PilotAdmin.ParseUrl(TargetEnv.PilotUrl).Database;

    public bool SourceIsCluster => SourceDb is not null && SettingsSource.IsCluster(SourceDb);
    public bool TargetIsCluster => TargetDb is not null && SettingsSource.IsCluster(TargetDb);

    /// <summary>Нужны ли файловые архивы: полная копия или выборочный перенос объектов с файлами.</summary>
    public bool CopiesFiles => Request.IsFileImport
        ? Request.FileSource.FileArchive
        : Request.Mode == MigrationMode.Full || (Request.Options.HasObjects && Request.Options.FileArchive);

    public bool TouchesArchives => Request.IsFileImport || Request.Mode == MigrationMode.Full || Request.Options.HasObjects;

    public PilotPgSchema Schema => TargetSchema!;

    public ObjectCategory? SourceCategory(int typeId) => ObjectCategories.ByTypeName(TypeName(SourceMetadata, typeId));
    public ObjectCategory? TargetCategory(int typeId) => ObjectCategories.ByTypeName(TypeName(TargetMetadata, typeId));

    public static string? TypeName(DMetadata? metadata, int typeId) => metadata?.Types.FirstOrDefault(t => t.Id == typeId)?.Name;

    public void Dispose()
    {
        foreach (var a in (Archives?.Source ?? Array.Empty<ResolvedArchive>()).Concat(Archives?.Target ?? Array.Empty<ResolvedArchive>()))
            a.Dispose();
        Dbp?.Dispose();
    }

    /// <summary>Разрешённые направления: из прода - в тест и стейдж, между тестом и стейджем. В прод - никогда.</summary>
    public static string? DirectionError(string source, string target)
    {
        if (source == target)
            return "Источник и приёмник совпадают.";
        if (target == MigrationConfig.Prod)
            return "Перенос в прод запрещён: прод может быть только источником.";
        var known = new[] { MigrationConfig.Test, MigrationConfig.Stage, MigrationConfig.Prod };
        if (!known.Contains(source) || !known.Contains(target))
            return $"Неизвестный контур: {source} / {target}.";
        return null;
    }

    /// <summary>
    /// Запрет записи в прод держится на метке контура. Метка «test» или «stage» с адресом, settings.xml или папкой архива
    /// прода обошла бы запрет, поэтому приёмник не должен ссылаться на то же, что настроено для прода.
    /// </summary>
    public static string? ProdAliasError(MigrationConfig config, EnvConfig target)
    {
        var prod = config.Find(MigrationConfig.Prod);
        if (prod is null || target.Key == MigrationConfig.Prod)
            return null;
        string? what = null;
        if (prod.IsConfigured && SameEndpoint(prod.PilotUrl, target.PilotUrl))
            what = "адрес сервера Pilot";
        else if (prod.SettingsXmlPath.Length > 0 && target.SettingsXmlPath.Length > 0 && SamePath(prod.SettingsXmlPath, target.SettingsXmlPath))
            what = "путь к settings.xml";
        else if (target.PathMappings.Any(m => prod.PathMappings.Any(p => m.LocalPath.Length > 0 && SamePath(p.LocalPath, m.LocalPath))))
            what = "папка файлового архива";
        else if (target.S3.Any(m => prod.S3.Any(p => SameEndpoint(p.ServerUrl, m.ServerUrl) || SameEndpoint(p.EffectiveUrl, m.EffectiveUrl))))
            what = "адрес S3-хранилища";
        return what is null
            ? null
            : $"В подключениях контура «{target.Title}» указан тот же {what}, что у прода. Запись в прод запрещена: исправьте подключения (Миграция / Подключения).";
    }

    /// <summary>
    /// Приёмник не должен быть прод-базой и без settings.xml: у него тот же идентификатор базы или та же база PostgreSQL
    /// (хост:порт/база из админского API), что у прода. Метка контура и подключения здесь ничего не гарантируют.
    /// </summary>
    internal static string? ProdIdentityError(AdminDatabaseInfo prod, AdminDatabaseInfo target, string targetTitle)
    {
        if (prod.Id != Guid.Empty && prod.Id == target.Id)
            return $"Контур «{targetTitle}» указывает на ту же базу, что и прод: тот же идентификатор базы {target.Id} " +
                   "(например, приёмник восстановлен из копии прода). Запись в прод запрещена: используйте другую базу приёмника.";
        // Не разобрать адрес PostgreSQL хотя бы у одной стороны - сравнить нечем, и приёмник нельзя считать не продом.
        if (PgConnections.Endpoint(prod.PostgresConnectionString) is not { } p || PgConnections.Endpoint(target.PostgresConnectionString) is not { } t)
            return $"Нельзя сравнить базу PostgreSQL контура «{targetTitle}» с продом: сервер Pilot не сообщил её адрес. Перенос не начат.";
        return PgConnections.SameDatabase(prod.PostgresConnectionString, target.PostgresConnectionString)
            ? $"Контур «{targetTitle}» указывает на ту же базу, что и прод: та же база PostgreSQL {PgConnections.Describe(t)}. " +
              "Запись в прод запрещена: исправьте адрес Pilot в подключениях (Миграция / Подключения)."
            : null;
    }

    /// <summary>
    /// Сведения о базе прода запрашиваются у сервера (прод-источник уже известен). Недоступен прод - проверить нечем,
    /// и перенос не начинается: иначе приёмник, указывающий на прод, прошёл бы проверку.
    /// </summary>
    internal static async Task CheckNotProdAsync(MigrationContext ctx, MigrationConfig config, Func<EnvConfig, CancellationToken, Task<AdminDatabaseInfo>> getDatabase, CheckList checks, CancellationToken ct)
    {
        var prodEnv = config.Find(MigrationConfig.Prod);
        if (prodEnv is null || !prodEnv.IsConfigured || ctx.TargetEnv.Key == MigrationConfig.Prod)
            return;
        AdminDatabaseInfo prodDb;
        if (ctx.SourceEnv.Key == MigrationConfig.Prod && ctx.SourceDb is not null)
        {
            prodDb = ctx.SourceDb;
        }
        else
        {
            try
            {
                prodDb = await getDatabase(prodEnv, ct);
            }
            catch (MigrationException ex)
            {
                checks.Block($"Сведения о базе прода не получены ({ex.Message}). Нельзя проверить, что «{ctx.TargetEnv.Title}» - не прод, поэтому перенос не начат: " +
                             "восстановите связь с сервером прода и повторите.");
                return;
            }
        }
        if (ProdIdentityError(prodDb, ctx.TargetDb!, ctx.TargetEnv.Title) is { } error)
            checks.Block(error);
    }

    // shortcut: сравнение синтаксическое; другое имя хоста или IP того же сервера не распознаётся (DNS не разрешаем), бакеты S3 не сравниваются.
    private static bool SamePath(string a, string b)
    {
        static string Norm(string p) => Path.GetFullPath(p.Trim().Replace('\\', '/')).Replace('\\', '/').TrimEnd('/');
        return string.Equals(Norm(a), Norm(b), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Адреса совпадают по хосту, порту (с учётом порта по умолчанию схемы) и пути; схема и регистр не важны.</summary>
    internal static bool SameEndpoint(string? a, string? b)
    {
        if (Uri.TryCreate((a ?? "").Trim(), UriKind.Absolute, out var x) && Uri.TryCreate((b ?? "").Trim(), UriKind.Absolute, out var y))
            return string.Equals(x.Host, y.Host, StringComparison.OrdinalIgnoreCase) && x.Port == y.Port &&
                   string.Equals(x.AbsolutePath.TrimEnd('/'), y.AbsolutePath.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
        return S3Endpoint.SameServerUrl(a, b);
    }

    /// <summary>Куда можно импортировать файловую базу: в тест и стейдж, в прод - никогда.</summary>
    public static string? ImportTargetError(string target)
    {
        if (target == MigrationConfig.Prod)
            return "Импорт в прод запрещён: прод может быть только источником.";
        if (target != MigrationConfig.Test && target != MigrationConfig.Stage)
            return $"Неизвестный контур приёмника: {target}.";
        return null;
    }

    /// <summary>
    /// Клиент Pilot хранит локальный кеш по идентификатору базы (Databases\&lt;id&gt;). Если у двух контуров он один,
    /// кеши смешиваются: «отменены изменения из-за конфликта с серверной версией», клиент зависает.
    /// Полная копия сохраняет идентификатор приёмника, поэтому совпадение надо устранить до переноса.
    /// </summary>
    public static string? SameDatabaseIdError(string sourceTitle, string targetTitle, Guid sourceId, Guid targetId)
    {
        if (sourceId == Guid.Empty || sourceId != targetId)
            return null;
        return $"У баз «{sourceTitle}» и «{targetTitle}» один идентификатор {sourceId}. Клиенты Pilot будут использовать для них общий локальный кеш " +
               "(конфликты изменений, зависание клиента). Выдайте приёмнику новый идентификатор: остановите pilot-server приёмника, " +
               "замените GUID в таблице databaseid и в атрибуте Id элемента DatabaseParameters в settings.xml, запустите сервер.";
    }

    public static async Task<MigrationContext> PrepareAsync(
        MigrationRequest request, MigrationConfig config, PilotAdmin pilot, CheckList checks, Action<string> log,
        ServiceOptions options, string tempDirectory, string importDirectory, string exportDirectory, CancellationToken ct)
    {
        request.Normalize();
        var sourceEnv = request.IsFileImport
            ? new EnvConfig { Key = MigrationRequest.FileSourceKey, Title = FileSourceTitle(request) }
            : config.Find(request.Source) ?? new EnvConfig { Key = request.Source, Title = request.Source };
        var targetEnv = request.IsFileExport
            ? new EnvConfig { Key = MigrationRequest.FileTargetKey, Title = $"выгрузка «{request.FileTarget.Name}»" }
            : config.Find(request.Target) ?? new EnvConfig { Key = request.Target, Title = request.Target };
        var ctx = new MigrationContext
        {
            Request = request, Config = config, SourceEnv = sourceEnv, TargetEnv = targetEnv, ExportDirectory = exportDirectory, Options = options,
        };

        if (request.IsFileExport)
        {
            if (request.IsFileImport)
            {
                checks.Block("Источник и приёмник - файловые базы: выгружать можно только базу контура.");
                return ctx;
            }
            return await PrepareFileExportAsync(ctx, pilot, checks, log, tempDirectory, ct);
        }

        if (request.IsFileImport)
        {
            if (ImportTargetError(request.Target) is { } importError)
            {
                checks.Block(importError);
                return ctx;
            }
            if (request.CopyServerSettings)
            {
                checks.Block("Настройки сервера переносятся только между контурами: у присланной файловой базы нет settings.xml. " +
                             "Снимите отметку «Настройки сервера».");
                return ctx;
            }
            if (!OpenFileSource(ctx, checks, log, importDirectory))
                return ctx;
        }
        else if (DirectionError(request.Source, request.Target) is { } directionError)
        {
            checks.Block(directionError);
            return ctx;
        }
        if (request.Mode == MigrationMode.Selective && request.Options.IsEmpty && !request.CopyServerSettings)
        {
            checks.Block("Не выбрано, что переносить.");
            return ctx;
        }
        if (request.Mode == MigrationMode.Selective && request.Options.Tree && !request.Options.TreeAll)
        {
            var bad = request.Options.TreeIds.Where(s => !Guid.TryParse(s, out _)).ToList();
            if (bad.Count > 0)
                checks.Block("Не Id объекта (нужен GUID): " + string.Join(", ", bad.Take(10)));
            if (request.Options.TreeIds.Count == 0)
                checks.Block("Для переноса выбранных объектов укажите их Id.");
        }
        foreach (var env in request.IsFileImport ? new[] { targetEnv } : new[] { sourceEnv, targetEnv })
        {
            if (!env.IsConfigured)
                checks.Block($"Контур «{env.Title}» не настроен в сервисе миграции (Миграция / Подключения).");
        }
        if (ProdAliasError(config, targetEnv) is { } aliasError)
            checks.Block(aliasError);
        if (checks.HasBlockers)
            return ctx;

        // Pilot-Server: базы, состояние, версия.
        log("Сервер Pilot: сведения о базах");
        try
        {
            if (!request.IsFileImport)
                ctx.SourceDb = await pilot.GetDatabaseAsync(sourceEnv, ct);
            ctx.TargetDb = await pilot.GetDatabaseAsync(targetEnv, ct);
        }
        catch (MigrationException ex)
        {
            checks.Block(ex.Message);
            return ctx;
        }
        await CheckNotProdAsync(ctx, config, pilot.GetDatabaseAsync, checks, ct);

        var databases = new List<(EnvConfig Env, AdminDatabaseInfo Db)> { (targetEnv, ctx.TargetDb!) };
        if (!request.IsFileImport)
            databases.Insert(0, (sourceEnv, ctx.SourceDb!));
        foreach (var (env, db) in databases)
        {
            if (!string.IsNullOrEmpty(db.DatabaseLocation))
                checks.Block($"База «{db.Name}» ({env.Title}) хранится в файле {db.DatabaseLocation}, а не в PostgreSQL - миграция работает только с базами PostgreSQL.");
            if (db.State == DatabaseState.Broken)
                checks.Block($"База «{db.Name}» ({env.Title}) в состоянии «повреждена»: {db.StateReason ?? db.InitializingError}.");
        }
        if (request.IsFileImport)
        {
            // Версии сравнить нечем: в файловой базе нет её номера. Вместо этого - сверка структуры (ниже).
            if (SameDatabaseIdError("присланная файловая база", targetEnv.Title, ctx.DbpDatabaseId, ctx.TargetDb!.Id) is { } sameFileId)
                checks.Block(sameFileId);
            else
                checks.Note($"Идентификатор присланной базы {ctx.DbpDatabaseId}; у приёмника останется свой ({ctx.TargetDb.Id}), " +
                            $"клиенты «{targetEnv.Title}» увидят её как прежнюю базу «{ctx.TargetDb.Name}».");
        }
        else
        {
            if (ctx.SourceDb!.State != DatabaseState.Launched)
                checks.Block($"База источника «{ctx.SourceDb.Name}» не запущена ({PilotAdmin.StateTitle(ctx.SourceDb.State)}).");
            if (ctx.SourceDb.Version != ctx.TargetDb!.Version)
                checks.Block($"Версии баз различаются: {sourceEnv.Title} {ctx.SourceDb.Version}, {targetEnv.Title} {ctx.TargetDb.Version}. Обновите сервер Pilot приёмника до той же версии.");
            if (SameDatabaseIdError(sourceEnv.Title, targetEnv.Title, ctx.SourceDb.Id, ctx.TargetDb.Id) is { } sameIdError)
                checks.Block(sameIdError);
            else if (request.Mode == MigrationMode.Full && ctx.SourceDb.Id != ctx.TargetDb.Id)
                checks.Note($"Идентификаторы баз различаются ({ctx.SourceDb.Id} и {ctx.TargetDb.Id}) - у приёмника останется свой идентификатор, клиенты увидят её как прежнюю базу «{ctx.TargetDb.Name}».");
        }
        if (checks.HasBlockers)
            return ctx;

        // Настройки сервера (settings.xml, в кластере - база конфигурации): архивы, строка PostgreSQL, параметры поведения.
        if (!request.IsFileImport)
            ctx.SourceSettings = await SettingsSource.ReadAsync(sourceEnv, ctx.SourceDb!, options, ctx.TouchesArchives, request.CopyServerSettings, checks.Add, ct);
        ctx.TargetSettings = await SettingsSource.ReadAsync(targetEnv, ctx.TargetDb!, options, ctx.TouchesArchives, false, checks.Add, ct);
        if (request.CopyServerSettings)
            CheckServerSettingsCopy(ctx, checks);
        if (checks.HasBlockers)
            return ctx;

        if (request.IsFileImport)
            return await PrepareFileImportAsync(ctx, checks, log, tempDirectory, importDirectory, ct);

        // PostgreSQL: подключение и одинаковая структура.
        if (request.Mode == MigrationMode.Full || request.Options.NeedsDatabase)
        {
            log("PostgreSQL: подключение и структура таблиц");
            try
            {
                ctx.SourcePg = PgFor(ctx, sourceEnv, ctx.SourceSettings, ctx.SourceDb!);
                ctx.TargetPg = PgFor(ctx, targetEnv, ctx.TargetSettings, ctx.TargetDb);
                if (PgConnections.SameDatabase(ctx.SourcePg, ctx.TargetPg))
                {
                    checks.Block("Источник и приёмник указывают на одну и ту же базу PostgreSQL.");
                    return ctx;
                }
                await using (var s = await PgConnections.OpenAsync(ctx.SourcePg, sourceEnv.Title, ct))
                await using (var t = await PgConnections.OpenAsync(ctx.TargetPg, targetEnv.Title, ct))
                {
                    ctx.SourceSchema = await PilotPgSchema.LoadAsync(s, ct);
                    ctx.TargetSchema = await PilotPgSchema.LoadAsync(t, ct);
                    await CheckSeparateClustersAsync(s, t, sourceEnv.Title, targetEnv.Title, checks, ct);
                }
                var diff = ctx.SourceSchema.DifferencesFrom(ctx.TargetSchema).Take(8).ToList();
                if (diff.Count > 0)
                    checks.Block("Структура баз различается (разные версии Pilot-Server?): " + string.Join("; ", diff) + ".");
                foreach (var required in new[] { "objects", "people", "organisationunits", "famediator", "faslist" })
                {
                    if (!ctx.TargetSchema.Has(required))
                        checks.Block($"В схеме {ctx.TargetSchema.Schema} нет таблицы {required} - эта версия Pilot-Server не поддерживается.");
                }
            }
            catch (MigrationException ex)
            {
                checks.Block(ex.Message);
            }
            catch (NpgsqlException ex)
            {
                checks.Block("PostgreSQL: " + ex.Message);
            }
        }

        // Метаданные: типы для выборочного переноса, автоматизации.
        if (request.Mode == MigrationMode.Selective && (request.Options.HasObjects || request.Options.Automations))
        {
            try
            {
                ctx.SourceMetadata = await pilot.GetMetadataAsync(sourceEnv, ct);
                ctx.TargetMetadata = await pilot.GetMetadataAsync(targetEnv, ct);
            }
            catch (MigrationException ex)
            {
                checks.Block(ex.Message);
            }
        }

        // Файловые архивы: общие хранилища, доступ.
        if (ctx.TouchesArchives && ctx.SourceSettings is not null && ctx.TargetSettings is not null)
        {
            log("Файловые архивы: доступ и общие хранилища");
            var source = ArchiveResolver.Resolve(sourceEnv, ctx.SourceSettings, tempDirectory);
            var target = ArchiveResolver.Resolve(targetEnv, ctx.TargetSettings, tempDirectory);
            ctx.Archives = new ArchiveTopology(source, target);
            await ctx.Archives.DetectSharingAsync(ct);
            ctx.Archives.Evaluate(checks, ctx.CopiesFiles, sourceEnv.Title, targetEnv.Title);
            if (request.Mode == MigrationMode.Selective && request.Options.HasObjects && !request.Options.FileArchive &&
                ctx.Archives.Target.Any(t => !ctx.Archives.IsSharedWithSource(t)))
            {
                checks.Warn("Файловый архив не отмечен: объекты перенесутся без тел файлов. Файлы, которых нет в архиве приёмника, не откроются.");
            }
        }

        if (request.Mode == MigrationMode.Full || request.Options.Automations)
            checks.Warn($"Автоматизации «{targetEnv.Title}» будут заменены автоматизациями «{sourceEnv.Title}». Если «{targetEnv.Title}» связан с веткой GitLab, сервер разойдётся с веткой - это будет видно в админке («Сравнение»).");

        return ctx;
    }

    private static string FileSourceTitle(MigrationRequest request) =>
        request.FileSource.MainFile.Length > 0
            ? $"файловая база «{request.FileSource.MainFile}»"
            : "присланная файловая база";

    /// <summary>Открывает выбранные файлы и проверяет, что все они сняты с одной базы и подходят по роли.</summary>
    private static bool OpenFileSource(MigrationContext ctx, CheckList checks, Action<string> log, string importDirectory)
    {
        var options = ctx.Request.FileSource;
        try
        {
            ctx.Dbp = DbpFileSet.Open(importDirectory, options);
            ctx.DbpFolder = Path.GetDirectoryName(ctx.Dbp.File(DbpFile.Main).Path);
            foreach (var role in ctx.Dbp.Files)
                log($"{DbpFileSet.Title(role)}: {ctx.Dbp.File(role).Path}");

            // Файл выбирают вручную, поэтому роль проверяется по набору таблиц, а не по имени.
            foreach (var role in ctx.Dbp.Files)
            {
                var file = ctx.Dbp.File(role);
                var expected = DbpFileSet.Expected(role);
                if (file.Kind == expected)
                    continue;
                checks.Block($"Файл {file.Name} выбран как «{DbpFileSet.Title(role)}», но по набору таблиц это " +
                             $"{KindTitle(file.Kind)}. Выберите подходящий файл.");
                return false;
            }

            ctx.DbpDatabaseId = ctx.Dbp.DatabaseId(DbpFile.Main);
            foreach (var role in ctx.Dbp.Files.Where(f => f != DbpFile.Main))
            {
                var id = ctx.Dbp.DatabaseId(role);
                if (id == ctx.DbpDatabaseId)
                    continue;
                checks.Block($"Файл {ctx.Dbp.Name(role)} снят с другой базы: его идентификатор {id}, " +
                             $"а у {ctx.Dbp.Name(DbpFile.Main)} - {ctx.DbpDatabaseId}. Нужен комплект файлов одной базы.");
                return false;
            }

            if (!options.HasChats)
                checks.Warn($"Чаты и сообщения не переносятся: в «{ctx.TargetEnv.Title}» они будут пустыми.");
            if (!options.HasJournal)
                checks.Note("Журнал действий пользователей не переносится (на работу Pilot он не влияет).");
            return true;
        }
        catch (MigrationException ex)
        {
            checks.Block(ex.Message);
            return false;
        }
    }

    private static string KindTitle(DbpFileKind kind) => kind switch
    {
        DbpFileKind.Main => "основная база",
        DbpFileKind.Chats => "чаты",
        DbpFileKind.Journal => "журнал действий",
        _ => "не база Pilot (знакомых таблиц нет)",
    };

    /// <summary>
    /// Подготовка импорта файловой базы: схема приёмника, сверка структуры с присланными файлами
    /// (вместо сравнения версий - в файловой базе её номера нет) и её файловые архивы.
    /// </summary>
    private static async Task<MigrationContext> PrepareFileImportAsync(MigrationContext ctx, CheckList checks,
        Action<string> log, string tempDirectory, string importDirectory, CancellationToken ct)
    {
        var request = ctx.Request;
        var targetEnv = ctx.TargetEnv;

        log("PostgreSQL приёмника: структура таблиц");
        try
        {
            ctx.TargetPg = PgFor(ctx, targetEnv, ctx.TargetSettings, ctx.TargetDb!);
            await using (var t = await PgConnections.OpenAsync(ctx.TargetPg, targetEnv.Title, ct))
                ctx.TargetSchema = await PilotPgSchema.LoadAsync(t, ct);

            var diff = DbpToPgMap.Differences(ctx.TargetSchema, ctx.Dbp!);
            if (diff.Count > 0)
                checks.Block($"Структура присланной базы не совпадает со схемой {ctx.TargetSchema.Schema} приёмника " +
                             $"(разные версии Pilot-Server): {string.Join("; ", diff.Take(8))}" +
                             (diff.Count > 8 ? $" и ещё {diff.Count - 8} расхождений" : "") + ".");
            else
                checks.Note($"Структура присланной базы совпадает со схемой {ctx.TargetSchema.Schema} приёмника " +
                            $"({DbpToPgMap.For(ctx.Dbp!).Count()} таблиц).");
        }
        catch (MigrationException ex)
        {
            checks.Block(ex.Message);
        }
        catch (NpgsqlException ex)
        {
            checks.Block("PostgreSQL: " + ex.Message);
        }
        catch (Microsoft.Data.Sqlite.SqliteException ex)
        {
            checks.Block("Присланная база: " + ex.Message);
        }
        if (checks.HasBlockers)
            return ctx;

        // Выгрузка сервиса миграции: тела файлов лежат в FileArchive рядом с основной базой.
        if (request.FileSource.FileArchive && request.FileSource.Archives.Count == 0 && ctx.DbpFolder is not null)
        {
            var exported = Path.Combine(ctx.DbpFolder, ExportFiles.ArchiveFolder);
            if (Directory.Exists(exported))
            {
                var relative = Path.GetRelativePath(Path.GetFullPath(importDirectory), exported).Replace('\\', '/');
                request.FileSource.Archives.Add(new FileSourceArchive { Folder = relative });
                checks.Note($"Тела файлов берутся из папки «{relative}» рядом с основной базой (выгрузка сервиса миграции).");
            }
        }

        var faslist = ctx.Dbp!.Faslist();
        if (ctx.TargetSettings is not null)
        {
            log("Файловые архивы: доступ и общие хранилища");
            var source = ArchiveResolver.ResolveFileSource(faslist, request.FileSource, importDirectory, tempDirectory);
            var target = ArchiveResolver.Resolve(targetEnv, ctx.TargetSettings, tempDirectory);
            ctx.Archives = new ArchiveTopology(source, target);
            await ctx.Archives.DetectSharingAsync(ct);
            ctx.Archives.Evaluate(checks, ctx.CopiesFiles, ctx.SourceEnv.Title, targetEnv.Title);
            if (!request.FileSource.FileArchive)
                checks.Warn($"Файловый архив не отмечен: записи о файлах перенесутся, тела файлов - нет. " +
                            $"В «{targetEnv.Title}» такие файлы не откроются.");
        }

        var masked = faslist.Where(a => a.Mask.Length > 0).ToList();
        if (masked.Count > 0)
            checks.Warn("У архивов присланной базы задана маска файлов (" +
                        string.Join(", ", masked.Select(a => $"«{a.Name}»: {a.Mask}")) +
                        "); в PostgreSQL такой настройки нет, она будет потеряна.");

        checks.Warn($"Метаданные и автоматизации «{targetEnv.Title}» будут заменены на метаданные присланной базы. " +
                    $"Если «{targetEnv.Title}» связан с веткой GitLab, сервер разойдётся с веткой - это будет видно в админке («Сравнение»).");
        return ctx;
    }

    /// <summary>
    /// Подготовка выгрузки в файлы: база источника, схема (вся она должна быть в карте - иначе часть данных
    /// не попала бы в файлы молча), файловые архивы источника и каталог выгрузок.
    /// </summary>
    private static async Task<MigrationContext> PrepareFileExportAsync(MigrationContext ctx, PilotAdmin pilot, CheckList checks,
        Action<string> log, string tempDirectory, CancellationToken ct)
    {
        var request = ctx.Request;
        var options = request.FileTarget;
        var sourceEnv = ctx.SourceEnv;

        var known = new[] { MigrationConfig.Test, MigrationConfig.Stage, MigrationConfig.Prod };
        if (!known.Contains(request.Source))
            checks.Block($"Неизвестный контур источника: {request.Source}.");
        else if (!sourceEnv.IsConfigured)
            checks.Block($"Контур «{sourceEnv.Title}» не настроен в сервисе миграции (Миграция / Подключения).");
        if (options.NameError() is { } nameError)
            checks.Block(nameError);
        if (string.IsNullOrWhiteSpace(ctx.ExportDirectory) || !Directory.Exists(ctx.ExportDirectory))
            checks.Block($"Каталог выгрузок {(string.IsNullOrWhiteSpace(ctx.ExportDirectory) ? "не задан (Migration__ExportDirectory)" : ctx.ExportDirectory + " не смонтирован")}: " +
                         "задайте EXPORT_ROOT в .env и пересоздайте контейнер pilot-migration.");
        else if (options.NameError() is null && Directory.Exists(Path.Combine(ctx.ExportDirectory, options.Name)))
            checks.Block($"Выгрузка «{options.Name}» уже есть в каталоге выгрузок - укажите другое имя.");
        if (checks.HasBlockers)
            return ctx;

        log("Сервер Pilot: сведения о базе");
        try
        {
            ctx.SourceDb = await pilot.GetDatabaseAsync(sourceEnv, ct);
        }
        catch (MigrationException ex)
        {
            checks.Block(ex.Message);
            return ctx;
        }
        var db = ctx.SourceDb;
        if (!string.IsNullOrEmpty(db.DatabaseLocation))
            checks.Block($"База «{db.Name}» ({sourceEnv.Title}) хранится в файле {db.DatabaseLocation}, а не в PostgreSQL - выгрузка работает только с базами PostgreSQL.");
        if (db.State == DatabaseState.Broken)
            checks.Block($"База «{db.Name}» ({sourceEnv.Title}) в состоянии «повреждена»: {db.StateReason ?? db.InitializingError}.");
        if (checks.HasBlockers)
            return ctx;
        checks.Note($"База «{db.Name}» ({sourceEnv.Title}), версия {db.Version}, идентификатор {db.Id} - он сохранится в выгрузке; " +
                    "при импорте у приёмника остаётся свой идентификатор.");

        ctx.SourceSettings = await SettingsSource.ReadAsync(sourceEnv, db, ctx.Options, options.FileArchive, false, checks.Add, ct);
        if (checks.HasBlockers)
            return ctx;

        log("PostgreSQL: подключение и структура таблиц");
        try
        {
            ctx.SourcePg = PgFor(ctx, sourceEnv, ctx.SourceSettings, db);
            long databaseBytes;
            await using (var s = await PgConnections.OpenAsync(ctx.SourcePg, sourceEnv.Title, ct))
            {
                ctx.SourceSchema = await PilotPgSchema.LoadAsync(s, ct);
                await using var size = new NpgsqlCommand(
                    "select coalesce(sum(pg_total_relation_size(c.oid)), 0)::bigint from pg_class c " +
                    "join pg_namespace n on n.oid = c.relnamespace where n.nspname = @s and c.relkind = 'r'", s);
                size.Parameters.AddWithValue("s", ctx.SourceSchema.Schema);
                databaseBytes = (long)(await size.ExecuteScalarAsync(ct))!;
            }
            var diff = DbpToPgMap.ExportDifferences(ctx.SourceSchema);
            if (diff.Count > 0)
                checks.Block($"Схема {ctx.SourceSchema.Schema} не совпадает с картой выгрузки: {string.Join("; ", diff.Take(8))}" +
                             (diff.Count > 8 ? $" и ещё {diff.Count - 8} расхождений" : "") + ".");
            else
                checks.Note($"Схема {ctx.SourceSchema.Schema}: все таблицы есть в карте выгрузки (таблиц: {DbpToPgMap.Tables.Count}).");

            var free = new DriveInfo(Path.GetFullPath(ctx.ExportDirectory!)).AvailableFreeSpace;
            static string Gb(long bytes) => bytes >= 1L << 30 ? $"{bytes / 1024.0 / 1024 / 1024:N1} ГБ" : $"{bytes / 1024.0 / 1024:N0} МБ";
            if (free < databaseBytes)
                checks.Block($"В каталоге выгрузок свободно {Gb(free)}, а таблицы базы занимают {Gb(databaseBytes)} (без тел файлов).");
            else
                checks.Note($"Таблицы базы в PostgreSQL занимают {Gb(databaseBytes)}, в каталоге выгрузок свободно {Gb(free)}" +
                            (options.FileArchive ? " (тела файлов - сверх этого)." : "."));
        }
        catch (MigrationException ex)
        {
            checks.Block(ex.Message);
        }
        catch (NpgsqlException ex)
        {
            checks.Block("PostgreSQL: " + ex.Message);
        }
        if (checks.HasBlockers)
            return ctx;

        if (ctx.SourceSettings is not null)
        {
            var source = ArchiveResolver.Resolve(sourceEnv, ctx.SourceSettings, tempDirectory);
            ctx.Archives = new ArchiveTopology(source, Array.Empty<ResolvedArchive>());
            if (options.FileArchive)
            {
                log("Файловые архивы источника: доступ");
                foreach (var archive in source)
                {
                    var problem = archive.Problem ?? (archive.Store is null ? "хранилище недоступно" : await archive.Store.CheckReadableAsync(ct));
                    if (problem is null)
                        checks.Note($"Архив {archive.Title}: доступен ({archive.Store!.Describe}).");
                    else
                        checks.Block($"Архив {archive.Title} источника недоступен: {problem}. Без него тела файлов не выгрузить - " +
                                     "поправьте подключение или снимите отметку «Файловый архив».");
                }
            }
        }
        if (!options.FileArchive)
            checks.Warn("Файловый архив не отмечен: в выгрузке будет только индекс файлов, после импорта файлы не откроются.");
        if (!options.Chats)
            checks.Note("Чаты не выгружаются: после импорта они будут пустыми.");

        checks.Warn("В выгрузке все данные базы, в том числе пользователи с хешами паролей. Храните и передавайте её как резервную копию " +
                    $"«{sourceEnv.Title}».");
        return ctx;
    }

    /// <summary>Строка подключения к PostgreSQL контура; пароль - из настроек сервера (settings.xml), а без него - из окружения сервиса.</summary>
    private static string PgFor(MigrationContext ctx, EnvConfig env, ServerDatabaseSettings? settings, AdminDatabaseInfo db) =>
        PgConnections.Build(env, settings?.PgConnectionString ?? db.PostgresConnectionString, ctx.Options.PgPassword(env.Key), SettingsSource.IsCluster(db));

    /// <summary>
    /// «Настройки сервера» нельзя перенести - выясняется до копирования данных, а не после него: каталог приёмника
    /// не смонтирован или у источника нет настроек, из которых брать значения.
    /// </summary>
    internal static void CheckServerSettingsCopy(MigrationContext ctx, CheckList checks)
    {
        var dir = ServerSettingsCopy.DirectoryFor(ctx.TargetEnv, ctx.TargetIsCluster);
        if (dir is null || !Directory.Exists(dir))
            checks.Block($"Настройки сервера: каталог приёмника {dir ?? "(не задан путь к settings.xml)"} не смонтирован в сервис миграции - " +
                         "смонтируйте C:/Ascon-Docker/<контур>/server-settings или снимите отметку «Настройки сервера».");
        if (ctx.SourceSettings?.Behavior is null)
            checks.Block("Настройки сервера источника не прочитаны: " + (ctx.SourceIsCluster
                ? "конфигурация кластера недоступна (причина - выше)."
                : ctx.SourceEnv.SettingsXmlPath.Length == 0
                    ? "не задан путь к settings.xml источника (Миграция / Подключения)."
                    : "settings.xml источника не прочитан (причина - выше)."));
    }

    /// <summary>
    /// Два сервера PostgreSQL на одной папке данных (ошибка монтирования томов) выглядят как разные хосты,
    /// но читают один и тот же pg_control: совпадают идентификатор кластера и последняя контрольная точка.
    /// Запись в такой «приёмник» портит источник - перенос запрещается.
    /// </summary>
    internal static async Task CheckSeparateClustersAsync(NpgsqlConnection source, NpgsqlConnection target,
        string sourceTitle, string targetTitle, CheckList checks, CancellationToken ct)
    {
        const string sql = "select s.system_identifier::text, c.checkpoint_lsn::text, c.redo_lsn::text " +
                           "from pg_control_system() s, pg_control_checkpoint() c";
        try
        {
            var a = await ReadControlAsync(source, sql, ct);
            var b = await ReadControlAsync(target, sql, ct);
            if (a == b)
            {
                checks.Block($"PostgreSQL «{sourceTitle}» и «{targetTitle}» работают на одной и той же папке данных " +
                             $"(общий pg_control: кластер {a.Split('|')[0]}, контрольная точка {a.Split('|')[1]}). " +
                             "Запись в приёмник испортит источник. Проверьте тома контейнеров PostgreSQL: у каждого контура должна быть своя папка данных.");
            }
        }
        catch (PostgresException ex) when (ex.SqlState == "42501")
        {
            checks.Warn("Защита от общей папки данных PostgreSQL НЕ сработала: у пользователя сервиса нет прав на pg_control_*, " +
                        "поэтому не проверено, что у контуров разные кластеры. Выдайте ему роль pg_monitor (GRANT pg_monitor TO <пользователь>) " +
                        "и повторите проверку.");
        }
    }

    private static async Task<string> ReadControlAsync(NpgsqlConnection connection, string sql, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(sql, connection);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        return $"{reader.GetString(0)}|{reader.GetString(1)}|{reader.GetString(2)}";
    }
}
