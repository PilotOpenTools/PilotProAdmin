using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.ComponentModel.Composition;
using System.ComponentModel.Composition.Hosting;
using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Text.Json;
using System.Runtime.Loader;
using Ascon.Pilot.ServerExtensions.SDK;
using ServerHost.Contract;

namespace ServerHost.Hosting;

/// <summary>
/// Реестр серверных модулей процесса Pilot-Server: хранение, загрузка в выгружаемые контексты, горячая замена
/// и маршрутизация вызовов активностей из слотов.
///
/// Как устроены слоты. Pilot собирает IServerActivity через MEF один раз при старте базы, а движок автоматизаций
/// связывает имя активности из сценария с объектом при пересборке снимка (после изменения метаданных), читая Name
/// у живого объекта. Хост выставляет <see cref="SlotCount"/> слотов; слот получает имя активности модуля и
/// пересылает вызов в текущую загруженную версию. Обновление модуля с теми же именами действует сразу; новое имя
/// занимает свободный слот и начинает работать после ближайшего изменения метаданных базы.
/// Слот, однажды получивший имя, не переименовывается до перезапуска сервера - иначе уже привязанный сценарий
/// вызвал бы чужую активность.
///
/// Кластер. Узлы Pilot-Server делят каталог модулей (<see cref="ModuleStorage.Root"/>): он источник истины, состояние
/// загрузок лежит в staging на диске. Изменяющие операции идут под межузловым замком и после себя обновляют метку
/// поколения; остальные узлы замечают смену метки (при обращении и по таймеру) и сверяют свои загруженные модули
/// с манифестами на диске.
/// </summary>
internal sealed class ModuleHost
{
    public const int SlotCount = 64;
    private const int JournalSize = 200;
    private const long MaxUploadBytes = 200L * 1024 * 1024;
    private static readonly TimeSpan UploadLifetime = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan BackgroundSyncPeriod = TimeSpan.FromSeconds(5);

    private static readonly Lazy<ModuleHost> Shared = new(CreateShared, LazyThreadSafetyMode.ExecutionAndPublication);
    public static ModuleHost Instance => Shared.Value;

    private readonly ModuleStorage _storage;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _slotLock = new();
    private readonly string?[] _slotNames = new string?[SlotCount];
    private readonly Dictionary<string, int> _slotByName = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, LoadedModule> _modules = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ActivityStats> _stats = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<JournalEntry> _journal = new();
    private ImmutableDictionary<string, Route> _routes = ImmutableDictionary<string, Route>.Empty.WithComparers(StringComparer.Ordinal);
    private readonly object _startLock = new();
    private volatile bool _started;

    /// <summary>Метка поколения каталога, с которой узел сверился (или которую записал сам). null - ещё не сверялся.</summary>
    private volatile string? _generation;
    private long _lastSyncCheck;
    private Timer? _syncTimer;

    /// <summary>Не чаще этого интервала обращения (GetState, SlotName, RunAsync) проверяют метку поколения.</summary>
    internal TimeSpan SyncThrottle { get; set; } = TimeSpan.FromMilliseconds(250);

    /// <summary>Сервисы Pilot, которые хост получил через MEF и передаёт модулям (например, IMetricsEvents).</summary>
    private readonly ConcurrentDictionary<Type, object> _hostServices = new();

    private ServerStats? _telemetry;

    internal ModuleHost(ModuleStorage storage)
    {
        _storage = storage;
        StartedAt = DateTime.UtcNow;
    }

    private static ModuleHost CreateShared()
    {
        var host = new ModuleHost(ModuleStorage.OpenDefault());
        // Таймер только у процессного экземпляра: тестовые узлы сверяются явно (SyncNow).
        host._syncTimer = new Timer(_ => host.TrySync(wait: false), null, BackgroundSyncPeriod, BackgroundSyncPeriod);
        try
        {
            host._telemetry = new ServerStats(host._storage.Root, HostVersion(), host.Log);
            host.Log("info", host._telemetry.Enabled ? $"Статистика серверных модулей: {host._telemetry.Url}" : "Статистика серверных модулей выключена");
        }
        catch (Exception ex)
        {
            host.Log("warn", "Статистика серверных модулей не запущена: " + ex.Message);
        }
        return host;
    }

    public DateTime StartedAt { get; }

    public void RegisterHostService<T>(T? service) where T : class
    {
        if (service is not null)
            _hostServices[typeof(T)] = service;
    }

    // ------------------------------------------------------------------ старт и слоты

    /// <summary>Загрузка установленных модулей при первом обращении (сборка слотов Pilot при старте базы).</summary>
    public void EnsureStarted()
    {
        if (_started)
            return;
        lock (_startLock)
        {
            if (_started)
                return;
            _gate.Wait();
            try
            {
                // Замок нужен и старту: пока другой узел переименовывает папки при установке, модуль мог бы не найтись.
                using var clusterLock = _storage.AcquireLock();
                StartCore();
            }
            catch (Exception ex)
            {
                Log("error", "Старт хоста модулей: " + ex.Message);
                _generation = null; // загрузка могла оборваться на середине - пусть таймер доделает сверкой
            }
            finally
            {
                _started = true;
                _gate.Release();
            }
        }
    }

    private void StartCore()
    {
        {
            // Чужие свежие загрузки не трогаем: идущая на другом узле загрузка не должна пропасть из-за нашего старта.
            _storage.CleanupStaging(UploadLifetime);
            _generation = _storage.ReadGeneration();
            foreach (var name in _storage.InstalledNames().OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    var manifest = ModuleStorage.ReadManifest(_storage.CurrentDir(name));
                    if (manifest is null || !manifest.Enabled)
                    {
                        if (manifest is not null)
                            _modules[name] = LoadedModule.Disabled(manifest);
                        continue;
                    }
                    var module = TryLoad(manifest, _storage.CurrentDir(name), out var error);
                    _modules[name] = module ?? LoadedModule.Failed(manifest, error);
                    if (module is not null)
                        _telemetry?.Loaded(name, module.Manifest.Version);
                    Log(module is null ? "error" : "info",
                        module is null ? $"{name} {manifest.Version}: не загружен - {error}" : $"{name} {manifest.Version}: загружен ({ActivityList(module)})");
                }
                catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
                {
                    Log("error", $"{name}: манифест не прочитан - {ex.Message}"); // один испорченный модуль не должен мешать остальным
                }
            }
            // Новые имена при старте не требуют перепривязки: Pilot соберёт снимок автоматизаций после этого.
            RebuildRoutes(out _);
        }
    }

    public string SlotName(int index)
    {
        EnsureStarted();
        SyncIfDue();
        lock (_slotLock)
            return _slotNames[index] ?? $"Host.Slot{index:00}";
    }

    public async Task RunAsync(int slot, Func<IServerActivity, Task> call)
    {
        var name = SlotName(slot);
        if (!_routes.TryGetValue(name, out var route))
            throw new InvalidOperationException($"Серверный модуль с активностью «{name}» отключён или не загружен (ServerHost).");

        var stats = _stats.GetOrAdd(name, _ => new ActivityStats());
        Interlocked.Increment(ref stats.Calls);
        stats.LastCallAt = DateTime.UtcNow;
        var watch = Stopwatch.StartNew();
        try
        {
            await call(route.Activity).ConfigureAwait(false);
            _telemetry?.Call(route.Module, route.Version, name, watch.ElapsedMilliseconds, null);
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref stats.Errors);
            stats.LastError = $"{DateTime.UtcNow:u} {ex.GetType().Name}: {ex.Message}";
            _telemetry?.Call(route.Module, route.Version, name, watch.ElapsedMilliseconds, ex);
            throw;
        }
    }

    /// <summary>API, которые выставляют сами модули (IServerApiFactory внутри модуля).</summary>
    public bool TryCreateModuleApi(string interfaceName, IServerApiServiceContainer container, out object? api)
    {
        foreach (var module in _modules.Values)
            foreach (var factory in module.ApiFactories)
                if (factory.TryCreate(interfaceName, container, out api))
                    return true;
        api = null;
        return false;
    }

    // ------------------------------------------------------------------ операции API

    public HostState GetState()
    {
        EnsureStarted();
        SyncIfDue();
        int used;
        lock (_slotLock)
            used = _slotNames.Count(n => n is not null);
        return new HostState
        {
            HostVersion = HostVersion(),
            StartedAt = StartedAt,
            StorageDir = _storage.Root,
            StoragePersistent = _storage.Persistent,
            SlotsTotal = SlotCount,
            SlotsUsed = used,
            Modules = _modules.Values.OrderBy(m => m.Manifest.Name, StringComparer.OrdinalIgnoreCase).Select(ToState).ToList(),
            NativeExtensions = NativeExtensions(),
            Journal = _journal.Reverse().ToList(),
        };
    }

    public string BeginUpload(string fileName, long size)
    {
        // Под замком: уборка не должна совпасть с распаковкой или установкой чужой загрузки. Замок занят - уберём в следующий раз.
        using (var clusterLock = _storage.TryAcquireLock(TimeSpan.Zero))
            if (clusterLock is not null)
                _storage.CleanupStaging(UploadLifetime);
        if (size <= 0 || size > MaxUploadBytes)
            throw new HostException($"Размер архива {size} байт вне допустимого (1 байт … {MaxUploadBytes / 1024 / 1024} МБ).");
        var id = Guid.NewGuid().ToString("N");
        _storage.CreateUpload(id, new UploadMeta { FileName = Path.GetFileName(fileName), Size = size, CreatedAt = DateTime.UtcNow });
        return id;
    }

    public void AppendChunk(string uploadId, byte[] data)
    {
        var upload = GetUpload(uploadId);
        try
        {
            // FileShare.None: две одновременные дозаписи в один архив не перемешаются.
            using var archive = new FileStream(_storage.ArchivePath(uploadId), FileMode.Append, FileAccess.Write, FileShare.None);
            if (archive.Length + data.Length > upload.Size)
                throw new HostException("Получено больше данных, чем объявлено в beginUpload.");
            archive.Write(data, 0, data.Length);
        }
        catch (DirectoryNotFoundException)
        {
            throw UploadNotFound();
        }
        catch (IOException ex)
        {
            throw new HostException("Не удалось записать фрагмент архива: " + ex.Message);
        }
    }

    public async Task<ValidationReport> ValidateAsync(string uploadId, bool stripServerAssemblies)
    {
        EnsureStarted();
        var upload = GetUpload(uploadId);
        var archivePath = _storage.ArchivePath(uploadId);
        var received = File.Exists(archivePath) ? new FileInfo(archivePath).Length : 0;
        if (received != upload.Size)
            throw new HostException($"Архив загружен не полностью: {received} из {upload.Size} байт.");

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            using var clusterLock = _storage.AcquireLock();
            SyncLocked(); // владельцев активностей проверяем по актуальному набору модулей

            // Прежний отчёт гасим на диске до распаковки: оборвётся она - install не должен поставить недораспакованное по старому отчёту.
            if (upload.Report is not null)
            {
                upload.Report = null;
                _storage.WriteUpload(uploadId, upload);
            }

            // Список доверенных ключей читается при каждой проверке: правка trusted-keys.pem действует сразу и на всех узлах.
            var inspected = ArchiveInspector.Inspect(File.ReadAllBytes(archivePath), upload.FileName, CaptureServer(), stripServerAssemblies,
                trusted: TrustedKeys.Load(_storage.Root));
            var report = inspected.Report;
            report.UploadId = uploadId;
            if (inspected.SignedBy is not null)
                Log("info", $"{upload.FileName}: подпись архива проверена, ключ {ArchiveSignature.Short(inspected.SignedBy)}");
            else if (inspected.SignatureError is not null)
                Log("warn", $"{upload.FileName}: архив отвергнут проверкой подписи - {inspected.SignatureError}");
            upload.Report = report;
            var extracted = _storage.ExtractedDir(uploadId);
            ModuleStorage.DeleteDirectory(extracted);
            if (report.Errors.Count > 0)
            {
                _storage.WriteUpload(uploadId, upload);
                return report;
            }

            if (_modules.TryGetValue(report.ModuleName, out var current))
                report.CurrentVersion = current.Manifest.Version;
            if (NativeExtensions().Any(n => string.Equals(n.Name, report.ModuleName + ".servext", StringComparison.OrdinalIgnoreCase)))
                report.Warnings.Add($"Модуль {report.ModuleName} также загружен Pilot штатно (объект «Расширение Pilot-Server» в «Модулях расширения»). " +
                                    "Удалите его оттуда, иначе его активности будут зарегистрированы дважды.");

            Directory.CreateDirectory(extracted);
            Extract(archivePath, inspected.KeepEntries, extracted);

            // Пробная загрузка в отдельный контекст: MEF-композиция, список активностей, версия файла.
            var probeManifest = new ModuleManifest { Name = report.ModuleName, Version = report.Version, MainFile = MainFileName(report.ModuleName) };
            var probe = TryLoad(probeManifest, extracted, out var error);
            if (probe is null)
            {
                report.Errors.Add("Модуль не загружается: " + error);
                ModuleStorage.DeleteDirectory(extracted);
                _storage.WriteUpload(uploadId, upload);
                return report;
            }
            try
            {
                report.Version = probe.Manifest.Version;
                report.Activities = probe.Activities.Keys.OrderBy(n => n).ToList();
                if (report.Activities.Count == 0 && probe.ApiFactories.Count == 0)
                    report.Errors.Add("В модуле нет классов с [Export(typeof(IServerActivity))] - загружать нечего.");
                foreach (var activity in report.Activities)
                {
                    var owner = _modules.Values.FirstOrDefault(m => !string.Equals(m.Manifest.Name, report.ModuleName, StringComparison.OrdinalIgnoreCase)
                                                                    && m.Activities.ContainsKey(activity));
                    if (owner is not null)
                        report.Errors.Add($"Активность «{activity}» уже есть в модуле {owner.Manifest.Name}.");
                }
                int newNames;
                lock (_slotLock)
                    newNames = report.Activities.Count(a => !_slotByName.ContainsKey(a));
                if (FreeSlots() < newNames)
                    report.Errors.Add($"Нет свободных слотов для {newNames} новых активностей (свободно {FreeSlots()} из {SlotCount}). Перезапустите сервер.");
            }
            finally
            {
                probe.Unload();
            }

            if (report.Errors.Count > 0)
                ModuleStorage.DeleteDirectory(extracted);
            report.CanInstall = report.Errors.Count == 0;
            _storage.WriteUpload(uploadId, upload);
            return report;
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<(ModuleState Module, bool RebindRequired)> InstallAsync(string uploadId, string? actor, string? reason)
    {
        var upload = GetUpload(uploadId);
        if (upload.Report is not { CanInstall: true } report)
            throw NotValidated();

        return MutateAsync(() =>
        {
            var staged = _storage.ExtractedDir(uploadId);
            // Загрузку мог уже установить другой узел, пока мы ждали замок.
            if (!Directory.Exists(staged))
                throw NotValidated();
            var manifest = new ModuleManifest
            {
                Name = report.ModuleName,
                Version = report.Version,
                Enabled = true,
                InstalledAt = DateTime.UtcNow,
                InstalledBy = actor,
                Reason = reason,
                Sha256 = report.Sha256,
                MainFile = MainFileName(report.ModuleName),
                Revision = ModuleManifest.NewRevision(),
                Files = Directory.GetFiles(staged, "*", SearchOption.AllDirectories)
                    .Select(f => Path.GetRelativePath(staged, f).Replace('\\', '/')).OrderBy(f => f).ToList(),
            };
            ModuleStorage.WriteManifest(staged, manifest);
            _storage.Promote(manifest.Name, staged);
            ModuleStorage.DeleteDirectory(_storage.UploadDir(uploadId));

            var result = Activate(manifest.Name, out var rebind);
            Log(result.Loaded ? "info" : "error",
                $"{manifest.Name} {manifest.Version}: установлен{(report.CurrentVersion is { } prev ? $" вместо {prev}" : "")}" +
                $"{(actor is null ? "" : $" ({actor})")}{(string.IsNullOrWhiteSpace(reason) ? "" : $" - {reason}")}" +
                (result.Error is null ? "" : $"; ошибка загрузки: {result.Error}"));
            return (result, rebind);
        });
    }

    public Task<(ModuleState Module, bool RebindRequired)> SetEnabledAsync(string name, bool enabled, string? actor) =>
        MutateAsync(() =>
        {
            var dir = _storage.CurrentDir(name);
            var manifest = ModuleStorage.ReadManifest(dir) ?? throw new HostException($"Модуль {name} не установлен.");
            manifest.Enabled = enabled;
            manifest.Revision = ModuleManifest.NewRevision();
            ModuleStorage.WriteManifest(dir, manifest);
            var result = Activate(manifest.Name, out var rebind);
            Log("info", $"{manifest.Name}: {(enabled ? "включён" : "отключён")}{(actor is null ? "" : $" ({actor})")}");
            return (result, rebind);
        });

    public Task<(ModuleState Module, bool RebindRequired)> ReloadAsync(string name, string? actor) =>
        MutateAsync(() =>
        {
            var dir = _storage.CurrentDir(name);
            var manifest = ModuleStorage.ReadManifest(dir) ?? throw new HostException($"Модуль {name} не установлен.");
            // Новая ревизия - чтобы перезагрузку выполнили и остальные узлы.
            manifest.Revision = ModuleManifest.NewRevision();
            ModuleStorage.WriteManifest(dir, manifest);
            var result = Activate(name, out var rebind);
            Log(result.Error is null ? "info" : "error", $"{name}: перезагружен{(actor is null ? "" : $" ({actor})")}{(result.Error is null ? "" : $"; ошибка: {result.Error}")}");
            return (result, rebind);
        });

    public Task<(ModuleState Module, bool RebindRequired)> RollbackAsync(string name, string? actor) =>
        MutateAsync(() =>
        {
            var previous = ModuleStorage.ReadManifest(_storage.PreviousDir(name))
                           ?? throw new HostException($"У модуля {name} нет предыдущей версии.");
            var current = ModuleStorage.ReadManifest(_storage.CurrentDir(name));
            _storage.SwapWithPrevious(name);
            previous.Enabled = true;
            previous.Revision = ModuleManifest.NewRevision();
            ModuleStorage.WriteManifest(_storage.CurrentDir(name), previous);
            var result = Activate(name, out var rebind);
            Log("info", $"{name}: откат {current?.Version} / {previous.Version}{(actor is null ? "" : $" ({actor})")}");
            return (result, rebind);
        });

    public Task RemoveAsync(string name, string? actor) =>
        MutateAsync(() =>
        {
            if (!Directory.Exists(_storage.ModuleDir(name)))
                throw new HostException($"Модуль {name} не установлен.");
            if (_modules.TryRemove(name, out var old))
            {
                RebuildRoutes(out _);
                old.Unload();
            }
            _storage.MoveToTrash(name);
            Log("info", $"{name}: удалён{(actor is null ? "" : $" ({actor})")}");
            return true;
        });

    /// <summary>
    /// Изменяющая операция: внутрипроцессный _gate, затем межузловой замок; перед операцией узел догоняет каталог,
    /// после неё - записывает новую метку поколения (и запоминает её как свою, чтобы не перезагружать себя).
    /// Без этого догона наша метка затёрла бы чужую, ещё не применённую у нас.
    /// </summary>
    private async Task<T> MutateAsync<T>(Func<T> operation)
    {
        EnsureStarted();
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            using var clusterLock = _storage.AcquireLock();
            SyncLocked();
            T result;
            try
            {
                result = operation();
            }
            catch
            {
                // Операция могла успеть изменить диск или память: пусть все узлы, и этот тоже, сверятся с каталогом.
                _generation = null;
                try { _storage.NewGeneration(); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Log("error", "Не удалось обновить метку поколения после сбоя операции: " + ex.Message);
                }
                throw;
            }
            _generation = _storage.NewGeneration();
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    // ------------------------------------------------------------------ синхронизация узлов

    /// <summary>Сверить узел с каталогом сейчас, дожидаясь замков (для тестов и явных вызовов).</summary>
    internal void SyncNow()
    {
        EnsureStarted();
        TrySync(wait: true);
    }

    private void SyncIfDue()
    {
        var now = Environment.TickCount64;
        if (now - Interlocked.Read(ref _lastSyncCheck) < SyncThrottle.TotalMilliseconds)
            return;
        Interlocked.Exchange(ref _lastSyncCheck, now);
        TrySync(wait: false);
    }

    /// <summary>
    /// wait: false - для SlotName/RunAsync/GetState и таймера: ничего не ждём (занят _gate или замок - пропускаем,
    /// следующая проверка повторит). Вызывается без _gate; сама берёт его и межузловой замок.
    /// </summary>
    private void TrySync(bool wait)
    {
        if (!_started)
            return;
        var generation = _storage.ReadGeneration();
        if (NothingToSync(generation))
            return;
        if (wait)
            _gate.Wait();
        else if (!_gate.Wait(0))
            return;
        try
        {
            using var clusterLock = wait ? _storage.AcquireLock() : _storage.TryAcquireLock(TimeSpan.Zero);
            if (clusterLock is not null)
                SyncLocked();
        }
        catch (Exception ex)
        {
            Log("error", "синхронизация: " + ex.Message);
            // Не повторяем сразу: при устойчивой ошибке журнал и консоль не должны заполняться каждые 250 мс.
            Interlocked.Exchange(ref _lastSyncCheck, Environment.TickCount64 + 5000);
            if (wait)
                throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Метку не прочли, она не менялась или пропала совсем (том каталога, видимо, не смонтирован) - модули не трогаем.</summary>
    private bool NothingToSync(string? generation) =>
        generation is null || generation == _generation || (generation.Length == 0 && !string.IsNullOrEmpty(_generation));

    /// <summary>Привести загруженные модули в соответствие с манифестами на диске. Вызывается под _gate и межузловым замком.</summary>
    private void SyncLocked()
    {
        var generation = _storage.ReadGeneration();
        if (NothingToSync(generation))
            return;
        var onDisk = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in _storage.InstalledNames().OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
        {
            onDisk.Add(name); // нечитаемый манифест не повод выгружать уже загруженный модуль
            try
            {
                var manifest = ModuleStorage.ReadManifest(_storage.CurrentDir(name));
                if (manifest is null || (_modules.TryGetValue(name, out var loaded) && loaded.Manifest.SameState(manifest)))
                    continue;
                var state = Activate(name, out _);
                Log(state.Error is null ? "info" : "error",
                    $"синхронизация: {name} {state.Version} " + (state.Error is null ? (state.Enabled ? "загружен" : "отключён") : $"не загружен - {state.Error}"));
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or HostException)
            {
                Log("error", $"синхронизация: {name}: манифест не прочитан - {ex.Message}"); // иначе через сверку нельзя было бы даже удалить такой модуль
            }
        }
        foreach (var name in _modules.Keys.Where(n => !onDisk.Contains(n)).ToList())
        {
            if (!_modules.TryRemove(name, out var gone))
                continue;
            RebuildRoutes(out _);
            gone.Unload();
            Log("info", $"синхронизация: {name} {gone.Manifest.Version} выгружен");
        }
        _generation = generation;
    }

    // ------------------------------------------------------------------ загрузка

    /// <summary>(Пере)загрузить модуль из current по его module.json и переключить маршруты. Вызывается под _gate.</summary>
    private ModuleState Activate(string name, out bool rebindRequired)
    {
        var dir = _storage.CurrentDir(name);
        var manifest = ModuleStorage.ReadManifest(dir) ?? throw new HostException($"Модуль {name} не установлен.");
        LoadedModule next;
        if (!manifest.Enabled)
            next = LoadedModule.Disabled(manifest);
        else
            next = TryLoad(manifest, dir, out var error) ?? LoadedModule.Failed(manifest, error);

        _modules.TryGetValue(name, out var old);
        _modules[name] = next;
        if (next.Context is not null)
            _telemetry?.Loaded(name, next.Manifest.Version);
        RebuildRoutes(out rebindRequired);
        old?.Unload();
        return ToState(next);
    }

    private LoadedModule? TryLoad(ModuleManifest manifest, string dir, out string? error)
    {
        error = null;
        var mainPath = Path.Combine(dir, string.IsNullOrEmpty(manifest.MainFile) ? MainFileName(manifest.Name) : manifest.MainFile);
        if (!File.Exists(mainPath))
        {
            error = $"нет файла {Path.GetFileName(mainPath)}";
            return null;
        }

        var context = new ModuleLoadContext(manifest.Name, dir, CaptureServer(), lib => FindInOtherModules(manifest.Name, lib));
        try
        {
            var assembly = context.LoadMain(mainPath);
            var fileVersion = assembly.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version;
            if (!string.IsNullOrEmpty(fileVersion))
                manifest.Version = ModuleVersions.Display(fileVersion);
            else if (string.IsNullOrEmpty(manifest.Version))
                manifest.Version = ModuleVersions.Display(assembly.GetName().Version?.ToString());

            var catalog = new AssemblyCatalog(assembly);
            var container = new CompositionContainer(catalog, CompositionOptions.DisableSilentRejection);
            var batch = new CompositionBatch();
            if (_hostServices.TryGetValue(typeof(IMetricsEvents), out var metrics))
                batch.AddExportedValue((IMetricsEvents)metrics);
            container.Compose(batch);

            var activities = new Dictionary<string, IServerActivity>(StringComparer.Ordinal);
            foreach (var activity in container.GetExportedValues<IServerActivity>())
            {
                if (string.IsNullOrWhiteSpace(activity.Name))
                    throw new HostException($"У активности {activity.GetType().FullName} пустое имя (Name).");
                if (!activities.TryAdd(activity.Name, activity))
                    throw new HostException($"Две активности с именем «{activity.Name}» в одном модуле.");
            }
            var factories = container.GetExportedValues<IServerApiFactory>().ToList();
            return new LoadedModule(manifest, context, container, activities, factories, null);
        }
        catch (Exception ex)
        {
            error = Describe(ex);
            try { context.Unload(); } catch { /* уже выгружается */ }
            return null;
        }
    }

    /// <summary>Маршруты «имя активности / объект» по включённым модулям; новые имена занимают свободные слоты.</summary>
    private void RebuildRoutes(out bool rebindRequired)
    {
        rebindRequired = false;
        var builder = ImmutableDictionary.CreateBuilder<string, Route>(StringComparer.Ordinal);
        foreach (var module in _modules.Values.OrderBy(m => m.Manifest.Name, StringComparer.OrdinalIgnoreCase))
            foreach (var (name, activity) in module.Activities)
                builder.TryAdd(name, new Route(module.Manifest.Name, module.Manifest.Version, activity));

        lock (_slotLock)
        {
            foreach (var name in builder.Keys.OrderBy(n => n, StringComparer.Ordinal))
            {
                if (_slotByName.ContainsKey(name))
                    continue;
                var free = Array.IndexOf(_slotNames, null);
                if (free < 0)
                {
                    Log("error", $"Нет свободного слота для активности «{name}» - перезапустите сервер.");
                    builder.Remove(name);
                    continue;
                }
                _slotNames[free] = name;
                _slotByName[name] = free;
                rebindRequired = true;
            }
        }
        Volatile.Write(ref _routes, builder.ToImmutable());
    }

    private int FreeSlots()
    {
        lock (_slotLock)
            return _slotNames.Count(n => n is null);
    }

    private ServerAssemblies CaptureServer() => ServerAssemblies.Capture(exclude: new[] { typeof(ModuleHost).Assembly.GetName().Name! });

    private static void Extract(string zipPath, IReadOnlyList<string> keep, string target)
    {
        var allowed = new HashSet<string>(keep, StringComparer.Ordinal);
        var remaining = ArchiveInspector.MaxUncompressedBytes;
        using var archive = ZipFile.OpenRead(zipPath);
        foreach (var entry in archive.Entries)
        {
            var path = entry.FullName.Replace('\\', '/');
            if (!allowed.Contains(path))
                continue;
            var root = Path.GetFullPath(target).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var destination = Path.GetFullPath(Path.Combine(target, path));
            if (!destination.StartsWith(root, StringComparison.Ordinal))
                throw new HostException($"Недопустимый путь в архиве: {path}");
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            using var input = entry.Open();
            using var output = File.Create(destination);
            remaining -= ArchiveInspector.CopyLimited(input, output, remaining);
        }
    }

    /// <summary>
    /// dll, которой нет в папке модуля, - в папке другого установленного модуля (по алфавиту): так библиотеки
    /// общего модуля доступны остальным. Главные сборки модулей (*.servext) не отдаются.
    /// </summary>
    private string? FindInOtherModules(string module, string assembly)
    {
        if (assembly.EndsWith(".servext", StringComparison.OrdinalIgnoreCase))
            return null;
        foreach (var other in _storage.InstalledNames().OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
        {
            if (string.Equals(other, module, StringComparison.OrdinalIgnoreCase))
                continue;
            var path = Path.Combine(_storage.CurrentDir(other), assembly + ".dll");
            if (File.Exists(path))
                return path;
        }
        return null;
    }

    /// <summary>Сборки в папке модуля с версиями - для таблицы библиотек общего модуля в админке.</summary>
    private List<AssemblyRef> AssembliesOf(string name)
    {
        var list = new List<AssemblyRef>();
        var dir = _storage.CurrentDir(name);
        string[] files;
        try
        {
            files = Directory.GetFiles(dir, "*.dll", SearchOption.AllDirectories);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return list; // нет папки или другой узел как раз меняет версию
        }
        foreach (var path in files.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            string version;
            try { version = AssemblyName.GetAssemblyName(path).Version?.ToString() ?? ""; }
            catch (Exception) { version = ""; } // native
            list.Add(new AssemblyRef { Name = Path.GetFileNameWithoutExtension(path), Version = version });
        }
        return list;
    }

    private ModuleState ToState(LoadedModule m)
    {
        ModuleManifest? previous = null;
        try
        {
            previous = ModuleStorage.ReadManifest(_storage.PreviousDir(m.Manifest.Name));
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            // другой узел как раз меняет previous или манифест повреждён - показываем модуль без предыдущей версии
        }
        return new ModuleState
        {
            Name = m.Manifest.Name,
            Version = m.Manifest.Version,
            Enabled = m.Manifest.Enabled,
            Loaded = m.Context is not null,
            Error = m.Error,
            InstalledAt = m.Manifest.InstalledAt,
            InstalledBy = m.Manifest.InstalledBy,
            Reason = m.Manifest.Reason,
            Sha256 = m.Manifest.Sha256,
            Files = m.Manifest.Files,
            Assemblies = AssembliesOf(m.Manifest.Name),
            PreviousVersion = previous?.Version,
            LoadedAt = m.LoadedAt,
            Activities = m.Activities.Select(a =>
            {
                _stats.TryGetValue(a.Key, out var s);
                return new ActivityState
                {
                    Name = a.Key,
                    TypeName = a.Value.GetType().FullName ?? "",
                    Calls = s?.Calls ?? 0,
                    Errors = s?.Errors ?? 0,
                    LastError = s?.LastError,
                    LastCallAt = s?.LastCallAt,
                };
            }).OrderBy(a => a.Name).ToList(),
        };
    }

    private static List<AssemblyRef> NativeExtensions()
    {
        var self = typeof(ModuleHost).Assembly;
        return AssemblyLoadContext.Default.Assemblies
            .Where(a => a != self && a.GetName().Name?.EndsWith(".servext", StringComparison.OrdinalIgnoreCase) == true)
            .Select(a => new AssemblyRef
            {
                Name = a.GetName().Name!,
                Version = ModuleVersions.Display(a.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version ?? a.GetName().Version?.ToString()),
            })
            .OrderBy(a => a.Name)
            .ToList();
    }

    private static string HostVersion()
    {
        var asm = typeof(ModuleHost).Assembly;
        return ModuleVersions.Display(asm.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version ?? asm.GetName().Version?.ToString());
    }

    private static string MainFileName(string moduleName) => moduleName + ArchiveInspector.MainSuffix;

    private static string ActivityList(LoadedModule m) =>
        m.Activities.Count == 0 ? "без активностей" : "активности: " + string.Join(", ", m.Activities.Keys.OrderBy(k => k));

    private static string Describe(Exception ex)
    {
        if (ex is ReflectionTypeLoadException rtle)
            return string.Join("; ", rtle.LoaderExceptions.Where(e => e is not null).Select(e => e!.Message).Distinct().Take(5));
        if (ex is CompositionException ce)
            return string.Join("; ", ce.Errors.Select(e => e.Description).Distinct().Take(5));
        return ex.InnerException is null ? ex.Message : $"{ex.Message} / {Describe(ex.InnerException)}";
    }

    /// <summary>Загрузка с диска: формат идентификатора проверяется до построения пути.</summary>
    private UploadMeta GetUpload(string uploadId) =>
        _storage.ReadUpload(uploadId) ?? throw UploadNotFound();

    private static HostException UploadNotFound() => new("Загрузка не найдена или устарела - начните заново.");

    private static HostException NotValidated() => new("Сначала проверьте архив (validate) - установка возможна только после успешной проверки.");

    internal void Log(string level, string text)
    {
        _journal.Enqueue(new JournalEntry { At = DateTime.UtcNow, Level = level, Text = text });
        while (_journal.Count > JournalSize && _journal.TryDequeue(out _)) { }
        // stdout Pilot-Server попадает в docker logs рядом с его собственным журналом.
        Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss,fff} [ServerHost] {level.ToUpperInvariant()} {text}");
    }

    // ------------------------------------------------------------------ типы

    private sealed record Route(string Module, string Version, IServerActivity Activity);

    private sealed class ActivityStats
    {
        public long Calls;
        public long Errors;
        public string? LastError;
        public DateTime? LastCallAt;
    }

    private sealed class LoadedModule
    {
        public LoadedModule(ModuleManifest manifest, ModuleLoadContext? context, CompositionContainer? container,
            Dictionary<string, IServerActivity> activities, List<IServerApiFactory> apiFactories, string? error)
        {
            Manifest = manifest;
            Context = context;
            Container = container;
            Activities = activities;
            ApiFactories = apiFactories;
            Error = error;
            LoadedAt = context is null ? null : DateTime.UtcNow;
        }

        public ModuleManifest Manifest { get; }
        public ModuleLoadContext? Context { get; private set; }
        public CompositionContainer? Container { get; private set; }
        public Dictionary<string, IServerActivity> Activities { get; }
        public List<IServerApiFactory> ApiFactories { get; }
        public string? Error { get; }
        public DateTime? LoadedAt { get; }

        public static LoadedModule Disabled(ModuleManifest manifest) => new(manifest, null, null, new(), new(), null);

        public static LoadedModule Failed(ModuleManifest manifest, string? error) => new(manifest, null, null, new(), new(), error ?? "ошибка загрузки");

        /// <summary>Выгрузка контекста: сборки освободятся, когда завершатся текущие вызовы и сборщик мусора их соберёт.</summary>
        public void Unload()
        {
            try { Container?.Dispose(); } catch { /* модуль мог упасть в Dispose */ }
            try { Context?.Unload(); } catch { /* уже выгружается */ }
            Container = null;
            Context = null;
        }
    }
}

internal sealed class HostException : Exception
{
    public HostException(string message) : base(message) { }
}
