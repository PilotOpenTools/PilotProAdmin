using PilotMigration.Config;
using PilotMigration.Database;
using PilotMigration.Engine;
using PilotMigration.Pilot;

namespace PilotMigration.Archives;

/// <summary>Файловый архив базы в одном контуре и доступ к его хранилищу со стороны сервиса.</summary>
public sealed class ResolvedArchive : IDisposable
{
    public required Guid Id { get; init; }
    public required bool IsS3 { get; init; }

    /// <summary>Записываемый ли архив для сервера Pilot этого контура (settings.xml).</summary>
    public required bool IsWritable { get; init; }

    /// <summary>Как архив записан у сервера Pilot: папка или S3-адрес/бакет.</summary>
    public required string Location { get; init; }

    /// <summary>Ключ физического хранилища: одинаковый ключ - одно и то же хранилище.</summary>
    public required string StorageKey { get; init; }

    public IArchiveStore? Store { get; init; }

    /// <summary>Почему хранилище недоступно сервису (нет соответствия пути, нет ключей S3, …).</summary>
    public string? Problem { get; set; }

    /// <summary>Причина, по которой сервис не может писать в хранилище; null - может (или не проверялось).</summary>
    public string? WriteProblem { get; set; }

    public bool WriteChecked { get; set; }

    public string Title => $"{(IsS3 ? "S3" : "папка")} {Location} ({Id.ToString()[..8]}, {(IsWritable ? "записываемый" : "только чтение")})";

    public void Dispose() => Store?.Dispose();
}

public static class ArchiveResolver
{
    public static List<ResolvedArchive> Resolve(EnvConfig env, ServerDatabaseSettings settings, string tempDirectory)
    {
        var result = new List<ResolvedArchive>();
        foreach (var a in settings.Archives)
        {
            if (!a.IsS3)
            {
                var local = env.MapPath(a.Folder);
                result.Add(new ResolvedArchive
                {
                    Id = a.Id,
                    IsS3 = false,
                    IsWritable = a.IsWritable,
                    Location = a.Folder,
                    StorageKey = local is null ? $"fs-unmapped:{env.Key}:{a.Folder}" : "fs:" + Path.GetFullPath(local).TrimEnd('/'),
                    Store = local is null ? null : new FolderArchiveStore(local),
                    Problem = local is null
                        ? $"для папки «{a.Folder}» не задано соответствие пути в контейнере миграции (Подключения / «{env.Title}» / Пути)"
                        : null,
                });
                continue;
            }

            var serverUrl = (a.S3Url ?? "").TrimEnd('/');
            var bucket = a.S3Bucket ?? "";
            var endpoint = env.FindS3(serverUrl);
            var effective = (endpoint?.EffectiveUrl ?? serverUrl).TrimEnd('/');
            string? problem = null;
            if (endpoint is null)
                problem = $"нет ключей доступа к S3 «{serverUrl}» (Подключения / «{env.Title}» / S3)";
            else if (S3Endpoint.UrlError(effective) is { } urlError)
                problem = urlError;
            else if (string.IsNullOrEmpty(endpoint.AccessKey) || string.IsNullOrEmpty(endpoint.SecretKey))
                problem = $"для S3 «{serverUrl}» не заданы Access key / Secret key";

            result.Add(new ResolvedArchive
            {
                Id = a.Id,
                IsS3 = true,
                IsWritable = a.IsWritable,
                Location = $"{serverUrl}/{bucket}",
                StorageKey = $"s3:{effective.ToLowerInvariant()}/{bucket}",
                Store = problem is null ? new S3ArchiveStore(effective, bucket, endpoint!.AccessKey, endpoint.SecretKey, tempDirectory) : null,
                Problem = problem,
            });
        }
        return result;
    }

    /// <summary>
    /// Архивы присланной файловой базы: Id берутся из её таблицы faslist, расположение тел - из запроса
    /// (в .dbp тел файлов нет). Признак «записываемый» сохраняется от базы-источника: если указать
    /// хранилище приёмника, перенос будет остановлен так же, как для контуров.
    /// </summary>
    public static List<ResolvedArchive> ResolveFileSource(
        IReadOnlyList<DbpArchive> faslist, FileSourceOptions source, string importDirectory, string tempDirectory)
    {
        var result = new List<ResolvedArchive>();
        foreach (var a in faslist)
        {
            var location = source.ArchiveFor(a.Id);
            var title = $"«{a.Name}» ({a.Id.ToString()[..8]})";
            if (location is null)
            {
                result.Add(new ResolvedArchive
                {
                    Id = a.Id,
                    IsS3 = false,
                    IsWritable = a.IsWritable,
                    Location = a.Name,
                    StorageKey = $"import-unset:{a.Id}",
                    Problem = $"не указано, где лежат тела файлов архива {title} присланной базы",
                });
                continue;
            }

            if (!location.IsS3)
            {
                string? folder = null;
                string? problem = null;
                try
                {
                    folder = ImportFiles.Resolve(importDirectory, location.Folder);
                }
                catch (MigrationException ex)
                {
                    problem = ex.Message;
                }
                result.Add(new ResolvedArchive
                {
                    Id = a.Id,
                    IsS3 = false,
                    IsWritable = a.IsWritable,
                    Location = location.Folder,
                    StorageKey = folder is null ? $"import-bad:{a.Id}" : "fs:" + folder.TrimEnd('/', '\\'),
                    Store = folder is null ? null : new FolderArchiveStore(folder),
                    Problem = problem,
                });
                continue;
            }

            var missingKeys = string.IsNullOrEmpty(location.AccessKey) || string.IsNullOrEmpty(location.SecretKey);
            var urlProblem = S3Endpoint.UrlError(location.S3Url);
            result.Add(new ResolvedArchive
            {
                Id = a.Id,
                IsS3 = true,
                IsWritable = a.IsWritable,
                Location = $"{location.S3Url}/{location.S3Bucket}",
                StorageKey = $"s3:{location.S3Url.ToLowerInvariant()}/{location.S3Bucket}",
                Store = missingKeys || urlProblem is not null
                    ? null
                    : new S3ArchiveStore(location.S3Url, location.S3Bucket, location.AccessKey, location.SecretKey, tempDirectory),
                Problem = urlProblem is not null ? $"архив {title}: {urlProblem}" : missingKeys ? $"для архива {title} не заданы Access key / Secret key" : null,
            });
        }
        return result;
    }
}

/// <summary>
/// Сопоставление архивов источника и приёмника: какие хранилища общие, куда класть тела файлов,
/// что блокирует перенос.
/// </summary>
public sealed class ArchiveTopology
{
    private readonly HashSet<(Guid Target, Guid Source)> _shared = new();

    public ArchiveTopology(IReadOnlyList<ResolvedArchive> source, IReadOnlyList<ResolvedArchive> target)
    {
        Source = source;
        Target = target;
    }

    public IReadOnlyList<ResolvedArchive> Source { get; }
    public IReadOnlyList<ResolvedArchive> Target { get; }

    public bool IsShared(Guid targetId, Guid sourceId) => _shared.Contains((targetId, sourceId));

    /// <summary>Архив приёмника, делящий хранилище с каким-либо архивом источника.</summary>
    public ResolvedArchive? SharedSourceFor(ResolvedArchive target) =>
        Source.FirstOrDefault(s => IsShared(target.Id, s.Id));

    public bool IsSharedWithSource(ResolvedArchive target) => SharedSourceFor(target) is not null;

    /// <summary>Записываемый архив приёмника (куда сервер Pilot пишет новые файлы).</summary>
    public ResolvedArchive? TargetWritable => Target.FirstOrDefault(t => t.IsWritable);

    /// <summary>Куда в приёмнике относится тело из архива источника: архив с тем же Id или записываемый архив.</summary>
    public ResolvedArchive? TargetFor(Guid sourceArchiveId) =>
        Target.FirstOrDefault(t => t.Id == sourceArchiveId) ?? TargetWritable;

    /// <summary>Нужно ли физически копировать тела из архива источника в архив приёмника.</summary>
    public bool NeedsCopy(Guid sourceArchiveId, ResolvedArchive target) => !IsShared(target.Id, sourceArchiveId);

    /// <summary>Выясняет общие хранилища: совпадение ключа хранилища или пробный объект, видимый с обеих сторон.</summary>
    public async Task DetectSharingAsync(CancellationToken ct)
    {
        foreach (var t in Target)
        {
            foreach (var s in Source.Where(s => s.IsS3 == t.IsS3 && s.StorageKey == t.StorageKey))
                _shared.Add((t.Id, s.Id));

            if (t.Store is null)
                continue;

            // Пробный объект пишется только в хранилище приёмника; источник лишь читается.
            var probe = ArchiveLayout.ProbePrefix + Guid.NewGuid().ToString("N");
            try
            {
                await t.Store.WriteProbeAsync(probe, ct);
                t.WriteChecked = true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                t.WriteChecked = true;
                t.WriteProblem = ex.Message;
                continue;
            }

            try
            {
                foreach (var s in Source.Where(s => s.IsS3 == t.IsS3 && s.Store is not null))
                {
                    try
                    {
                        if (await s.Store!.ProbeExistsAsync(probe, ct))
                            _shared.Add((t.Id, s.Id));
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        s.Problem ??= "хранилище недоступно: " + ex.Message;
                    }
                }
            }
            finally
            {
                try { await t.Store.DeleteProbeAsync(probe, CancellationToken.None); } catch { /* пробный объект */ }
            }
        }

        foreach (var s in Source.Where(s => s.Store is not null && s.Problem is null))
        {
            var problem = await s.Store!.CheckReadableAsync(ct);
            if (problem is not null)
                s.Problem = problem;
        }
    }

    /// <summary>
    /// Проверки, общие для полной копии и переноса файлов. <paramref name="copyFiles"/> - будут ли вообще копироваться тела.
    /// </summary>
    public void Evaluate(CheckList checks, bool copyFiles, string sourceTitle, string targetTitle)
    {
        foreach (var t in Target)
        {
            var shared = SharedSourceFor(t);
            if (shared is not null)
            {
                if (t.IsWritable && shared.IsWritable)
                {
                    checks.Block($"Файловый архив {t.Title} подключён к тому же хранилищу, что и архив {shared.Title} источника, и записываемый в обеих базах. " +
                                 "Две базы, пишущие в один архив, портят данные друг друга - перенос запрещён. Сделайте архив в одной из баз «только чтение» и подключите ей свой записываемый архив.");
                    continue;
                }
                checks.Note($"Файловый архив {t.Title} - общее хранилище с «{sourceTitle}» ({shared.Title}). " +
                            $"Файлы из него не переносятся: «{targetTitle}» читает их напрямую из архива «{sourceTitle}».");
                continue;
            }

            if (!copyFiles)
                continue;
            if (t.Problem is not null)
                checks.Block($"Архив приёмника {t.Title}: {t.Problem}.");
            else if (t.WriteProblem is not null)
                checks.Block($"Сервис миграции не может писать в архив приёмника {t.Title}: {t.WriteProblem}");
        }

        if (TargetWritable is null)
            checks.Block($"У базы «{targetTitle}» нет записываемого файлового архива.");

        if (!copyFiles)
            return;

        foreach (var s in Source)
        {
            var target = TargetFor(s.Id);
            if (target is null || !NeedsCopy(s.Id, target))
                continue;
            if (s.Problem is not null)
                checks.Block($"Архив источника {s.Title}: {s.Problem}.");
            if (target.Id != s.Id)
                checks.Warn($"Архива {s.Id.ToString()[..8]} источника нет у «{targetTitle}»: его файлы будут положены в записываемый архив {target.Title}.");
        }
    }
}
