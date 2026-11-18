using System.Collections.Concurrent;
using Ascon.Pilot.DataClasses;
using PilotMigration.Config;
using PilotMigration.Database;
using PilotMigration.Pilot;

namespace PilotMigration.Engine;

/// <summary>Проверка и запуск переносов. Одновременно выполняется только один перенос.</summary>
public sealed class MigrationEngine
{
    private readonly ConfigStore _config;
    private readonly RunStore _runs;
    private readonly PilotAdmin _pilot;
    private readonly ILogger<MigrationEngine> _logger;
    private readonly ServiceOptions _options;
    private readonly string _tempDirectory;
    private readonly string _importDirectory;
    private readonly string _exportDirectory;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _planGate = new(2, 2);
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _cancellation = new();

    public MigrationEngine(ConfigStore config, RunStore runs, PilotAdmin pilot, ServiceOptions options, ILogger<MigrationEngine> logger)
    {
        _config = config;
        _runs = runs;
        _pilot = pilot;
        _logger = logger;
        _options = options;
        _tempDirectory = Path.Combine(options.DataDirectory, "tmp");
        _importDirectory = options.ImportDirectory;
        _exportDirectory = options.ExportDirectory;
    }

    public MigrationRun? Active { get; private set; }

    /// <summary>
    /// Проверка без изменений данных: что будет сделано и что мешает. Единственная запись - пробный объект
    /// в архив приёмника (определение общего хранилища), он тут же удаляется. Одновременно - не больше двух проверок.
    /// </summary>
    public async Task<MigrationPlan> PlanAsync(MigrationRequest request, CancellationToken ct)
    {
        await _planGate.WaitAsync(ct);
        try
        {
            return await PlanCoreAsync(request, ct);
        }
        finally
        {
            _planGate.Release();
        }
    }

    private async Task<MigrationPlan> PlanCoreAsync(MigrationRequest request, CancellationToken ct)
    {
        var checks = new CheckList();
        var summary = new List<string>();
        string? targetDb = null;
        using var ctx = await MigrationContext.PrepareAsync(request, _config.Current, _pilot, checks, _ => { }, _options, _tempDirectory, _importDirectory, _exportDirectory, ct);
        try
        {
            targetDb = ctx.TargetDb?.Name;
            if (!checks.HasBlockers)
                await SummarizeAsync(ctx, checks, summary, ct);
        }
        catch (MigrationException ex)
        {
            checks.Block(ex.Message);
        }
        return new MigrationPlan { Request = request, Checks = checks.Items.ToList(), Summary = summary, TargetDatabase = targetDb };
    }

    /// <summary>Ставит перенос в работу. null - уже идёт другой перенос.</summary>
    public MigrationRun? TryStart(MigrationRequest request, string requestedBy, string trigger, Guid? scheduleId)
    {
        if (!_gate.Wait(0))
            return null;
        var run = new MigrationRun
        {
            Request = request.Normalize(),
            RequestedBy = requestedBy,
            Trigger = trigger,
            ScheduleId = scheduleId,
            Status = RunStatus.Running,
            StartedAt = DateTimeOffset.Now,
        };
        Active = run;
        _runs.Add(run);
        var cts = new CancellationTokenSource();
        _cancellation[run.Id] = cts;
        _ = Task.Run(async () =>
        {
            try
            {
                await ExecuteAsync(run, cts.Token);
            }
            finally
            {
                _cancellation.TryRemove(run.Id, out _);
                cts.Dispose();
                Active = null;
                _gate.Release();
            }
        });
        return run;
    }

    public bool Cancel(Guid runId)
    {
        if (!_cancellation.TryGetValue(runId, out var cts))
            return false;
        cts.Cancel();
        return true;
    }

    private async Task ExecuteAsync(MigrationRun run, CancellationToken ct)
    {
        var log = new RunLog(run, _runs);
        var config = _config.Current;
        var request = run.Request;
        var stateTimeout = TimeSpan.FromMinutes(config.DatabaseStateTimeoutMinutes);
        MigrationContext? ctx = null;
        var targetStopped = false;
        try
        {
            var targetTitle = request.IsFileExport ? "файлы" : config.Find(request.Target)?.Title ?? request.Target;
            log.Info($"{config.Find(request.Source)?.Title ?? request.Source} / {targetTitle}: {request.Describe()}. Запустил: {run.RequestedBy} ({run.Trigger}).");
            log.Phase("Проверки");
            var checks = new CheckList();
            ctx = await MigrationContext.PrepareAsync(request, config, _pilot, checks, log.Info, _options, _tempDirectory, _importDirectory, _exportDirectory, ct);
            lock (run) run.Checks = checks.Items.ToList();
            foreach (var c in checks.Items)
            {
                if (c.Level == CheckLevel.Blocker) log.Error(c.Text);
                else if (c.Level == CheckLevel.Warning) log.Warn(c.Text);
                else log.Info(c.Text);
            }
            if (checks.HasBlockers)
            {
                Finish(run, log, RunStatus.Blocked, "Перенос не начат: есть блокирующие ошибки проверки. Приёмник не изменён.");
                return;
            }

            if (request.IsFileExport)
            {
                // Источник только читается: базу не останавливаем, индекс не перестраиваем.
                await FileDbExport.ExportAsync(ctx, run.RequestedBy, log, ct);
                Finish(run, log, RunStatus.Succeeded, null);
                return;
            }

            var files = ctx.Archives is not null ? new FileTransfer(ctx.Archives, log) : null;
            var o = request.Options;

            if (request.IsFileImport)
            {
                log.Phase("Файлы до остановки приёмника");
                await FileDbImport.PreSyncFilesAsync(ctx, files!, log, ct);

                log.Phase($"Остановка базы «{ctx.TargetDatabaseName}» ({ctx.TargetEnv.Title})");
                targetStopped = true;
                await _pilot.SetStateAndWaitAsync(ctx.TargetEnv, DatabaseState.Stopped, stateTimeout, log.Info, ct);

                var imported = await FileDbImport.ImportAsync(ctx, files!, log, ct);
                log.Phase("Удаление лишних файлов приёмника");
                await FullCopy.PruneAsync(ctx, files!, imported, log, CancellationToken.None);
            }
            else if (request.Mode == MigrationMode.Full)
            {
                log.Phase("Файлы до остановки приёмника");
                await FullCopy.PreSyncFilesAsync(ctx, files!, log, ct);

                log.Phase($"Остановка базы «{ctx.TargetDatabaseName}» ({ctx.TargetEnv.Title})");
                targetStopped = true;
                await _pilot.SetStateAndWaitAsync(ctx.TargetEnv, DatabaseState.Stopped, stateTimeout, log.Info, ct);

                var referenced = await FullCopy.CopyDatabaseAsync(ctx, files!, log, ct);
                // Пока база остановлена: иначе можно удалить файл, который пользователь загружает прямо сейчас.
                log.Phase("Удаление лишних файлов приёмника");
                await FullCopy.PruneAsync(ctx, files!, referenced, log, CancellationToken.None);
            }
            else if (o.NeedsDatabase)
            {
                log.Phase($"Остановка базы «{ctx.TargetDatabaseName}» ({ctx.TargetEnv.Title})");
                targetStopped = true;
                await _pilot.SetStateAndWaitAsync(ctx.TargetEnv, DatabaseState.Stopped, stateTimeout, log.Info, ct);
                await new SelectiveCopy(ctx, log, files).ExecuteAsync(ct);
            }

            if (targetStopped)
            {
                log.Phase($"Запуск базы «{ctx.TargetDatabaseName}»");
                await _pilot.SetStateAndWaitAsync(ctx.TargetEnv, DatabaseState.Launched, stateTimeout, log.Info, CancellationToken.None);
                targetStopped = false;
                if (ctx.TargetIsCluster)
                    log.Info("Кластер: если на каком-то узле не загрузились серверные расширения, перезапустите этот узел (известный сбой Pilot-Server).");
            }

            if (request.Mode == MigrationMode.Selective && o.Automations)
            {
                log.Phase("Автоматизации");
                var automations = (await _pilot.GetMetadataAsync(ctx.SourceEnv, ct)).AutomationConfig ?? new List<AutomationScript>();
                var count = await _pilot.ReplaceAutomationsAsync(ctx.TargetEnv, automations, ct);
                log.Ok($"Автоматизации приёмника заменены: {count} шт.");
                log.Stat("Автоматизаций", count);
            }

            if (request.Mode == MigrationMode.Full || o.HasObjects)
            {
                try
                {
                    await _pilot.AttrSearchRebuildIndexAsync(ctx.TargetEnv, CancellationToken.None);
                    log.Info("Запущено перестроение индекса поиска по атрибутам.");
                }
                catch (MigrationException ex)
                {
                    log.Warn("Индекс поиска по атрибутам не перестроен: " + ex.Message);
                }
            }

            if (request.CopyServerSettings)
            {
                log.Phase("Настройки сервера");
                var changes = ServerSettingsCopy.Apply(ctx.SourceSettings!.Behavior!, ctx.TargetEnv, ctx.TargetIsCluster, $"миграция ({run.RequestedBy})");
                if (changes.Count == 0)
                {
                    log.Ok("Параметры поведения сервера приёмника уже совпадают с источником.");
                }
                else
                {
                    log.Ok("Настройки сервера приёмника изменены: " + string.Join("; ", changes) + ".");
                    log.Warn(ctx.TargetIsCluster
                        ? $"Они вступят в силу только после перезапуска всех узлов контура «{ctx.TargetEnv.Title}»: остановите все узлы сервера Pilot, затем запустите. " +
                          "Пока не перезапущены все узлы, остановка или запуск базы на ещё не перезапущенном узле может вернуть прежние значения."
                        : $"Они вступят в силу только после перезапуска контейнера {ServerSettingsCopy.ContainerName(ctx.TargetEnv)} " +
                          $"(docker restart {ServerSettingsCopy.ContainerName(ctx.TargetEnv)}).");
                }
                log.Info($"Не копируются (свои у каждого контура): {ServerSettingsCopy.ContourKeysText}.");
            }

            if (files is not null)
            {
                log.Stat("Файлов скопировано", files.Copied);
                log.Stat("МБ скопировано", files.CopiedBytes / 1024 / 1024);
                log.Stat("Файлов в общем архиве (не копировались)", files.Shared);
                if (files.MissingInSource > 0)
                    log.Stat("Файлов нет в архиве источника", files.MissingInSource);
            }
            Finish(run, log, RunStatus.Succeeded, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Finish(run, log, RunStatus.Canceled, run.Request.IsFileExport
                ? "Выгрузка отменена, незавершённая папка удалена. Источник не менялся."
                : "Перенос отменён. Незафиксированные изменения базы отменены.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Перенос {Id}", run.Id);
            var message = ex is MigrationException ? ex.Message : $"{ex.GetType().Name}: {ex.Message}";
            Finish(run, log, RunStatus.Failed, message + (run.Request.IsFileExport
                ? " Незавершённая выгрузка удалена. Источник не менялся."
                : " Изменения базы приёмника отменены (транзакция не зафиксирована), если ошибка случилась до фиксации."));
        }
        finally
        {
            if (targetStopped && ctx is not null)
            {
                // База приёмника не должна остаться остановленной ни при какой ошибке.
                try
                {
                    log.Phase($"Запуск базы «{ctx.TargetDatabaseName}» после ошибки");
                    await _pilot.SetStateAndWaitAsync(ctx.TargetEnv, DatabaseState.Launched, stateTimeout, log.Info, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    log.Error($"Не удалось запустить базу приёмника: {ex.Message}. Запустите её вручную (myAdmin / база / «Запустить»).");
                }
            }
            ctx?.Dispose();
            log.Flush();
        }
    }

    private static void Finish(MigrationRun run, RunLog log, RunStatus status, string? error)
    {
        lock (run)
        {
            run.Status = status;
            run.Error = error;
            run.FinishedAt = DateTimeOffset.Now;
            run.Phase = null;
        }
        if (status == RunStatus.Succeeded)
            log.Ok("Готово.");
        else if (error is not null)
            log.Error(error);
    }

    private static async Task SummarizeAsync(MigrationContext ctx, CheckList checks, List<string> summary, CancellationToken ct)
    {
        if (ctx.Request.IsFileExport)
        {
            summary.AddRange(await FileDbExport.SummarizeAsync(ctx, ct));
            return;
        }
        await SummarizeDataAsync(ctx, checks, summary, ct);
        SummarizeServerSettings(ctx, summary);
    }

    /// <summary>Возможность переноса настроек проверена в подготовке (<c>MigrationContext.PrepareAsync</c>): здесь только описание.</summary>
    private static void SummarizeServerSettings(MigrationContext ctx, List<string> summary)
    {
        var t = ctx.TargetEnv.Title;
        if (!ctx.Request.CopyServerSettings)
        {
            summary.Add(ctx.TargetIsCluster
                ? $"Настройки сервера «{t}» (логирование, поведение) не меняются."
                : $"Настройки сервера «{t}» (settings.xml: логирование, поведение) не меняются.");
            return;
        }

        var values = ctx.SourceSettings?.Behavior ?? throw new MigrationException("Настройки сервера источника не прочитаны.");
        var text = string.Join(", ", ServerSettingsCopy.BehaviorKeys
            .Where(k => values.ContainsKey(k.Key))
            .Select(k => $"{k.Title} - {(values[k.Key] ? "вкл" : "выкл")}"));
        var origin = ctx.SourceIsCluster ? "в конфигурации кластера источника" : "в settings.xml источника";
        var effect = ctx.TargetIsCluster
            ? "после перезапуска всех узлов контура: остановите все узлы сервера Pilot, затем запустите"
            : $"после перезапуска контейнера {ServerSettingsCopy.ContainerName(ctx.TargetEnv)}";
        summary.Add($"Настройки сервера «{t}»: параметры поведения как у «{ctx.SourceEnv.Title}» ({(text.Length > 0 ? text : $"{origin} их нет")}). Вступят в силу {effect}.");
        summary.Add($"Не копируются, свои у каждого контура: {ServerSettingsCopy.ContourKeysText}.");
    }

    private static async Task SummarizeDataAsync(MigrationContext ctx, CheckList checks, List<string> summary, CancellationToken ct)
    {
        var request = ctx.Request;
        var s = ctx.SourceEnv.Title;
        var t = ctx.TargetEnv.Title;
        var db = ctx.TargetDb!.Name;

        if (request.IsFileImport)
        {
            summary.AddRange(FileDbImport.Summarize(ctx));
            AddArchiveSummary(ctx, summary);
            return;
        }

        if (request.Mode == MigrationMode.Full)
        {
            await using var source = await PgConnections.OpenAsync(ctx.SourcePg!, s, ct);
            var schema = ctx.TargetSchema!;
            var objects = await TableCopier.CountAsync(source, schema.Q("objects"), ct);
            var people = await TableCopier.CountAsync(source, schema.Q("people"), ct);
            var files = await TableCopier.CountAsync(source, schema.Q("famediator"), ct);
            summary.Add($"База «{db}» ({t}) будет остановлена, все её данные удалены и заменены копией «{s}»: {objects} объектов, {people} пользователей, {files} файлов.");
            summary.Add($"Сохраняются: идентификатор базы приёмника и его файловые архивы ({(ctx.TargetIsCluster ? "конфигурация кластера приёмника" : "settings.xml приёмника")}).");
            AddArchiveSummary(ctx, summary);
            summary.Add($"После копирования база «{db}» запускается, клиенты «{t}» перечитают данные с сервера.");
            return;
        }

        var o = request.Options;
        if (o.NeedsDatabase)
            summary.Add($"База «{db}» ({t}) будет остановлена на время переноса.");
        if (o.Users)
            summary.Add($"Пользователи и оргструктура «{t}» заменяются пользователями «{s}» (Id сохраняются; пользователи приёмника, на которых есть ссылки, остаются).");
        if (o.HasObjects)
        {
            await using var source = await PgConnections.OpenAsync(ctx.SourcePg!, s, ct);
            await using var target = await PgConnections.OpenAsync(ctx.TargetPg!, t, ct);
            var plan = await SelectiveCopy.BuildPlanAsync(ctx, source, null, target, null, ct);
            summary.AddRange(SelectiveCopy.Describe(plan));
            foreach (var c in o.Categories)
                summary.Add($"{Objects.ObjectCategories.Title(c)}: в приёмнике будут ровно как в источнике.");
            var mismatches = SelectiveCopy.TypeMismatches(ctx, plan);
            if (mismatches.Count > 0)
                checks.Block("Типы объектов различаются: " + string.Join("; ", mismatches.Take(10)) + ".");
            if (plan.NotFound.Count > 0)
                checks.Warn($"В источнике нет объектов: {string.Join(", ", plan.NotFound.Take(10))}.");
            foreach (var w in plan.Warnings.Take(5))
                checks.Warn(w);
            if (o.FileArchive)
                AddArchiveSummary(ctx, summary);
        }
        if (o.Automations)
        {
            summary.Add($"Автоматизации «{t}» заменяются: {ctx.SourceMetadata?.AutomationConfig?.Count ?? 0} из «{s}» вместо {ctx.TargetMetadata?.AutomationConfig?.Count ?? 0}.");
        }
    }

    private static void AddArchiveSummary(MigrationContext ctx, List<string> summary)
    {
        if (ctx.Archives is null)
            return;
        foreach (var a in ctx.Archives.Target)
        {
            var shared = ctx.Archives.SharedSourceFor(a);
            summary.Add(shared is not null
                ? $"Архив {a.Title}: общий с источником - файлы не переносятся."
                : $"Архив {a.Title}: недостающие файлы докопируются из источника" +
                  (ctx.Request.Mode == MigrationMode.Full ? ", лишние удалятся." : "."));
        }
    }
}
