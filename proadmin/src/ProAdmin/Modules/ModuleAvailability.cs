using System.Text.Json;
using ProAdmin.GitLab;
using ProAdmin.Infrastructure;
using ProAdmin.Jobs;
using ProAdmin.Pilot;
using Microsoft.Extensions.Options;

namespace ProAdmin.Modules;

/// <summary>
/// Клиентский модуль, отключённый на контуре, - объект Extension в корзине Pilot. Клиент Pilot перестаёт видеть такой объект:
/// работающий клиент выгружает расширение, при следующем запуске удаляет его папку из кэша. Права и история версий сохраняются.
/// Корзина убирает объект из папки расширений, поэтому в обходе дерева его не найти: админка помнит его здесь.
/// </summary>
public sealed class DisabledModule
{
    public string Env { get; set; } = "";
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string Additional { get; set; } = "";
    public string Folder { get; set; } = "";
    public Guid FolderId { get; set; }
    public string Reason { get; set; } = "";
    public string By { get; set; } = "";
    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;

    public string? Version => ModuleVersion.Display(ModulePackage.VersionFromAdditional(Additional));

    /// <summary>Для сопоставления с репозиторием и пакетом - по тем же правилам, что у живых модулей.</summary>
    public PilotModule ToModule() => new(Id, Name, Additional, Folder, FolderId, null, null, 0, Reason, null);
}

/// <summary>Отключённые клиентские модули по контурам: data/disabled-modules.json.</summary>
public sealed class DisabledModuleStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly string _path;
    private readonly ILogger<DisabledModuleStore> _logger;
    private readonly object _lock = new();
    private List<DisabledModule> _items;

    public DisabledModuleStore(IOptions<AppOptions> options, ILogger<DisabledModuleStore> logger)
    {
        _logger = logger;
        _path = Path.Combine(Path.GetFullPath(options.Value.DataDirectory), "disabled-modules.json");
        _items = Load();
    }

    public IReadOnlyList<DisabledModule> For(string env)
    {
        lock (_lock) return _items.Where(m => m.Env == env).ToList();
    }

    public DisabledModule? Find(string env, Guid id)
    {
        lock (_lock) return _items.FirstOrDefault(m => m.Env == env && m.Id == id);
    }

    public void Add(DisabledModule module)
    {
        lock (_lock)
        {
            _items = _items.Where(m => !(m.Env == module.Env && m.Id == module.Id)).Append(module).ToList();
            Write();
        }
    }

    public void Remove(string env, Guid id)
    {
        lock (_lock)
        {
            _items = _items.Where(m => !(m.Env == env && m.Id == id)).ToList();
            Write();
        }
    }

    private void Write()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_items, Json));
        File.Move(tmp, _path, overwrite: true);
    }

    private List<DisabledModule> Load()
    {
        try
        {
            return File.Exists(_path) ? JsonSerializer.Deserialize<List<DisabledModule>>(File.ReadAllText(_path), Json) ?? new() : new();
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            // Без списка выкладка создала бы второй объект вместо отключённого: не запускаемся молча с пустым.
            _logger.LogError(ex, "Не прочитан {Path}", _path);
            throw;
        }
    }
}

/// <summary>Кто и откуда меняет состояние модуля (для журнала «Операции»).</summary>
public sealed record ModuleActor(string PilotUser, string? GitLabUser)
{
    public string Name => GitLabUser ?? PilotUser;
}

/// <summary>Отключить, включить и удалить клиентский модуль на контуре. Каждое действие попадает в журнал «Операции».</summary>
public sealed class ModuleAvailabilityService
{
    private readonly PilotModuleGateway _pilot;
    private readonly DisabledModuleStore _disabled;
    private readonly AssemblySpecStore _libraries;
    private readonly JobStore _jobs;

    public ModuleAvailabilityService(PilotModuleGateway pilot, DisabledModuleStore disabled, AssemblySpecStore libraries, JobStore jobs)
    {
        _pilot = pilot;
        _disabled = disabled;
        _libraries = libraries;
        _jobs = jobs;
    }

    /// <summary>
    /// Почему модуль нельзя отключать; null - можно. Модули Preload клиент грузит первыми, а библиотеки общего модуля
    /// нужны остальным: без него ломаются все зависимые модули.
    /// </summary>
    public string? ProtectedReason(PilotModule module) => ProtectedReason(module, _libraries.For(ModuleKind.Client).SharedModule);

    internal static string? ProtectedReason(PilotModule module, string? sharedModule)
    {
        if (!string.IsNullOrWhiteSpace(sharedModule) &&
            (ProjectNames.Key(module.Name) == ProjectNames.Key(sharedModule) ||
             ModulePackage.CodeFromName(module.Name) is { } code && ProjectNames.Key(code) == ProjectNames.Key(sharedModule)))
            return "Это общий модуль: его библиотеки нужны остальным модулям. Он не отключается.";
        if (module.Folder == AssemblySpec.PreloadFolder || module.Folder.StartsWith(AssemblySpec.PreloadFolder + "/", StringComparison.Ordinal))
            return $"Модуль лежит в папке «{AssemblySpec.PreloadFolder}»: другие модули могут зависеть от его библиотек. Сначала перенесите его из этой папки.";
        return null;
    }

    /// <summary>В корзину. Запись появляется раньше изменения на сервере: выкладка из CI не должна успеть создать второй объект.</summary>
    public Task<string?> DisableAsync(ModuleEnv env, PilotLogin login, int license, string jobName, PilotModule module, string? reason, ModuleActor actor, CancellationToken ct)
    {
        if (ProtectedReason(module) is { } denied)
            return Task.FromResult<string?>(denied);
        if (string.IsNullOrWhiteSpace(reason))
            return Task.FromResult<string?>("Укажите причину отключения.");
        return RunAsync(env, jobName, "отключение модуля", module.Name, reason.Trim(), actor, async job =>
        {
            _disabled.Add(new DisabledModule
            {
                Env = env.Key,
                Id = module.Id,
                Name = module.Name,
                Additional = module.Additional,
                Folder = module.Folder,
                FolderId = module.FolderId,
                Reason = reason.Trim(),
                By = actor.Name,
            });
            try
            {
                await _pilot.MoveToRecycleBinAsync(env.PilotUrl, login, license, module.Id, ct);
            }
            catch
            {
                _disabled.Remove(env.Key, module.Id);
                throw;
            }
            job.Add("ok", $"Сервер {env.Title}: «{module.Name}» отключён. Меню и команды модуля исчезнут у пользователей сразу, полностью он выгрузится при перезапуске клиента.");
        });
    }

    /// <summary>Из корзины в прежнюю папку. Права на объект не менялись, назначать модуль заново не нужно.</summary>
    public Task<string?> EnableAsync(ModuleEnv env, PilotLogin login, int license, string jobName, DisabledModule module, ModuleActor actor, CancellationToken ct) =>
        RunAsync(env, jobName, "включение модуля", module.Name, null, actor, async job =>
        {
            var state = await _pilot.RestoreFromRecycleBinAsync(env.PilotUrl, login, license, module.Id, module.FolderId, ct);
            _disabled.Remove(env.Key, module.Id);
            if (state == RecycledModuleState.Gone)
                throw new WorkflowException($"На сервере {env.Title} объекта модуля «{module.Name}» больше нет (удалён в Pilot). Запись об отключении убрана: выкладка создаст новый объект, его нужно будет назначить пользователям.");
            job.Add("ok", state == RecycledModuleState.Alive
                ? $"Сервер {env.Title}: «{module.Name}» уже восстановлен из корзины в Pilot."
                : $"Сервер {env.Title}: «{module.Name}» включён. Пользователи получат его сразу, без перезапуска клиента.");
        });

    /// <summary>Окончательное удаление отключённого модуля: объект и все его версии. Живой объект не трогает.</summary>
    public Task<string?> DeleteAsync(ModuleEnv env, PilotLogin login, int license, string jobName, DisabledModule module, string? confirm, string? reason, ModuleActor actor, CancellationToken ct)
    {
        if (!string.Equals(confirm?.Trim(), module.Name, StringComparison.OrdinalIgnoreCase))
            return Task.FromResult<string?>($"Для удаления введите имя модуля точно: «{module.Name}».");
        if (string.IsNullOrWhiteSpace(reason))
            return Task.FromResult<string?>("Укажите причину удаления.");
        return RunAsync(env, jobName, "удаление модуля", module.Name, reason.Trim(), actor, async job =>
        {
            var state = await _pilot.DeletePermanentlyAsync(env.PilotUrl, login, license, module.Id, ct);
            if (state == RecycledModuleState.Alive)
            {
                _disabled.Remove(env.Key, module.Id);
                throw new WorkflowException($"На сервере {env.Title} модуль «{module.Name}» восстановлен из корзины в Pilot - он работает и не удалён. Чтобы удалить, отключите его снова.");
            }
            _disabled.Remove(env.Key, module.Id);
            job.Add("ok", state == RecycledModuleState.Gone
                ? $"Сервер {env.Title}: объекта модуля «{module.Name}» уже нет, запись об отключении убрана."
                : $"Сервер {env.Title}: «{module.Name}» удалён окончательно вместе с историей версий.");
        });
    }

    private async Task<string?> RunAsync(ModuleEnv env, string jobName, string operation, string moduleName, string? reason, ModuleActor actor, Func<PublishJob, Task> run)
    {
        var jobKey = ModuleKind.Client.JobEnv(env.Key);
        if (_jobs.FindActive(jobKey, jobName) is { } active)
            return $"По модулю «{jobName}» на контуре {env.Title} уже идёт операция «{active.Operation}» (запустил {active.PilotUser}).";

        var job = new PublishJob
        {
            Operation = operation,
            Env = jobKey,
            EnvTitle = ModuleKind.Client.JobEnvTitle(env.Title),
            Branch = moduleName,
            AutomationName = jobName,
            CommitMessage = operation,
            ChangeNotes = reason is null ? new() : new() { reason },
            PilotUser = actor.PilotUser,
            GitLabUser = actor.GitLabUser,
        };
        job.Add("info", $"Модуль «{moduleName}»: {operation} на сервере {env.Title}." + (reason is null ? "" : $" Причина: {reason}"));
        _jobs.Add(job);
        try
        {
            await run(job);
            job.Update(j => j.Status = JobStatus.Succeeded);
            return null;
        }
        catch (Exception ex) when (ex is WorkflowException or PilotGatewayException or IOException or UnauthorizedAccessException)
        {
            job.Update(j => { j.Status = JobStatus.Failed; j.Error = ex.Message; });
            job.Add("error", ex.Message);
            return ex.Message;
        }
    }
}
