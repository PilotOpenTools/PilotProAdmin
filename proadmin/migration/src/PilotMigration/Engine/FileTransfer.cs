using System.Collections.Concurrent;
using Amazon.S3;
using PilotMigration.Archives;

namespace PilotMigration.Engine;

/// <summary>Копирование и чистка тел файлов между архивами источника и приёмника.</summary>
public sealed class FileTransfer
{
    private const int Parallelism = 4;

    private readonly ArchiveTopology _topology;
    private readonly RunLog _log;
    private readonly ConcurrentDictionary<Guid, Dictionary<Guid, long>> _targetListing = new();

    public FileTransfer(ArchiveTopology topology, RunLog log)
    {
        _topology = topology;
        _log = log;
    }

    public long Copied { get; private set; }
    public long CopiedBytes { get; private set; }
    public long AlreadyPresent { get; private set; }
    public long Shared { get; private set; }
    public long MissingInSource { get; private set; }

    /// <summary>Докопирует в архивы приёмника тела, которых там нет. Общие хранилища пропускаются.</summary>
    public async Task CopyMissingAsync(IReadOnlyCollection<(Guid FileId, Guid SourceArchive)> files, string stage, CancellationToken ct)
    {
        var jobs = new List<(Guid FileId, ResolvedArchive From, ResolvedArchive To)>();
        long present0 = 0, shared0 = 0, copied0 = Copied, bytes0 = CopiedBytes;
        foreach (var group in files.GroupBy(f => f.SourceArchive))
        {
            var target = _topology.TargetFor(group.Key)
                         ?? throw new MigrationException("У приёмника нет записываемого файлового архива.");
            if (!_topology.NeedsCopy(group.Key, target))
            {
                Shared += group.Count();
                shared0 += group.Count();
                continue;
            }
            var source = _topology.Source.FirstOrDefault(s => s.Id == group.Key);
            if (source?.Store is null)
            {
                MissingInSource += group.Count();
                _log.Warn($"Архив {group.Key} источника недоступен сервису ({source?.Problem ?? "нет в settings.xml источника"}): {group.Count()} файлов не скопировано.");
                continue;
            }
            if (target.Store is null)
                throw new MigrationException($"Архив приёмника {target.Title} недоступен: {target.Problem}");

            var present = await ListTargetAsync(target, ct);
            foreach (var f in group.DistinctBy(f => f.FileId))
            {
                if (present.ContainsKey(f.FileId))
                {
                    AlreadyPresent++;
                    present0++;
                }
                else
                    jobs.Add((f.FileId, source, target));
            }
        }

        if (jobs.Count == 0)
        {
            _log.Info($"{stage}: копировать нечего (уже есть: {present0}, общее хранилище: {shared0}).");
            return;
        }

        _log.Info($"{stage}: копирую {jobs.Count} файлов (уже есть: {present0}, общее хранилище: {shared0}).");
        var done = 0L;
        var lastReport = DateTimeOffset.UtcNow;
        var missing = new ConcurrentBag<Guid>();
        await Parallel.ForEachAsync(jobs, new ParallelOptions { MaxDegreeOfParallelism = Parallelism, CancellationToken = ct }, async (job, token) =>
        {
            try
            {
                await using var input = await job.From.Store!.OpenReadAsync(job.FileId, token);
                var counting = new CountingStream(input);
                await job.To.Store!.WriteAsync(job.FileId, counting, -1, token);
                lock (this)
                {
                    Copied++;
                    CopiedBytes += counting.BytesRead;
                    _targetListing.GetValueOrDefault(job.To.Id)?.TryAdd(job.FileId, counting.BytesRead);
                }
            }
            catch (Exception ex) when (IsNotFound(ex))
            {
                missing.Add(job.FileId);
            }

            var n = Interlocked.Increment(ref done);
            if (DateTimeOffset.UtcNow - lastReport > TimeSpan.FromSeconds(10))
            {
                lastReport = DateTimeOffset.UtcNow;
                _log.Info($"{stage}: {n} из {jobs.Count}, {(CopiedBytes - bytes0) / 1024 / 1024} МБ");
            }
        });

        MissingInSource += missing.Count;
        if (!missing.IsEmpty)
            _log.Warn($"В архиве источника нет {missing.Count} файлов, на которые ссылается база (например {string.Join(", ", missing.Take(5))}). Они не скопированы.");
        _log.Ok($"{stage}: скопировано {Copied - copied0} файлов, {(CopiedBytes - bytes0) / 1024 / 1024} МБ.");
    }

    /// <summary>Удаляет из архива приёмника тела, на которые больше не ссылается база.</summary>
    public async Task<int> PruneAsync(ResolvedArchive target, ISet<Guid> referenced, IEnumerable<Guid>? candidates, CancellationToken ct)
    {
        if (target.Store is null || _topology.IsSharedWithSource(target))
            return 0;
        var present = await ListTargetAsync(target, ct);
        var toDelete = (candidates ?? present.Keys).Where(id => present.ContainsKey(id) && !referenced.Contains(id)).Distinct().ToList();
        await Parallel.ForEachAsync(toDelete, new ParallelOptions { MaxDegreeOfParallelism = Parallelism, CancellationToken = ct }, async (id, token) =>
        {
            try { await target.Store.DeleteAsync(id, token); }
            catch (Exception ex) when (IsNotFound(ex)) { }
        });
        lock (this)
        {
            foreach (var id in toDelete)
                present.Remove(id);
        }
        return toDelete.Count;
    }

    private async Task<Dictionary<Guid, long>> ListTargetAsync(ResolvedArchive target, CancellationToken ct)
    {
        if (_targetListing.TryGetValue(target.Id, out var cached))
            return cached;
        var listing = await target.Store!.ListAsync(ct);
        _targetListing[target.Id] = listing;
        return listing;
    }

    private static bool IsNotFound(Exception ex) =>
        ex is FileNotFoundException or DirectoryNotFoundException ||
        (ex is AmazonS3Exception s3 && s3.StatusCode == System.Net.HttpStatusCode.NotFound);

    private sealed class CountingStream : Stream
    {
        private readonly Stream _inner;
        public CountingStream(Stream inner) => _inner = inner;
        public long BytesRead { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => BytesRead; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = _inner.Read(buffer, offset, count);
            BytesRead += n;
            return n;
        }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            var n = await _inner.ReadAsync(buffer, ct);
            BytesRead += n;
            return n;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
