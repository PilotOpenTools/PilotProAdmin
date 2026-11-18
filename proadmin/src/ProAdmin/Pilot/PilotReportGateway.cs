using System.Collections.Concurrent;
using System.Net.Sockets;
using Ascon.Pilot.Common;
using Ascon.Pilot.Common.DataProtection;
using Ascon.Pilot.Transport;
using Ascon.Pilot.DataClasses;
using Ascon.Pilot.DataModifier;
using Ascon.Pilot.Server.Api;
using Ascon.Pilot.Server.Api.Contracts;
using ProAdmin.Infrastructure;
using ProAdmin.Reports;

namespace ProAdmin.Pilot;

/// <summary>Отчёт на сервере Pilot: объект типа Report с файлом .repx внутри папки отчётов.</summary>
public sealed record PilotReport(Guid Id, string Name, string Folder, Guid? FolderId, string? FileName, Guid? BodyId, long Size)
{
    /// <summary>SHA-1 git-блоба точного содержимого файла (заполняется после скачивания).</summary>
    public string? Sha { get; init; }

    /// <summary>SHA-1 нормализованного текста (без BOM, LF) - для сравнения с веткой с точностью до переводов строк.</summary>
    public string? NormalizedSha { get; init; }

    /// <summary>Тело файла не найдено в файловом архиве сервера (объект есть, а .repx потерян).</summary>
    public bool FileMissing { get; init; }
}

public sealed record PilotReportFolder(Guid Id, string Path);

public sealed class PilotReportList
{
    public List<PilotReport> Reports { get; } = new();
    public List<PilotReportFolder> Folders { get; } = new();
    public Guid? RootFolderId { get; set; }
}

/// <summary>
/// Отчёты Pilot через пользовательский API базы (не админский: отчёты - обычные объекты с файлами).
/// Вход - учётка пользователя базы с правами на папку отчётов. На операцию - отдельное короткое подключение.
/// Содержимое файлов кэшируется по Id тела файла: оно неизменяемо, поэтому повторно скачиваются только новые версии.
/// </summary>
public sealed class PilotReportGateway
{
    public const string ReportFolderTypeName = "Report_Folder_F2CC6F1D-70E1-4E9B-B32F-BEB3E991318F";
    public const string ReportTypeName = "Report_6088AF81-061E-456E-9225-CF65B7B25368";
    public const string ReportNameAttribute = "Report_name_D745D627-E7CD-4E3A-B30E-F9EDC1A09D77";

    private static readonly Guid StandardRootId = Guid.Parse("00000001-0001-0001-0001-000000000001");
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromMinutes(5);

    private readonly NetworkAddressMapper _network;
    private readonly ILogger<PilotReportGateway> _logger;
    private readonly ConcurrentDictionary<Guid, (string Sha, string NormalizedSha)> _shaByBody = new();

    public PilotReportGateway(NetworkAddressMapper network, ILogger<PilotReportGateway> logger)
    {
        _network = network;
        _logger = logger;
    }

    /// <summary>Все отчёты базы с папками; <paramref name="withContent"/> - ещё и хеши содержимого (скачивает новые файлы).</summary>
    public Task<PilotReportList> ListAsync(string url, PilotLogin login, int licenseType, bool withContent, CancellationToken ct) =>
        RunAsync(url, login, licenseType, async s =>
        {
            var list = new PilotReportList();
            var root = await FindReportRootAsync(s);
            if (root is null)
                throw new PilotGatewayException(PilotErrorKind.Configuration, "В базе не найдена папка отчётов (тип Report_Folder).");
            list.RootFolderId = root.Id;
            list.Folders.Add(new PilotReportFolder(root.Id, ""));
            await WalkAsync(s, root, "", list, ct);

            if (withContent)
            {
                for (var i = 0; i < list.Reports.Count; i++)
                {
                    var r = list.Reports[i];
                    if (r.BodyId is not { } body)
                        continue;
                    if (!_shaByBody.TryGetValue(body, out var sha))
                    {
                        byte[] bytes;
                        try
                        {
                            bytes = Download(s, body);
                        }
                        catch (PilotServerException ex)
                        {
                            // Один битый файл не должен ронять весь список.
                            _logger.LogWarning("Файл отчёта «{Name}» ({Id}, тело {Body}) не скачивается: {Message}", r.Name, r.Id, body, ex.Message);
                            list.Reports[i] = r with { FileMissing = true };
                            continue;
                        }
                        sha = (ReportFiles.BlobSha(bytes), ReportFiles.BlobSha(ReportFiles.Normalize(bytes)));
                        _shaByBody[body] = sha;
                    }
                    list.Reports[i] = r with { Sha = sha.Sha, NormalizedSha = sha.NormalizedSha };
                }
            }
            return list;
        }, ct);

    /// <summary>Содержимое .repx отчёта (null - у отчёта нет файла).</summary>
    public Task<byte[]?> DownloadAsync(string url, PilotLogin login, int licenseType, Guid reportId, CancellationToken ct) =>
        RunAsync(url, login, licenseType, async s =>
        {
            var obj = (await s.Api.GetObjectsAsync(new[] { reportId })).FirstOrDefault();
            var file = obj is null ? null : RepxFile(obj);
            return file is null ? null : Download(s, file.Body.Id);
        }, ct);

    /// <summary>Новая версия отчёта: снимок с новым .repx. Возвращает Id тела нового файла.</summary>
    public Task UpdateAsync(string url, PilotLogin login, int licenseType, Guid reportId, string fileName, byte[] content, string reason, CancellationToken ct) =>
        RunAsync(url, login, licenseType, s =>
        {
            var modifier = s.CreateModifier();
            modifier.EditObject(reportId)
                .CreateSnapshot(reason)
                .AddFile(Document(fileName, content), s.Storage);
            return modifier.AnyChanges() ? ApplyAndConfirmAsync(s, modifier, $"новая версия отчёта {reportId}") : Task.FromResult(true);
        }, ct);

    /// <summary>Новый отчёт в папке отчётов. Возвращает Id объекта.</summary>
    public Task<Guid> CreateAsync(string url, PilotLogin login, int licenseType, Guid folderId, string name, string fileName, byte[] content, CancellationToken ct) =>
        RunAsync(url, login, licenseType, async s =>
        {
            var type = s.Metadata.Types.FirstOrDefault(t => t.Name == ReportTypeName)
                       ?? throw new PilotGatewayException(PilotErrorKind.Configuration, $"В базе нет типа {ReportTypeName}.");
            var id = Guid.NewGuid();
            var modifier = s.CreateModifier();
            modifier.CreateObject(id, folderId, type.Id)
                .SetAttribute(ReportNameAttribute, new DValue { StrValue = name })
                .AddFile(Document(fileName, content), s.Storage);
            await ApplyAndConfirmAsync(s, modifier, $"создание отчёта «{name}»");
            return id;
        }, ct);

    /// <summary>Отчёт в корзину - откат создания.</summary>
    public Task MoveToRecycleBinAsync(string url, PilotLogin login, int licenseType, Guid reportId, CancellationToken ct) =>
        RunAsync(url, login, licenseType, s =>
        {
            var modifier = s.CreateModifier();
            modifier.MoveToRecycleBin(reportId, s.Storage);
            return ApplyAndConfirmAsync(s, modifier, $"отчёт {reportId} в корзину");
        }, ct);

    private static readonly TimeSpan ConfirmTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Apply и ожидание подтверждения сервера (NotifyChangeAsyncCompleted / NotifyChangeAsyncError).
    /// Сервер применяет изменение асинхронно: если закрыть сессию сразу после Apply, он падает на отправке
    /// подтверждения (ClosedCallbackException), а открытые клиенты Pilot не узнают о новой версии до перезахода.
    /// Ошибка сервера здесь же превращается в исключение - раньше она терялась.
    /// </summary>
    internal async Task<bool> ApplyAndConfirmAsync(Session s, Modifier modifier, string what)
    {
        var confirmation = s.Changes.Expect();
        modifier.Apply(null);
        if (await Task.WhenAny(confirmation, Task.Delay(ConfirmTimeout)) != confirmation)
        {
            _logger.LogWarning("Сервер Pilot не подтвердил изменение за {Seconds} с: {What}", ConfirmTimeout.TotalSeconds, what);
            return true;
        }
        var error = await confirmation;
        if (error is not null)
            throw new PilotGatewayException(PilotErrorKind.Other, $"Сервер Pilot отклонил изменение ({what}): {error}");
        return true;
    }

    // ------------------------------------------------------------------

    internal static DocumentInfo Document(string fileName, byte[] content)
    {
        var now = DateTime.UtcNow;
        return new DocumentInfo(fileName, () => new MemoryStream(content, writable: false), now, now, now);
    }

    private async Task WalkAsync(Session s, DObject folder, string path, PilotReportList list, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var ids = folder.Children.Select(c => c.ObjectId).ToArray();
        if (ids.Length == 0)
            return;

        var folderType = s.TypeId(ReportFolderTypeName);
        var children = await s.Api.GetObjectsAsync(ids);
        foreach (var child in children.Where(c => !IsRemoved(c)))
        {
            if (child.TypeId == folderType)
            {
                var childPath = path.Length == 0 ? Title(child, s.Metadata) : $"{path}/{Title(child, s.Metadata)}";
                list.Folders.Add(new PilotReportFolder(child.Id, childPath));
                await WalkAsync(s, child, childPath, list, ct);
                continue;
            }

            var file = RepxFile(child);
            if (file is null)
                continue;
            list.Reports.Add(new PilotReport(child.Id, ReportName(child, s.Metadata), path, folder.Id, file.Name, file.Body.Id, file.Body.Size));
        }
    }

    internal static bool IsRemoved(DObject o) =>
        o.StateInfo?.State is ObjectState.InRecycleBin or ObjectState.DeletedPermanently;

    private static DFile? RepxFile(DObject obj) =>
        obj.ActualFileSnapshot?.Files?.FirstOrDefault(f => !f.IsDeleted && f.Name.EndsWith(".repx", StringComparison.OrdinalIgnoreCase));

    private static string ReportName(DObject obj, DMetadata metadata)
    {
        if (obj.Attributes.TryGetValue(ReportNameAttribute, out var v) && !string.IsNullOrWhiteSpace(v.StrValue))
            return v.StrValue!.Trim();
        return Title(obj, metadata);
    }

    internal static string Title(DObject obj, DMetadata metadata)
    {
        var type = metadata.Types.FirstOrDefault(t => t.Id == obj.TypeId);
        if (type is not null)
        {
            var title = NObjectExtensions.GetTitle(obj, type);
            if (!string.IsNullOrWhiteSpace(title))
                return title.Trim();
        }
        foreach (var attr in obj.Attributes.Values)
            if (!string.IsNullOrWhiteSpace(attr.StrValue))
                return attr.StrValue!.Trim();
        return obj.Id.ToString();
    }

    private static async Task<DObject?> FindReportRootAsync(Session s)
    {
        var folderType = s.TypeId(ReportFolderTypeName);
        if (folderType < 0)
            return null;

        var roots = await s.Api.GetObjectsAsync(new[] { StandardRootId });
        if (roots.Count == 0 || !roots[0].Context.Any())
            return null;
        var globalRoot = (await s.Api.GetObjectsAsync(new[] { roots[0].Context[0] })).FirstOrDefault();
        var folderIds = globalRoot?.Children.Where(c => c.TypeId == folderType).Select(c => c.ObjectId).ToArray() ?? Array.Empty<Guid>();
        return folderIds.Length == 0 ? null : (await s.Api.GetObjectsAsync(folderIds)).FirstOrDefault();
    }

    internal static byte[] Download(Session s, Guid bodyId)
    {
        const int chunkSize = 1024 * 1024;
        using var ms = new MemoryStream();
        long offset = 0;
        while (true)
        {
            var chunk = s.Files.GetFileChunk(bodyId, offset, chunkSize);
            if (chunk is null || chunk.Length == 0) break;
            ms.Write(chunk, 0, chunk.Length);
            offset += chunk.Length;
            if (chunk.Length < chunkSize) break;
        }
        return ms.ToArray();
    }

    /// <summary>Короткая сессия пользователя базы на одну операцию (используется и шлюзом модулей).</summary>
    internal async Task<T> RunAsync<T>(string url, PilotLogin login, int licenseType, Func<Session, Task<T>> action, CancellationToken ct)
    {
        if (!PilotAddress.TryParse(url, out var address, out var error))
            throw new PilotGatewayException(PilotErrorKind.Configuration, error!);
        if (address!.Database.Length == 0)
            throw new PilotGatewayException(PilotErrorKind.Configuration, $"В адресе «{url}» не указана база данных.");

        var serverUrl = _network.ToInternal(address.ServerUrl);
        Session? session = null;
        try
        {
            var work = Task.Run(async () =>
            {
                session = Session.Open(serverUrl, address.Database, login, licenseType);
                return await action(session);
            }, ct);
            return await work.WaitAsync(OperationTimeout, ct);
        }
        catch (PilotGatewayException)
        {
            throw;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var translated = Translate(ex, address.ServerUrl);
            if (translated.Kind != PilotErrorKind.Authentication)
                _logger.LogWarning(ex, "Ошибка Pilot (объекты базы) {Url}", url);
            throw translated;
        }
        finally
        {
            session?.Dispose();
        }
    }

    private static PilotGatewayException Translate(Exception ex, string serverUrl)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            var typeName = e.GetType().Name;
            if (e is PilotSecurityAccessDeniedException or UnauthorizedAccessException ||
                typeName.Contains("AccessDenied") || typeName.Contains("Unauthorized") || typeName.Contains("Authentication") ||
                e.Message.Contains("пароль", StringComparison.OrdinalIgnoreCase) || e.Message.Contains("password", StringComparison.OrdinalIgnoreCase))
            {
                return new PilotGatewayException(PilotErrorKind.Authentication,
                    "Не удалось войти в базу Pilot: нужен логин и пароль пользователя базы с правами на нужную папку (отчётов или модулей).", ex);
            }
            if (e is HttpRequestException or SocketException or TimeoutException)
                return new PilotGatewayException(PilotErrorKind.Connection, $"Сервер Pilot {serverUrl} недоступен: {e.Message}", ex);
        }
        return new PilotGatewayException(PilotErrorKind.Other, $"Ошибка сервера Pilot: {ex.Message}", ex);
    }

    /// <summary>Подключение пользователя к базе: API объектов, файлов и Modifier для записи.</summary>
    internal sealed class Session : IDisposable
    {
        private readonly HttpPilotClient _client;
        private readonly string _storageDir;
        private Backend? _backend;
        private FileSystemStorageProvider? _storage;
        private readonly IServerApi _syncApi;
        private readonly IMessagesApi? _messages;

        private Session(HttpPilotClient client, ChangeCallback changes, IServerAsyncApi api, IServerApi syncApi, IFileArchiveApi files, IMessagesApi? messages, DMetadata metadata)
        {
            _client = client;
            Changes = changes;
            Api = api;
            _syncApi = syncApi;
            Files = files;
            _messages = messages;
            Metadata = metadata;
            _storageDir = Path.Combine(Path.GetTempPath(), "pilot-admin-reports", Guid.NewGuid().ToString("N"));
        }

        public IServerAsyncApi Api { get; }
        public IFileArchiveApi Files { get; }

        /// <summary>Подтверждения асинхронных изменений от сервера.</summary>
        public ChangeCallback Changes { get; }
        public DMetadata Metadata { get; }

        public FileSystemStorageProvider Storage
        {
            get
            {
                if (_storage is null)
                {
                    Directory.CreateDirectory(_storageDir);
                    _storage = new FileSystemStorageProvider(_storageDir);
                }
                return _storage;
            }
        }

        /// <summary>API, которое выставляет серверное расширение (IServerApiFactory), - по имени интерфейса.</summary>
        public T CustomApi<T>() where T : class => _client.GetCustomApi<T>();

        public int TypeId(string name) => Metadata.Types.FirstOrDefault(t => t.Name == name)?.Id ?? -1;

        public Modifier CreateModifier()
        {
            _backend ??= new Backend(_syncApi, _messages, new ChangesetUploader(Files, Storage, null), null);
            return new Modifier(_backend, ChangesetDataSource.Native, MergeChangePolicy.Allow, false);
        }

        public static Session Open(string serverUrl, string database, PilotLogin login, int licenseType)
        {
            var url = serverUrl.TrimEnd('/') + "/" + Uri.EscapeDataString(database);
            var credentials = ConnectionCredentials.GetConnectionCredentials(url, login.Login, login.Password.ConvertToSecureString());
            var client = new HttpPilotClient(credentials.GetConnectionString(), credentials.GetConnectionProxy());
            try
            {
                client.Connect(false);
                var callback = new ChangeCallback();
                var api = client.GetServerAsyncApi(callback);
                var syncApi = client.GetServerApi(callback);
                var files = client.GetFileArchiveApi();
                IMessagesApi? messages = null;
                try { messages = client.GetMessagesApi(new NullMessageCallback()); } catch { /* не нужен для записи отчётов */ }

                client.GetAuthenticationApi().Login(database, credentials.Username, credentials.ProtectedPassword, false, licenseType);
                var db = api.OpenDatabaseAsync().GetAwaiter().GetResult();
                var metadata = api.GetMetadataAsync(db?.MetadataVersion ?? 0).GetAwaiter().GetResult();
                return new Session(client, callback, api, syncApi, files, messages, metadata);
            }
            catch
            {
                try { client.Disconnect(); } catch { /* уже закрыто */ }
                try { client.Dispose(); } catch { /* уже закрыто */ }
                throw;
            }
        }

        public void Dispose()
        {
            try { _client.Disconnect(); } catch { /* уже закрыто */ }
            try { _client.Dispose(); } catch { /* уже закрыто */ }
            try { if (Directory.Exists(_storageDir)) Directory.Delete(_storageDir, true); } catch { /* временные файлы */ }
        }
    }

    private sealed class NullMessageCallback : IMessageCallback
    {
        public void NotifyMessageCreated(NotifiableDMessage message) { }
        public void NotifyTypingMessage(Guid chatId, int personId) { }
        public void NotifyOnline(int personId) { }
        public void NotifyOffline(int personId) { }
        public void CreateNotification(DNotification notification) { }
        public void UpdateLastMessageDate(DateTime maxDate) { }
    }

    /// <summary>Колбэк сервера: ждёт результат ChangeAsync, остальные уведомления не нужны.</summary>
    internal sealed class ChangeCallback : IServerCallback
    {
        private TaskCompletionSource<string?>? _pending;

        /// <summary>Задача завершится null (изменение применено) или текстом ошибки сервера.</summary>
        public Task<string?> Expect()
        {
            var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending = tcs;
            return tcs.Task;
        }

        public void NotifyChangeAsyncCompleted(DChangeset changeset) => _pending?.TrySetResult(null);

        public void NotifyChangeAsyncError(Guid identity, ProtoExceptionInfo exception)
        {
            string text;
            try { text = ProtoExceptionInfo.Deserialize(exception)?.Message ?? "неизвестная ошибка"; }
            catch { text = exception?.JsonData ?? "неизвестная ошибка"; }
            _pending?.TrySetResult(text);
        }

        public void NotifyChangeset(DChangeset changeset) { }
        public void NotifyOrganisationUnitChangeset(OrganisationUnitChangeset changeset) { }
        public void NotifyPersonChangeset(PersonChangeset changeset) { }
        public void NotifyDMetadataChangeset(DMetadataChangeset changeset) { }
        public void NotifySearchResult(DSearchResult searchResult) { }
        public void NotifyGeometrySearchResult(DGeometrySearchResult searchResult) { }
        public void NotifyDNotificationChangeset(DNotificationChangeset changeset) { }
        public void NotifyCommandResult(Guid requestId, byte[] data, ServerCommandResult result) { }
        public void NotifyCustomNotification(string name, byte[] data) { }
        public void NotifyAccessChangeset(Guid objectId) { }
        public void NotifySettingsChangeset(string settingKey) { }
    }
}
