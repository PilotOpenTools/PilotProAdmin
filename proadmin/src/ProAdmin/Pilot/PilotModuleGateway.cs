using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Ascon.Pilot.DataClasses;
using ProAdmin.Modules;

namespace ProAdmin.Pilot;

/// <summary>
/// Клиентский модуль на сервере Pilot: объект типа Extension с zip модуля в папке расширений.
/// Назначение пользователям - права на этот объект; при выкладке объект не пересоздаётся, права не меняются.
/// </summary>
public sealed record PilotModule(Guid Id, string Name, string Additional, string Folder, Guid FolderId,
    string? FileName, Guid? BodyId, long Size, string? Reason, DateTime? SnapshotCreated)
{
    /// <summary>Версия X.Y.Z из «Версия 1.2.1 - Размер 93 КБ» (старые записи «1.2.1.0» - тоже 1.2.1).</summary>
    public string? Version => ModuleVersion.Display(ModulePackage.VersionFromAdditional(Additional));

    /// <summary>Коммит, выложенный админкой (из причины версии «… @ abc1234 …»); null - выложен вручную.</summary>
    public string? DeployedSha => ModuleDeploy.ShaFromReason(Reason);
}

/// <summary>Что с объектом модуля на сервере, когда админка считает его отключённым.</summary>
public enum RecycledModuleState
{
    /// <summary>Лежал в корзине, действие выполнено.</summary>
    Done,
    /// <summary>Живой: восстановили в самом Pilot, мимо админки.</summary>
    Alive,
    /// <summary>Объекта больше нет.</summary>
    Gone,
}

public sealed class PilotModuleList
{
    public List<PilotModule> Modules { get; } = new();
    public Guid? RootFolderId { get; set; }
}

/// <summary>
/// Клиентские модули Pilot через пользовательский API базы (как отчёты - это обычные объекты с файлами).
/// Вход - пользователь базы с правами на папку расширений (обычно администратор базы).
/// </summary>
public sealed class PilotModuleGateway : IModuleTarget
{
    public ModuleKind Kind => ModuleKind.Client;

    public string AfterUpdateNote => "Пользователи получат версию при следующем запуске клиента.";

    public string? AfterCreateWarning(string extensionName, string envTitle) =>
        $"Сервер {envTitle}: модуль «{extensionName}» создан, но никому не назначен - назначьте его пользователям в Pilot-myAdmin (права на модуль).";

    public const string ExtensionFolderTypeName = "Extension_folder";
    public const string ExtensionTypeName = "Extension";

    /// <summary>Серверное расширение (*.servext в zip): Pilot-Server распаковывает и грузит его при старте базы.</summary>
    public const string ServerExtensionTypeName = "$PilotServerExtension";
    public const string NameAttribute = "Extension_name 53522556-0749-46DE-913E-EDA195AD1299";
    public const string AdditionalAttribute = "Extension_additional 9A428222-2031-4F5E-A367-C5BAA18DFCA5";

    /// <summary>Откуда объект попал в корзину: его записывает MoveToRecycleBin.</summary>
    private const string DeleteSourceAttribute = "Delete_source_EFED1D30-E3E2-49E1-BDF3-13C544DADE9F";

    private static readonly Guid StandardRootId = Guid.Parse("00000001-0001-0001-0001-000000000001");

    private readonly PilotReportGateway _sessions;
    private readonly ConcurrentDictionary<Guid, byte[]> _zipByBody = new();

    public PilotModuleGateway(PilotReportGateway sessions) => _sessions = sessions;

    /// <summary>Все модули базы (без корзины) с папками.</summary>
    public Task<PilotModuleList> ListAsync(string url, PilotLogin login, int licenseType, CancellationToken ct) =>
        _sessions.RunAsync(url, login, licenseType, async s =>
        {
            var list = new PilotModuleList();
            var roots = await FindRootsAsync(s);
            if (roots.Count == 0)
                throw new PilotGatewayException(PilotErrorKind.Configuration, "В базе не найдена папка расширений (тип Extension_folder) или нет прав на неё.");
            list.RootFolderId = roots[0].Id;
            foreach (var root in roots)
                await WalkAsync(s, root, "", list, ExtensionTypeName, ct);
            return list;
        }, ct);

    /// <summary>Серверные расширения базы ($PilotServerExtension) в папках расширений - например, сам ServerHost.</summary>
    public Task<List<PilotModule>> ListServerExtensionsAsync(string url, PilotLogin login, int licenseType, CancellationToken ct) =>
        _sessions.RunAsync(url, login, licenseType, async s =>
        {
            var list = new PilotModuleList();
            foreach (var root in await FindRootsAsync(s))
                await WalkAsync(s, root, "", list, ServerExtensionTypeName, ct);
            return list.Modules;
        }, ct);

    /// <summary>
    /// Новая версия серверного расширения: снимок с новым zip, как у клиентских модулей. Pilot-Server подхватит её
    /// только при следующем старте базы (перезапуск pilot-server).
    /// </summary>
    public Task UpdateServerExtensionAsync(string url, PilotLogin login, int licenseType, Guid id, string fileName, byte[] zip,
        string additional, string reason, CancellationToken ct) =>
        _sessions.RunAsync(url, login, licenseType, s =>
        {
            var modifier = s.CreateModifier();
            modifier.EditObject(id)
                .SetAttribute(AdditionalAttribute, new DValue { StrValue = additional })
                .CreateSnapshot(reason)
                .AddFile(PilotReportGateway.Document(fileName, zip), s.Storage);
            return _sessions.ApplyAndConfirmAsync(s, modifier, $"новая версия серверного расширения {fileName}");
        }, ct);

    /// <summary>
    /// Первая установка серверного расширения (так ставят ServerHost): объект $PilotServerExtension с zip в корневой папке
    /// расширений, как делает «Модули расширения» клиента Pilot. Pilot-Server загрузит его при следующем старте базы.
    /// </summary>
    public Task<Guid> CreateServerExtensionAsync(string url, PilotLogin login, int licenseType, string name, string fileName, byte[] zip,
        string additional, string reason, CancellationToken ct) =>
        _sessions.RunAsync(url, login, licenseType, async s =>
        {
            var type = s.Metadata.Types.FirstOrDefault(t => t.Name == ServerExtensionTypeName)
                       ?? throw new PilotGatewayException(PilotErrorKind.Configuration, $"В базе нет типа {ServerExtensionTypeName}.");
            var root = (await FindRootsAsync(s)).FirstOrDefault()
                       ?? throw new PilotGatewayException(PilotErrorKind.Configuration, "В базе не найдена папка расширений (тип Extension_folder) или нет прав на неё.");
            var attributes = type.Attributes.Select(a => a.Name).ToHashSet(StringComparer.Ordinal);
            var id = Guid.NewGuid();
            var modifier = s.CreateModifier();
            var builder = modifier.CreateObject(id, root.Id, type.Id);
            if (attributes.Contains(NameAttribute))
                builder.SetAttribute(NameAttribute, new DValue { StrValue = name });
            if (attributes.Contains(AdditionalAttribute))
                builder.SetAttribute(AdditionalAttribute, new DValue { StrValue = additional });
            builder.CreateSnapshot(reason).AddFile(PilotReportGateway.Document(fileName, zip), s.Storage);
            await _sessions.ApplyAndConfirmAsync(s, modifier, $"установка серверного расширения {fileName}");
            return id;
        }, ct);

    public async Task<IReadOnlyList<PackageAssembly>?> AssembliesAsync(string url, PilotLogin login, int licenseType, PilotModule module, CancellationToken ct)
    {
        var zip = await DownloadAsync(url, login, licenseType, module, ct);
        if (zip is null)
            return null;
        string? main = null;
        try { main = ModulePackage.Analyze(zip, module.FileName ?? module.Name + ".zip").AssemblyName; }
        catch (InvalidDataException) { }
        return ModulePackage.ListAssemblies(zip, main);
    }

    /// <summary>Zip модуля с сервера (кэш по Id тела файла - оно неизменяемо).</summary>
    public Task<byte[]?> DownloadAsync(string url, PilotLogin login, int licenseType, PilotModule module, CancellationToken ct)
    {
        if (module.BodyId is not { } body)
            return Task.FromResult<byte[]?>(null);
        if (_zipByBody.TryGetValue(body, out var cached))
            return Task.FromResult<byte[]?>(cached);
        return _sessions.RunAsync<byte[]?>(url, login, licenseType, s =>
        {
            var bytes = PilotReportGateway.Download(s, body);
            _zipByBody[body] = bytes;
            return Task.FromResult<byte[]?>(bytes);
        }, ct);
    }

    /// <summary>Новая версия модуля: снимок с новым zip (старый уходит в историю версий) и новые атрибуты имени/версии.</summary>
    public Task UpdateAsync(string url, PilotLogin login, int licenseType, Guid moduleId, ModulePackageInfo package, string reason, CancellationToken ct) =>
        _sessions.RunAsync(url, login, licenseType, s =>
        {
            var modifier = s.CreateModifier();
            modifier.EditObject(moduleId)
                .SetAttribute(NameAttribute, new DValue { StrValue = package.ExtensionName })
                .SetAttribute(AdditionalAttribute, new DValue { StrValue = package.ExtensionAdditional })
                .CreateSnapshot(reason)
                .AddFile(PilotReportGateway.Document(package.FileName, package.Zip), s.Storage);
            return _sessions.ApplyAndConfirmAsync(s, modifier, $"новая версия модуля {package.ExtensionName}");
        }, ct);

    /// <summary>
    /// Новый модуль в папке расширений. Возвращает Id объекта. Пользователям он не назначен -
    /// назначение делается в Pilot-myAdmin (права на объект).
    /// </summary>
    public Task<Guid> CreateAsync(string url, PilotLogin login, int licenseType, Guid folderId, ModulePackageInfo package, string reason, CancellationToken ct) =>
        _sessions.RunAsync(url, login, licenseType, async s =>
        {
            var type = s.Metadata.Types.FirstOrDefault(t => t.Name == ExtensionTypeName)
                       ?? throw new PilotGatewayException(PilotErrorKind.Configuration, $"В базе нет типа {ExtensionTypeName}.");
            var id = Guid.NewGuid();
            var modifier = s.CreateModifier();
            modifier.CreateObject(id, folderId, type.Id)
                .SetAttribute(NameAttribute, new DValue { StrValue = package.ExtensionName })
                .SetAttribute(AdditionalAttribute, new DValue { StrValue = package.ExtensionAdditional })
                .CreateSnapshot(reason)
                .AddFile(PilotReportGateway.Document(package.FileName, package.Zip), s.Storage);
            await _sessions.ApplyAndConfirmAsync(s, modifier, $"создание модуля {package.ExtensionName}");
            return id;
        }, ct);

    /// <summary>Вернуть прежний zip (откат неудавшейся выкладки).</summary>
    public Task RestoreAsync(string url, PilotLogin login, int licenseType, PilotModule previous, byte[] zip, string reason, CancellationToken ct) =>
        _sessions.RunAsync(url, login, licenseType, s =>
        {
            var modifier = s.CreateModifier();
            modifier.EditObject(previous.Id)
                .SetAttribute(NameAttribute, new DValue { StrValue = previous.Name })
                .SetAttribute(AdditionalAttribute, new DValue { StrValue = previous.Additional })
                .CreateSnapshot(reason)
                .AddFile(PilotReportGateway.Document(previous.FileName ?? "module.zip", zip), s.Storage);
            return _sessions.ApplyAndConfirmAsync(s, modifier, $"откат модуля {previous.Name}");
        }, ct);

    public Task MoveToRecycleBinAsync(string url, PilotLogin login, int licenseType, Guid moduleId, CancellationToken ct) =>
        _sessions.RunAsync(url, login, licenseType, s =>
        {
            var modifier = s.CreateModifier();
            modifier.MoveToRecycleBin(moduleId, s.Storage);
            return _sessions.ApplyAndConfirmAsync(s, modifier, $"модуль {moduleId} в корзину");
        }, ct);

    /// <summary>
    /// Из корзины в папку, где модуль лежал (её помнит сам Pilot - атрибут Delete_source); папки уже нет - в <paramref name="fallbackFolder"/>
    /// или корень расширений. Права на объект корзина не меняет.
    /// </summary>
    public Task<RecycledModuleState> RestoreFromRecycleBinAsync(string url, PilotLogin login, int licenseType, Guid moduleId, Guid fallbackFolder, CancellationToken ct) =>
        _sessions.RunAsync(url, login, licenseType, async s =>
        {
            var obj = (await s.Api.GetObjectsAsync(new[] { moduleId })).FirstOrDefault();
            if (RecycledState(obj) != RecycledModuleState.Done)
                return RecycledState(obj);

            var candidates = new List<Guid>();
            if (Guid.TryParse(Attr(obj!, DeleteSourceAttribute), out var source))
                candidates.Add(source);
            candidates.Add(fallbackFolder);
            var folders = await s.Api.GetObjectsAsync(candidates.Where(c => c != Guid.Empty).Distinct().ToArray());
            var folder = candidates.Select(c => folders.FirstOrDefault(f => f.Id == c)).FirstOrDefault(f => f is not null && !PilotReportGateway.IsRemoved(f))?.Id
                         ?? (await FindRootsAsync(s)).FirstOrDefault()?.Id
                         ?? throw new PilotGatewayException(PilotErrorKind.Configuration, "В базе не найдена папка расширений (тип Extension_folder) или нет прав на неё.");

            var modifier = s.CreateModifier();
            modifier.MoveFromRecycleBin(moduleId, folder);
            await _sessions.ApplyAndConfirmAsync(s, modifier, $"модуль {moduleId} из корзины");
            return RecycledModuleState.Done;
        }, ct);

    /// <summary>Окончательное удаление объекта и всех его версий. Только из корзины: живой объект возвращается как есть.</summary>
    public Task<RecycledModuleState> DeletePermanentlyAsync(string url, PilotLogin login, int licenseType, Guid moduleId, CancellationToken ct) =>
        _sessions.RunAsync(url, login, licenseType, async s =>
        {
            var obj = (await s.Api.GetObjectsAsync(new[] { moduleId })).FirstOrDefault();
            if (RecycledState(obj) != RecycledModuleState.Done)
                return RecycledState(obj);
            var modifier = s.CreateModifier();
            modifier.DeletePermanently(moduleId, s.Storage);
            await _sessions.ApplyAndConfirmAsync(s, modifier, $"модуль {moduleId} удалён окончательно");
            return RecycledModuleState.Done;
        }, ct);

    private static RecycledModuleState RecycledState(DObject? obj) => obj?.StateInfo?.State switch
    {
        null or ObjectState.DeletedPermanently => RecycledModuleState.Gone,
        ObjectState.InRecycleBin => RecycledModuleState.Done,
        _ => RecycledModuleState.Alive,
    };

    // ------------------------------------------------------------------

    private static async Task<List<DObject>> FindRootsAsync(PilotReportGateway.Session s)
    {
        var folderType = s.TypeId(ExtensionFolderTypeName);
        if (folderType < 0)
            return new();
        var roots = await s.Api.GetObjectsAsync(new[] { StandardRootId });
        if (roots.Count == 0 || !roots[0].Context.Any())
            return new();
        var globalRoot = (await s.Api.GetObjectsAsync(new[] { roots[0].Context[0] })).FirstOrDefault();
        var ids = globalRoot?.Children.Where(c => c.TypeId == folderType).Select(c => c.ObjectId).ToArray() ?? Array.Empty<Guid>();
        return ids.Length == 0 ? new() : (await s.Api.GetObjectsAsync(ids)).Where(o => !PilotReportGateway.IsRemoved(o)).ToList();
    }

    private static async Task WalkAsync(PilotReportGateway.Session s, DObject folder, string path, PilotModuleList list, string typeName, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var ids = folder.Children.Select(c => c.ObjectId).ToArray();
        if (ids.Length == 0)
            return;

        var folderType = s.TypeId(ExtensionFolderTypeName);
        var moduleType = s.TypeId(typeName);
        foreach (var child in (await s.Api.GetObjectsAsync(ids)).Where(c => !PilotReportGateway.IsRemoved(c)))
        {
            if (child.TypeId == folderType)
            {
                var title = PilotReportGateway.Title(child, s.Metadata);
                await WalkAsync(s, child, path.Length == 0 ? title : $"{path}/{title}", list, typeName, ct);
                continue;
            }
            if (child.TypeId != moduleType)
                continue;

            var file = child.ActualFileSnapshot?.Files?.FirstOrDefault(f => !f.IsDeleted);
            list.Modules.Add(new PilotModule(
                child.Id,
                Attr(child, NameAttribute) ?? PilotReportGateway.Title(child, s.Metadata),
                Attr(child, AdditionalAttribute) ?? "",
                path,
                folder.Id,
                file?.Name,
                file?.Body.Id,
                file?.Body.Size ?? 0,
                child.ActualFileSnapshot?.Reason,
                child.ActualFileSnapshot?.Created));
        }
    }

    private static string? Attr(DObject obj, string name) =>
        obj.Attributes.TryGetValue(name, out var v) && !string.IsNullOrWhiteSpace(v.StrValue) ? v.StrValue!.Trim() : null;
}
