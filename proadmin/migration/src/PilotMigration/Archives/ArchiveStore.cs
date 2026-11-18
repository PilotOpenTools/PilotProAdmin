using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;

namespace PilotMigration.Archives;

/// <summary>
/// Хранилище тел файлов одного файлового архива Pilot. Раскладка одинакова для папки и S3:
/// <c>{байт 14 Id}/{байт 15 Id}/{Id}</c>, например <c>103/182/bfcfe351-b5fd-43dc-92af-1d003c7d67b6</c>.
/// </summary>
public interface IArchiveStore : IDisposable
{
    string Describe { get; }

    /// <summary>Все тела в хранилище: Id / размер.</summary>
    Task<Dictionary<Guid, long>> ListAsync(CancellationToken ct);

    Task<Stream> OpenReadAsync(Guid bodyId, CancellationToken ct);

    Task WriteAsync(Guid bodyId, Stream content, long size, CancellationToken ct);

    Task DeleteAsync(Guid bodyId, CancellationToken ct);

    /// <summary>Пробный объект в корне хранилища - для проверки записи и поиска общих хранилищ.</summary>
    Task WriteProbeAsync(string name, CancellationToken ct);

    Task<bool> ProbeExistsAsync(string name, CancellationToken ct);

    Task DeleteProbeAsync(string name, CancellationToken ct);

    /// <summary>null - хранилище доступно на чтение, иначе причина.</summary>
    Task<string?> CheckReadableAsync(CancellationToken ct);
}

public static class ArchiveLayout
{
    public static string RelativePath(Guid bodyId)
    {
        var b = bodyId.ToByteArray();
        return $"{b[14]}/{b[15]}/{bodyId}";
    }

    public const string ProbePrefix = ".pilot-migration-probe-";
}

public sealed class FolderArchiveStore : IArchiveStore
{
    private readonly string _root;

    public FolderArchiveStore(string root) => _root = root.TrimEnd('/', '\\');

    public string Root => _root;

    public string Describe => "папка " + _root;

    public Task<Dictionary<Guid, long>> ListAsync(CancellationToken ct)
    {
        var result = new Dictionary<Guid, long>();
        if (!Directory.Exists(_root))
            throw new MigrationException($"Папка архива не найдена: {_root}");
        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            if (Guid.TryParse(Path.GetFileName(file), out var id))
                result[id] = new FileInfo(file).Length;
        }
        return Task.FromResult(result);
    }

    public Task<Stream> OpenReadAsync(Guid bodyId, CancellationToken ct) =>
        Task.FromResult<Stream>(new FileStream(PathOf(bodyId), FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true));

    public async Task WriteAsync(Guid bodyId, Stream content, long size, CancellationToken ct)
    {
        var path = PathOf(bodyId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".migrating";
        await using (var file = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            await content.CopyToAsync(file, ct);
        File.Move(tmp, path, overwrite: true);
    }

    public Task DeleteAsync(Guid bodyId, CancellationToken ct)
    {
        var path = PathOf(bodyId);
        if (File.Exists(path))
            File.Delete(path);
        return Task.CompletedTask;
    }

    public Task WriteProbeAsync(string name, CancellationToken ct) =>
        File.WriteAllTextAsync(Path.Combine(_root, name), "probe", ct);

    public Task<bool> ProbeExistsAsync(string name, CancellationToken ct) =>
        Task.FromResult(File.Exists(Path.Combine(_root, name)));

    public Task DeleteProbeAsync(string name, CancellationToken ct)
    {
        File.Delete(Path.Combine(_root, name));
        return Task.CompletedTask;
    }

    public Task<string?> CheckReadableAsync(CancellationToken ct) =>
        Task.FromResult(Directory.Exists(_root) ? null : $"папка {_root} не найдена (проверьте монтирование и соответствие путей)");

    public string PathOf(Guid bodyId) => Path.Combine(_root, ArchiveLayout.RelativePath(bodyId));

    public void Dispose() { }
}

public sealed class S3ArchiveStore : IArchiveStore
{
    private readonly AmazonS3Client _client;
    private readonly string _bucket;
    private readonly string _url;
    private readonly string _tempDirectory;

    public S3ArchiveStore(string url, string bucket, string accessKey, string secretKey, string tempDirectory)
    {
        _url = url;
        _bucket = bucket;
        _tempDirectory = tempDirectory;
        _client = new AmazonS3Client(new BasicAWSCredentials(accessKey, secretKey), new AmazonS3Config
        {
            ServiceURL = url,
            ForcePathStyle = true,
            AuthenticationRegion = "us-east-1",
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
            Timeout = TimeSpan.FromMinutes(10),
        });
    }

    public string Describe => $"S3 {_url}/{_bucket}";

    public async Task<Dictionary<Guid, long>> ListAsync(CancellationToken ct)
    {
        var result = new Dictionary<Guid, long>();
        var request = new ListObjectsV2Request { BucketName = _bucket, MaxKeys = 1000 };
        while (true)
        {
            var response = await _client.ListObjectsV2Async(request, ct);
            foreach (var o in response.S3Objects ?? new List<S3Object>())
            {
                var name = o.Key[(o.Key.LastIndexOf('/') + 1)..];
                if (Guid.TryParse(name, out var id))
                    result[id] = o.Size ?? 0;
            }
            if (response.IsTruncated != true)
                break;
            request.ContinuationToken = response.NextContinuationToken;
        }
        return result;
    }

    public async Task<Stream> OpenReadAsync(Guid bodyId, CancellationToken ct)
    {
        var response = await _client.GetObjectAsync(_bucket, ArchiveLayout.RelativePath(bodyId), ct);
        return response.ResponseStream;
    }

    public async Task WriteAsync(Guid bodyId, Stream content, long size, CancellationToken ct)
    {
        // S3 нужна известная длина и повторяемый поток - копируем через временный файл.
        Directory.CreateDirectory(_tempDirectory);
        var tmp = Path.Combine(_tempDirectory, bodyId.ToString("N") + ".upload");
        try
        {
            await using (var file = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
                await content.CopyToAsync(file, ct);
            await using var read = new FileStream(tmp, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);
            await _client.PutObjectAsync(new PutObjectRequest
            {
                BucketName = _bucket,
                Key = ArchiveLayout.RelativePath(bodyId),
                InputStream = read,
                AutoCloseStream = false,
                UseChunkEncoding = false,
            }, ct);
        }
        finally
        {
            try { File.Delete(tmp); } catch { /* временный файл */ }
        }
    }

    public Task DeleteAsync(Guid bodyId, CancellationToken ct) =>
        _client.DeleteObjectAsync(_bucket, ArchiveLayout.RelativePath(bodyId), ct);

    public Task WriteProbeAsync(string name, CancellationToken ct) =>
        _client.PutObjectAsync(new PutObjectRequest { BucketName = _bucket, Key = name, ContentBody = "probe", UseChunkEncoding = false }, ct);

    public async Task<bool> ProbeExistsAsync(string name, CancellationToken ct)
    {
        try
        {
            await _client.GetObjectMetadataAsync(_bucket, name, ct);
            return true;
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    public Task DeleteProbeAsync(string name, CancellationToken ct) =>
        _client.DeleteObjectAsync(_bucket, name, ct);

    /// <summary>Доступен ли бакет на чтение.</summary>
    public async Task<string?> CheckReadableAsync(CancellationToken ct)
    {
        try
        {
            await _client.ListObjectsV2Async(new ListObjectsV2Request { BucketName = _bucket, MaxKeys = 1 }, ct);
            return null;
        }
        catch (AmazonServiceException ex)
        {
            return ex.Message;
        }
        catch (HttpRequestException ex)
        {
            return ex.Message;
        }
    }

    public void Dispose() => _client.Dispose();
}
