using System.IO.Compression;
using ProAdmin.Modules;
using ProAdmin.Pilot;
using ProAdmin.Pilot.ServerModules;

namespace ProAdmin.Tests;

/// <summary>Ручная загрузка серверного модуля кусками из потока и проверка архива новой версии самого ServerHost.</summary>
public class ServerModuleHostTests
{
    private const int Chunk = 512 * 1024;

    /// <summary>Хост вместо сервера Pilot: запоминает запросы и копии кусков (как сериализация - в момент вызова).</summary>
    private sealed class FakeApi : IServerModulesApi
    {
        public List<string> Ops { get; } = new();
        public List<HostRequest> Requests { get; } = new();
        public List<byte[]> Chunks { get; } = new();
        public int FailChunk { get; init; } = -1;

        public Task<string> InvokeAsync(string requestJson)
        {
            var request = System.Text.Json.JsonSerializer.Deserialize<HostRequest>(requestJson,
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
            Requests.Add(request);
            Ops.Add(request.Op);
            return Task.FromResult(request.Op == HostOps.BeginUpload ? """{"ok":true,"uploadId":"up1"}""" : """{"ok":true}""");
        }

        public Task<string> UploadChunkAsync(string uploadId, byte[] data)
        {
            Assert.Equal("up1", uploadId);
            Chunks.Add(data.ToArray());
            Ops.Add("chunk");
            return Task.FromResult(Chunks.Count - 1 == FailChunk ? """{"ok":false,"error":"нет места"}""" : """{"ok":true}""");
        }
    }

    private static byte[] Pattern(int length) => Enumerable.Range(0, length).Select(i => (byte)(i * 31 % 251)).ToArray();

    [Fact]
    public async Task Upload_sends_chunks_of_512KB_and_exact_tail()
    {
        var data = Pattern(2 * Chunk + 300_000);
        var api = new FakeApi();

        var response = await PilotServerModuleGateway.UploadAsync(api, "m.zip", new MemoryStream(data), data.Length, stripServerAssemblies: true, default);

        Assert.Null(response.Error);
        Assert.Equal(new[] { Chunk, Chunk, 300_000 }, api.Chunks.Select(c => c.Length));
        // Хвост буфера прошлого куска в последний попасть не должен: склейка кусков равна исходному архиву.
        Assert.Equal(data, api.Chunks.SelectMany(c => c).ToArray());
        Assert.Equal(new[] { HostOps.BeginUpload, "chunk", "chunk", "chunk", HostOps.Validate }, api.Ops);
        Assert.Equal(data.Length, api.Requests[0].Size);
        Assert.Equal("m.zip", api.Requests[0].FileName);
        Assert.Equal("up1", api.Requests[1].UploadId);
        Assert.True(api.Requests[1].StripServerAssemblies);
    }

    [Fact]
    public async Task Upload_of_exact_multiple_has_no_empty_chunk()
    {
        var data = Pattern(2 * Chunk);
        var api = new FakeApi();

        await PilotServerModuleGateway.UploadAsync(api, "m.zip", new MemoryStream(data), data.Length, false, default);

        Assert.Equal(new[] { Chunk, Chunk }, api.Chunks.Select(c => c.Length));
    }

    [Fact]
    public async Task Upload_fills_chunks_from_a_stream_that_returns_short_reads()
    {
        var data = Pattern(Chunk + 10);
        var api = new FakeApi();

        await PilotServerModuleGateway.UploadAsync(api, "m.zip", new TrickleStream(data, 4096), data.Length, false, default);

        Assert.Equal(new[] { Chunk, 10 }, api.Chunks.Select(c => c.Length));
        Assert.Equal(data, api.Chunks.SelectMany(c => c).ToArray());
    }

    [Fact]
    public async Task Upload_stops_on_chunk_error_without_validate()
    {
        var data = Pattern(3 * Chunk);
        var api = new FakeApi { FailChunk = 1 };

        var response = await PilotServerModuleGateway.UploadAsync(api, "m.zip", new MemoryStream(data), data.Length, false, default);

        Assert.Equal("нет места", response.Error);
        Assert.Equal(2, api.Chunks.Count);
        Assert.DoesNotContain(HostOps.Validate, api.Ops);
    }

    [Fact]
    public async Task Upload_fails_when_stream_is_shorter_than_declared()
    {
        var data = Pattern(1000);
        var api = new FakeApi();

        var response = await PilotServerModuleGateway.UploadAsync(api, "m.zip", new MemoryStream(data), 5000, false, default);

        Assert.Contains("1000", response.Error);
        Assert.DoesNotContain(HostOps.Validate, api.Ops);
    }

    /// <summary>Поток, который отдаёт не больше <c>step</c> байт за чтение (как сетевой или временный файл формы).</summary>
    private sealed class TrickleStream(byte[] data, int step) : MemoryStream(data)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(buffer.Length, step)], cancellationToken);
    }

    // ---------------------------------------------------------------- архив хоста

    private static readonly byte[] Dll = File.ReadAllBytes(typeof(ModulePackage).Assembly.Location);
    private static readonly string DllVersion = ModulePackage.ReadAssembly(Dll, "PilotProAdmin.dll").Version;

    private static byte[] Zip(params (string Name, byte[] Content)[] entries)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, content) in entries)
            {
                using var s = zip.CreateEntry(name).Open();
                s.Write(content);
            }
        return ms.ToArray();
    }

    [Fact]
    public void Host_archive_with_root_assembly_is_accepted_and_describes_the_version()
    {
        var zip = Zip((ServerHostPackage.MainAssembly, Dll), ("Dep.dll", Dll));

        var (package, downgrade) = ServerHostPackage.Check(zip, "ServerHost.zip", currentVersion: null);

        Assert.False(downgrade);
        Assert.Equal(DllVersion, package.Version);
        // Формат поля - как у существующих записей: «Версия X - Размер N КБ».
        Assert.Equal($"Версия {DllVersion} - Размер {ModulePackage.FormatSize(zip.Length)}", package.ExtensionAdditional);
        Assert.Same(zip, package.Zip);
    }

    [Theory]
    [InlineData("bin/ServerHost.servext.dll")]
    [InlineData("Other.servext.dll")]
    [InlineData("ServerHost.dll")]
    public void Host_archive_without_assembly_in_root_is_rejected(string entry)
    {
        var ex = Assert.Throws<InvalidDataException>(() => ServerHostPackage.Check(Zip((entry, Dll)), "h.zip", null));
        Assert.Contains("ServerHost.servext.dll", ex.Message);
    }

    [Fact]
    public void Host_archive_must_be_a_zip_with_a_dotnet_assembly()
    {
        var notZip = Assert.Throws<InvalidDataException>(() => ServerHostPackage.Check(new byte[] { 1, 2, 3, 4 }, "h.zip", null));
        Assert.Contains("не zip", notZip.Message);

        var notDotNet = Assert.Throws<InvalidDataException>(() =>
            ServerHostPackage.Check(Zip((ServerHostPackage.MainAssembly, new byte[] { 1, 2, 3 })), "h.zip", null));
        Assert.Contains("не .NET", notDotNet.Message);
    }

    [Fact]
    public void Host_archive_size_is_bounded()
    {
        Assert.Throws<InvalidDataException>(() => ServerHostPackage.Check(Array.Empty<byte>(), "h.zip", null));
        ServerHostPackage.CheckSize(ServerHostPackage.MaxSize, "h.zip");
        Assert.Throws<InvalidDataException>(() => ServerHostPackage.CheckSize(ServerHostPackage.MaxSize + 1, "h.zip"));
    }

    [Theory]
    [InlineData("999.0.0", true)]
    [InlineData("0.0.1", false)]
    [InlineData(null, false)]
    [InlineData("не версия", false)]
    public void Host_downgrade_is_detected_against_the_version_in_the_database(string? current, bool expected)
    {
        var (_, downgrade) = ServerHostPackage.Check(Zip((ServerHostPackage.MainAssembly, Dll)), "h.zip", current);
        Assert.Equal(expected, downgrade);
    }

    [Fact]
    public void Host_with_the_same_version_is_not_a_downgrade()
    {
        var (_, downgrade) = ServerHostPackage.Check(Zip((ServerHostPackage.MainAssembly, Dll)), "h.zip", DllVersion);
        Assert.False(downgrade);
    }

    [Fact]
    public void Host_object_is_found_by_name_or_file_name()
    {
        PilotModule Ext(string name, string? file) => new(Guid.NewGuid(), name, "Версия 1.0.0 - Размер 1 КБ", "", Guid.Empty, file, null, 0, null, null);
        var other = Ext("Other", "Other.zip");
        var byName = Ext("serverhost", "x.zip");
        var byFile = Ext("host", "ServerHost.zip");

        Assert.Same(byName, ServerHostPackage.Find(new[] { other, byFile, byName }));
        Assert.Same(byFile, ServerHostPackage.Find(new[] { other, byFile }));
        Assert.Null(ServerHostPackage.Find(new[] { other }));
        // Чужое расширение с похожим именем файла хостом не считается: иначе архив хоста затёр бы его.
        Assert.Null(ServerHostPackage.Find(new[] { Ext("Foo", "ServerHostFoo.zip") }));
        Assert.Equal("1.0.0", byName.Version);
    }
}
