using System.IO.Compression;
using Xunit;
using Ascon.Pilot.DataClasses;
using Ascon.Pilot.DataModifier;
using Ascon.Pilot.ServerExtensions.SDK;
using ServerHost.Contract;
using ServerHost.Hosting;

namespace ServerHost.Tests;

public sealed class ModuleHostTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "host-tests", Guid.NewGuid().ToString("N"));

    public void Dispose() => ModuleStorage.DeleteDirectory(_root);

    [Fact]
    public void Inspect_ServerAssemblyWithSameVersion_IsErrorUnlessStripped()
    {
        var zip = SampleZip();
        var server = ServerAssemblies.Capture();

        var strict = ArchiveInspector.Inspect(zip, "sample.zip", server, stripServerAssemblies: false).Report;
        Assert.Contains(strict.Errors, e => e.Contains("Newtonsoft.Json"));
        Assert.Equal("Sample", strict.ModuleName);

        var stripped = ArchiveInspector.Inspect(zip, "sample.zip", server, stripServerAssemblies: true);
        Assert.Empty(stripped.Report.Errors);
        Assert.Contains(stripped.Report.Files, f => f.AssemblyName == "Newtonsoft.Json" && f.Status == FileStatus.Duplicate && f.Stripped);
        Assert.DoesNotContain("Newtonsoft.Json.dll", stripped.KeepEntries);
        Assert.Contains("Sample.servext.dll", stripped.KeepEntries);
    }

    [Fact]
    public void Inspect_ServerAssemblyWithOtherVersion_IsWarning()
    {
        var server = new ServerAssemblies(new[] { ("Newtonsoft.Json", (Version?)new Version(12, 0, 0, 0)) });

        var report = ArchiveInspector.Inspect(SampleZip(), "sample.zip", server, stripServerAssemblies: false).Report;

        Assert.Empty(report.Errors);
        var file = Assert.Single(report.Files, f => f.AssemblyName == "Newtonsoft.Json");
        Assert.Equal(FileStatus.VersionMismatch, file.Status);
        Assert.Equal("12.0.0.0", file.ServerVersion);
        Assert.Contains(report.Warnings, w => w.Contains("12.0.0.0"));
    }

    [Fact]
    public void Inspect_RejectsTraversalAndMissingMain()
    {
        var zip = Zip(("../evil.dll", new byte[] { 1 }), ("readme.txt", new byte[] { 1 }));

        var report = ArchiveInspector.Inspect(zip, "bad.zip", new ServerAssemblies(Array.Empty<(string, Version?)>()), false).Report;

        Assert.Contains(report.Errors, e => e.Contains("Недопустимый путь"));
        Assert.Contains(report.Errors, e => e.Contains("servext.dll"));
    }

    [Theory]
    [InlineData(".servext.dll")]
    [InlineData("..servext.dll")]
    [InlineData("...servext.dll")]
    [InlineData("a b.servext.dll")]
    [InlineData("a..b.servext.dll")]
    public void Inspect_RejectsInvalidModuleName(string mainFile)
    {
        var zip = Zip((mainFile, SampleModule.Assembly));

        var report = ArchiveInspector.Inspect(zip, "bad.zip", new ServerAssemblies(Array.Empty<(string, Version?)>()), false).Report;

        Assert.Contains(report.Errors, e => e.Contains("Недопустимое имя модуля"));
        Assert.Equal("", report.ModuleName);
    }

    [Theory]
    [InlineData("Sample", true)]
    [InlineData("087OpenStoradge", true)]
    [InlineData("Мой_модуль-2", true)]
    [InlineData("", false)]
    [InlineData(".", false)]
    [InlineData("..", false)]
    [InlineData("../staging", false)]
    [InlineData("a/b", false)]
    [InlineData("a\b", false)]
    [InlineData(".hidden", false)]
    [InlineData("trailing.", false)]
    public void ModuleName_validation(string name, bool valid) => Assert.Equal(valid, ModuleStorage.IsValidName(name));

    [Fact]
    public void Inspect_RejectsZipBomb()
    {
        var big = new byte[3 * 1024 * 1024]; // сжимается в килобайты, распаковывается в мегабайты
        var zip = Zip(("Sample.servext.dll", SampleModule.Assembly), ("data.bin", big));
        var server = new ServerAssemblies(Array.Empty<(string, Version?)>());

        Assert.True(zip.Length < big.Length / 10);
        var report = ArchiveInspector.Inspect(zip, "bomb.zip", server, false, maxUncompressedBytes: 1024 * 1024).Report;
        Assert.Contains(report.Errors, e => e.Contains("Распакованный размер"));
        Assert.Empty(ArchiveInspector.Inspect(zip, "ok.zip", server, false).Report.Errors);

        var many = Zip(Enumerable.Range(0, ArchiveInspector.MaxEntries + 1).Select(i => ($"f{i}.txt", new byte[] { 1 })).ToArray());
        Assert.Contains(ArchiveInspector.Inspect(many, "many.zip", server, false).Report.Errors, e => e.Contains("файлов"));
    }

    [Fact]
    public void CopyLimited_StopsAtLimit()
    {
        using var ok = new MemoryStream();
        Assert.Equal(100, ArchiveInspector.CopyLimited(new MemoryStream(new byte[100]), ok, 100));
        Assert.Equal(100, ok.Length);
        // общий бюджет: второй файл получает остаток, а не новый полный лимит
        var remaining = 150L;
        remaining -= ArchiveInspector.CopyLimited(new MemoryStream(new byte[100]), new MemoryStream(), remaining);
        Assert.Throws<HostException>(() => ArchiveInspector.CopyLimited(new MemoryStream(new byte[100]), new MemoryStream(), remaining));
        Assert.Throws<HostException>(() => ArchiveInspector.CopyLimited(new MemoryStream(new byte[101]), new MemoryStream(), 100));
    }

    [Fact]
    public void TestServerAction_ci_copy_matches_template()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !Directory.Exists(Path.Combine(dir, "ci-template")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        Assert.Equal(File.ReadAllBytes(Path.Combine(dir!, "ci-template", ".gitlab-ci.yml")),
                     File.ReadAllBytes(Path.Combine(dir!, "TestServerAction", ".gitlab-ci.yml")));
    }

    [Fact]
    public async Task Operations_with_path_like_name_do_not_touch_other_folders()
    {
        var storage = new ModuleStorage(_root, persistent: true);
        var host = new ModuleHost(storage);
        await InstallAsync(host);
        Directory.CreateDirectory(storage.StagingDir);
        File.WriteAllText(Path.Combine(storage.StagingDir, "keep.txt"), "x");
        Directory.CreateDirectory(Path.Combine(storage.ModulesDir, "bad name", "current")); // чужая папка не считается модулем
        File.WriteAllText(Path.Combine(storage.ModulesDir, "bad name", "current", "module.json"), "{}");

        foreach (var name in new[] { "..", "../staging", "../../" + Path.GetFileName(_root), "", "a/b" })
        {
            await Assert.ThrowsAsync<HostException>(() => host.RemoveAsync(name, "test"));
            await Assert.ThrowsAsync<HostException>(() => host.SetEnabledAsync(name, false, "test"));
            await Assert.ThrowsAsync<HostException>(() => host.ReloadAsync(name, "test"));
            await Assert.ThrowsAsync<HostException>(() => host.RollbackAsync(name, "test"));
        }

        Assert.True(File.Exists(Path.Combine(storage.StagingDir, "keep.txt")));
        Assert.Equal(new[] { "Sample" }, storage.InstalledNames().ToArray());
        Assert.Single(host.GetState().Modules);
    }

    [Fact]
    public async Task Rollback_swaps_with_previous_and_fails_without_it()
    {
        var host = new ModuleHost(new ModuleStorage(_root, persistent: true));
        var (first, _) = await InstallAsync(host);
        Assert.Null(first.PreviousVersion);
        await Assert.ThrowsAsync<HostException>(() => host.RollbackAsync("Sample", "test"));

        await InstallAsync(host);
        var (rolledBack, _) = await host.RollbackAsync("Sample", "test");

        Assert.True(rolledBack.Loaded, rolledBack.Error);
        Assert.NotNull(rolledBack.PreviousVersion); // прежняя current стала previous
    }

    [Fact]
    public async Task Remove_moves_module_to_trash()
    {
        var storage = new ModuleStorage(_root, persistent: true);
        var host = new ModuleHost(storage);
        await InstallAsync(host);

        await host.RemoveAsync("Sample", "test");

        Assert.False(Directory.Exists(storage.ModuleDir("Sample")));
        Assert.StartsWith("Sample-", Path.GetFileName(Assert.Single(Directory.GetDirectories(storage.TrashDir))));
    }

    [Fact]
    public void CleanupStaging_removes_only_old_uploads()
    {
        var storage = new ModuleStorage(_root, persistent: true);
        var old = Directory.CreateDirectory(Path.Combine(storage.StagingDir, "old")).FullName;
        var fresh = Directory.CreateDirectory(Path.Combine(storage.StagingDir, "fresh")).FullName;
        Directory.SetCreationTimeUtc(old, DateTime.UtcNow.AddHours(-1));

        storage.CleanupStaging(TimeSpan.FromMinutes(30));

        Assert.False(Directory.Exists(old));
        Assert.True(Directory.Exists(fresh));
    }

    [Fact]
    public void BeginUpload_rejects_empty_and_oversized_archives()
    {
        var host = new ModuleHost(new ModuleStorage(_root, persistent: true));

        Assert.Throws<HostException>(() => host.BeginUpload("x.zip", 0));
        Assert.Throws<HostException>(() => host.BeginUpload("x.zip", 200L * 1024 * 1024 + 1));
        Assert.NotEmpty(host.BeginUpload("x.zip", 200L * 1024 * 1024));
    }

    [Fact]
    public void Stats_error_message_is_truncated()
    {
        Assert.Equal("abc", ServerStats.Truncate("abc"));
        Assert.Equal(1000, ServerStats.Truncate(new string('x', 5000)).Length);
    }

    [Fact]
    public async Task Install_HotSwap_Disable_Restart_Remove()
    {
        var host = new ModuleHost(new ModuleStorage(_root, persistent: true));
        host.EnsureStarted();
        Assert.Equal("Host.Slot00", host.SlotName(0));

        // Первая установка: новое имя занимает слот / нужна перепривязка автоматизаций.
        var (module, rebind) = await InstallAsync(host);
        Assert.True(module.Loaded, module.Error);
        Assert.True(rebind);
        Assert.Equal("1.2.3", module.Version);
        Assert.Equal("Sample", host.SlotName(0));
        Assert.Contains("1.2.3", await RunSlot(host, 0));

        // Повторная установка той же активности - горячая замена, слот тот же, перепривязка не нужна.
        (module, rebind) = await InstallAsync(host);
        Assert.False(rebind);
        Assert.Equal("1.2.3", module.PreviousVersion);
        Assert.Equal("Sample", host.SlotName(0));
        Assert.Contains("1.2.3", await RunSlot(host, 0));

        // Отключение: слот сохраняет имя (сценарии привязаны), но вызов даёт понятную ошибку.
        await host.SetEnabledAsync("Sample", false, "test");
        Assert.Equal("Sample", host.SlotName(0));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => RunSlot(host, 0));
        Assert.Contains("отключён", error.Message);
        await host.SetEnabledAsync("Sample", true, "test");

        // «Перезапуск сервера»: новый хост на том же хранилище поднимает модуль сам.
        var restarted = new ModuleHost(new ModuleStorage(_root, persistent: true));
        Assert.Equal("Sample", restarted.SlotName(0));
        Assert.Contains("1.2.3", await RunSlot(restarted, 0));
        var state = restarted.GetState();
        var loaded = Assert.Single(state.Modules);
        Assert.Equal("test", loaded.InstalledBy);
        Assert.Contains(state.Journal, j => j.Text.Contains("загружен"));

        await restarted.RemoveAsync("Sample", "test");
        Assert.Empty(restarted.GetState().Modules);
    }

    [Fact]
    public async Task Validate_RejectsActivityOwnedByAnotherModule()
    {
        var host = new ModuleHost(new ModuleStorage(_root, persistent: true));
        await InstallAsync(host);

        // Тот же модуль под другим именем главной сборки - активность Sample уже занята.
        var renamed = Rezip(SampleZip(), "Sample.servext.dll", "Other.servext.dll");
        var id = host.BeginUpload("other.zip", renamed.Length);
        host.AppendChunk(id, renamed);
        var report = await host.ValidateAsync(id, stripServerAssemblies: true);

        Assert.False(report.CanInstall);
        Assert.Contains(report.Errors, e => e.Contains("Sample") && e.Contains("Sample"));
    }

    [Fact]
    public async Task State_lists_module_assemblies_with_versions()
    {
        var host = new ModuleHost(new ModuleStorage(_root, persistent: true));
        var (module, _) = await InstallAsync(host);

        var main = Assert.Single(module.Assemblies, a => a.Name == "Sample.servext");
        Assert.Equal("1.2.3.0", main.Version);
        Assert.DoesNotContain(module.Assemblies, a => a.Name == "Newtonsoft.Json"); // серверная - выброшена при установке
    }

    [Fact]
    public void Missing_library_is_loaded_from_another_module_folder()
    {
        // Режим «общий модуль»: в папке модуля только своя сборка, библиотека - в папке другого модуля.
        var sample = Path.Combine(_root, "shared");
        Directory.CreateDirectory(sample);
        using (var zip = new ZipArchive(new MemoryStream(SampleZip())))
            zip.GetEntry("Sample.servext.dll")!.ExtractToFile(Path.Combine(sample, "Sample.servext.dll"));
        var own = Path.Combine(_root, "own");
        Directory.CreateDirectory(own);
        var server = new ServerAssemblies(Array.Empty<(string, Version?)>());

        var without = new ModuleLoadContext("own", own, server);
        Assert.Throws<FileNotFoundException>(() => without.LoadFromAssemblyName(new System.Reflection.AssemblyName("Sample.servext")));
        without.Unload();

        var with = new ModuleLoadContext("own", own, server, name => Path.Combine(sample, name + ".dll") is var p && File.Exists(p) ? p : null);
        Assert.Equal("Sample.servext", with.LoadFromAssemblyName(new System.Reflection.AssemblyName("Sample.servext")).GetName().Name);
        with.Unload();
    }

    // ------------------------------------------------------------------ кластер: несколько узлов над одним каталогом

    /// <summary>Узел кластера: свой ModuleHost и свой ModuleStorage над общим каталогом.</summary>
    private ModuleHost Node(Action<ModuleStorage>? configure = null)
    {
        var storage = new ModuleStorage(_root, persistent: true);
        configure?.Invoke(storage);
        return new ModuleHost(storage) { SyncThrottle = TimeSpan.Zero };
    }

    private static ModuleState Only(ModuleHost host) => Assert.Single(host.GetState().Modules);

    [Fact]
    public async Task Upload_steps_can_run_on_different_nodes()
    {
        var a = Node();
        var b = Node();
        var zip = SampleZip();

        var id = a.BeginUpload("sample.zip", zip.Length);
        foreach (var chunk in zip.Chunk(10_000))
            b.AppendChunk(id, chunk);
        var report = await b.ValidateAsync(id, stripServerAssemblies: true);
        Assert.True(report.CanInstall, string.Join("; ", report.Errors));
        var (module, _) = await a.InstallAsync(id, "test", "тест");

        Assert.True(module.Loaded, module.Error);
        Assert.Equal("1.2.3", Only(a).Version);
        Assert.False(Directory.Exists(Path.Combine(_root, "staging", id))); // загрузка убрана после установки
        await Assert.ThrowsAsync<HostException>(() => b.InstallAsync(id, "test", null)); // второй раз - уже нечего ставить
    }

    [Fact]
    public async Task Upload_size_is_checked_across_nodes()
    {
        var a = Node();
        var b = Node();
        var id = a.BeginUpload("x.zip", 10);

        b.AppendChunk(id, new byte[6]);
        Assert.Throws<HostException>(() => a.AppendChunk(id, new byte[5])); // 11 > 10
        var incomplete = await Assert.ThrowsAsync<HostException>(() => a.ValidateAsync(id, true));
        Assert.Contains("не полностью", incomplete.Message);
        a.AppendChunk(id, new byte[4]);
        Assert.Throws<HostException>(() => b.AppendChunk(id, new byte[1]));
    }

    [Fact]
    public async Task Install_on_one_node_reaches_another()
    {
        var a = Node();
        var b = Node();
        b.EnsureStarted();

        await InstallAsync(a);
        b.SyncNow();

        Assert.Equal("1.2.3", Only(b).Version);
        Assert.Equal("Sample", b.SlotName(0));
        Assert.Contains("1.2.3", await RunSlot(b, 0));
        Assert.Contains(b.GetState().Journal, j => j.Text.Contains("синхронизация: Sample 1.2.3 загружен"));
    }

    [Fact]
    public async Task Node_does_not_reload_itself_after_own_operation()
    {
        var a = Node();
        await InstallAsync(a);
        var loadedAt = Only(a).LoadedAt;

        a.SyncNow();

        Assert.Equal(loadedAt, Only(a).LoadedAt);
        Assert.DoesNotContain(a.GetState().Journal, j => j.Text.StartsWith("синхронизация"));
    }

    [Fact]
    public async Task SetEnabled_reload_rollback_remove_reach_another_node()
    {
        var a = Node();
        var b = Node();
        b.EnsureStarted();
        await InstallAsync(a);
        await InstallAsync(a); // есть previous для отката
        b.SyncNow();
        Assert.True(Only(b).Loaded);

        await a.SetEnabledAsync("Sample", false, "test");
        b.SyncNow();
        Assert.False(Only(b).Enabled);
        Assert.Contains("отключён", (await Assert.ThrowsAsync<InvalidOperationException>(() => RunSlot(b, 0))).Message);
        await a.SetEnabledAsync("Sample", true, "test");
        b.SyncNow();
        Assert.True(Only(b).Loaded);
        Assert.Contains("1.2.3", await RunSlot(b, 0));

        var loadedAt = Only(b).LoadedAt;
        await a.ReloadAsync("Sample", "test");
        b.SyncNow();
        Assert.True(Only(b).LoadedAt > loadedAt, "reload должен дойти до другого узла");

        loadedAt = Only(b).LoadedAt;
        await a.RollbackAsync("Sample", "test");
        b.SyncNow();
        Assert.True(Only(b).LoadedAt > loadedAt, "rollback должен дойти до другого узла");

        await a.RemoveAsync("Sample", "test");
        b.SyncNow();
        Assert.Empty(b.GetState().Modules);
        Assert.Contains(b.GetState().Journal, j => j.Text.Contains("синхронизация: Sample") && j.Text.Contains("выгружен"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => RunSlot(b, 0));
    }

    [Fact]
    public async Task Operation_on_stale_node_does_not_lose_other_nodes_change()
    {
        var a = Node();
        var b = Node();
        b.EnsureStarted();
        await InstallAsync(a);

        // B ещё не сверился, но перед своей операцией догоняет каталог.
        await b.SetEnabledAsync("Sample", false, "test");
        Assert.False(Only(b).Enabled);
        a.SyncNow();

        Assert.False(Only(a).Enabled);
    }

    [Fact]
    public async Task Slot_name_picks_up_new_module_without_explicit_sync()
    {
        var a = Node();
        var b = Node();
        b.EnsureStarted();
        await InstallAsync(a);

        Assert.Equal("Sample", b.SlotName(0));
        Assert.Contains("1.2.3", await RunSlot(b, 0));
    }

    [Fact]
    public async Task Opportunistic_sync_does_not_wait_for_busy_cluster_lock()
    {
        var a = Node();
        var b = Node(s => s.LockTimeout = TimeSpan.FromMinutes(5));
        b.EnsureStarted();
        await InstallAsync(a);
        File.WriteAllText(Path.Combine(_root, ModuleStorage.LockFileName), "other-node");

        var watch = System.Diagnostics.Stopwatch.StartNew();
        Assert.Equal("Host.Slot00", b.SlotName(0));
        Assert.Empty(b.GetState().Modules);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), "SlotName/GetState не должны ждать замок");

        File.Delete(Path.Combine(_root, ModuleStorage.LockFileName));
        Assert.Equal("Sample", b.SlotName(0));
    }

    [Fact]
    public async Task Manifest_without_revision_is_read_and_not_reloaded()
    {
        var a = Node();
        await InstallAsync(a);
        var manifestPath = Path.Combine(_root, "modules", "Sample", "current", "module.json");
        var legacy = System.Text.RegularExpressions.Regex.Replace(File.ReadAllText(manifestPath), ",\\s*\"revision\":\\s*\"[^\"]*\"", "");
        Assert.DoesNotContain("revision", legacy);
        File.WriteAllText(manifestPath, legacy);

        var b = Node();
        b.EnsureStarted();
        var loadedAt = Only(b).LoadedAt;
        Assert.True(Only(b).Loaded);
        new ModuleStorage(_root, true).NewGeneration(); // «кто-то изменил каталог»
        b.SyncNow();

        Assert.Equal(loadedAt, Only(b).LoadedAt);
    }

    [Fact]
    public async Task Starting_node_keeps_fresh_upload_and_removes_stale_one()
    {
        var a = Node();
        var zip = SampleZip();
        var fresh = a.BeginUpload("sample.zip", zip.Length);
        var storage = new ModuleStorage(_root, persistent: true);
        var stale = Guid.NewGuid().ToString("N");
        storage.CreateUpload(stale, new UploadMeta { FileName = "old.zip", Size = 1, CreatedAt = DateTime.UtcNow.AddHours(-1) });

        var b = Node();
        b.EnsureStarted();

        Assert.True(Directory.Exists(storage.UploadDir(fresh)));
        Assert.False(Directory.Exists(storage.UploadDir(stale)));
        // идущая загрузка продолжается на стартовавшем узле
        b.AppendChunk(fresh, zip);
        Assert.True((await b.ValidateAsync(fresh, true)).CanInstall);
    }

    [Theory]
    [InlineData("..")]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("../../../../../../../../../../tmp")]
    [InlineData("..\\..\\..\\..\\..\\..\\..\\..\\..\\..\\..\\..")]
    [InlineData("0123456789abcdef0123456789abcde/")]
    [InlineData("0123456789abcdef0123456789abcdeg")]
    [InlineData("0123456789abcdef0123456789abcdef0")]
    public async Task Invalid_upload_id_is_rejected_before_touching_disk(string id)
    {
        var host = Node();
        host.EnsureStarted();
        var before = Directory.GetFileSystemEntries(_root, "*", SearchOption.AllDirectories).OrderBy(x => x).ToArray();

        Assert.Throws<HostException>(() => host.AppendChunk(id, new byte[] { 1 }));
        await Assert.ThrowsAsync<HostException>(() => host.ValidateAsync(id, true));
        await Assert.ThrowsAsync<HostException>(() => host.InstallAsync(id, "test", null));

        Assert.Equal(before, Directory.GetFileSystemEntries(_root, "*", SearchOption.AllDirectories).OrderBy(x => x).ToArray());
    }

    [Fact]
    public async Task Abandoned_cluster_lock_is_taken_over_and_released()
    {
        var host = Node(s => s.LockStaleAfter = TimeSpan.FromMinutes(2));
        await InstallAsync(host);
        var lockPath = Path.Combine(_root, ModuleStorage.LockFileName);
        File.WriteAllText(lockPath, "dead-node");
        File.SetLastWriteTimeUtc(lockPath, DateTime.UtcNow.AddMinutes(-10));

        var (module, _) = await host.ReloadAsync("Sample", "test");

        Assert.True(module.Loaded, module.Error);
        Assert.False(File.Exists(lockPath), "замок должен сниматься после операции");
    }

    [Fact]
    public async Task Busy_cluster_lock_gives_clear_error_after_timeout()
    {
        var host = Node(s => s.LockTimeout = TimeSpan.FromMilliseconds(300));
        await InstallAsync(host);
        var lockPath = Path.Combine(_root, ModuleStorage.LockFileName);
        File.WriteAllText(lockPath, "live-node");

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var error = await Assert.ThrowsAsync<HostException>(() => host.ReloadAsync("Sample", "test"));

        Assert.Contains("занят", error.Message);
        Assert.Contains("live-node", error.Message);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10));
        Assert.Equal("live-node", File.ReadAllText(lockPath)); // чужой замок не тронут
        File.Delete(lockPath);
        Assert.True((await host.ReloadAsync("Sample", "test")).Module.Loaded);
    }

    [Fact]
    public async Task Stale_lock_that_cannot_be_taken_over_times_out_instead_of_spinning()
    {
        var storage = new ModuleStorage(_root, persistent: true);
        var lockPath = Path.Combine(_root, ModuleStorage.LockFileName);
        File.WriteAllText(lockPath, "dead-node");
        File.SetLastWriteTimeUtc(lockPath, DateTime.UtcNow.AddMinutes(-10));
        using var holder = new FileStream(lockPath, FileMode.Open, FileAccess.Read, FileShare.None); // Windows: такой файл не переместить

        var attempt = Task.Run(() => storage.TryAcquireLock(TimeSpan.FromMilliseconds(300)));

        Assert.Same(attempt, await Task.WhenAny(attempt, Task.Delay(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken)));
        (await attempt)?.Dispose(); // на Linux файл можно переместить, и замок перехватывается
    }

    [Fact]
    public void Held_cluster_lock_is_renewed_so_long_operation_is_not_taken_over()
    {
        var storage = new ModuleStorage(_root, persistent: true) { LockStaleAfter = TimeSpan.FromSeconds(1) };
        var lockPath = Path.Combine(_root, ModuleStorage.LockFileName);
        using var held = storage.TryAcquireLock(TimeSpan.Zero);
        Assert.NotNull(held);
        File.SetLastWriteTimeUtc(lockPath, DateTime.UtcNow.AddMinutes(-10)); // «давно не обновлялся»

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow - File.GetLastWriteTimeUtc(lockPath) > TimeSpan.FromSeconds(1) && DateTime.UtcNow < deadline)
            Thread.Sleep(50);

        Assert.Null(storage.TryAcquireLock(TimeSpan.Zero)); // не перехвачен
    }

    [Fact]
    public async Task Unreadable_generation_mark_does_not_throw()
    {
        var a = Node();
        await InstallAsync(a);
        var generation = Path.Combine(_root, "generation");
        File.Delete(generation);
        Directory.CreateDirectory(generation); // чтение даёт UnauthorizedAccessException, как у файла в состоянии delete-pending
        var b = Node();

        Assert.Null(new ModuleStorage(_root, persistent: true).ReadGeneration());
        b.EnsureStarted();
        b.SyncNow();

        Assert.Equal("Sample", b.SlotName(0));
    }

    [Fact]
    public async Task Missing_generation_mark_does_not_unload_modules()
    {
        var a = Node();
        var b = Node();
        await InstallAsync(a);
        b.EnsureStarted();
        Assert.True(Only(b).Loaded);
        // Том каталога подменён пустым: метки и манифестов нет.
        File.Delete(Path.Combine(_root, "generation"));
        File.Delete(Path.Combine(_root, "modules", "Sample", "current", "module.json"));

        b.SyncNow();

        Assert.True(Only(b).Loaded);
        Assert.DoesNotContain(b.GetState().Journal, j => j.Text.Contains("выгружен"));
    }

    [Fact]
    public async Task Failed_operation_triggers_resync_of_this_node()
    {
        var a = Node();
        await InstallAsync(a);
        var trash = Path.Combine(_root, "trash");
        File.WriteAllText(trash, "не каталог"); // MoveToTrash упадёт уже после выгрузки модуля из памяти

        await Assert.ThrowsAsync<IOException>(() => a.RemoveAsync("Sample", "test"));
        File.Delete(trash);
        a.SyncNow();

        Assert.True(Only(a).Loaded, "модуль остался на диске, узел должен его вернуть");
    }

    [Fact]
    public async Task Corrupt_manifest_of_one_module_does_not_block_others_or_its_removal()
    {
        var a = Node();
        await InstallAsync(a);
        var broken = Path.Combine(_root, "modules", "A.Broken", "current");
        Directory.CreateDirectory(broken);
        File.WriteAllText(Path.Combine(broken, "module.json"), "{не json");

        var b = Node();
        b.EnsureStarted(); // A.Broken идёт раньше по алфавиту
        Assert.True(Only(b).Loaded);
        new ModuleStorage(_root, persistent: true).NewGeneration();
        b.SyncNow(); // не бросает: сломанный модуль пропускается
        Assert.Contains(b.GetState().Journal, j => j.Level == "error" && j.Text.Contains("A.Broken"));

        await b.RemoveAsync("A.Broken", "test");

        Assert.False(Directory.Exists(Path.Combine(_root, "modules", "A.Broken")));
        Assert.True(Only(b).Loaded);
    }

    [Fact]
    public async Task GetState_survives_corrupt_previous_manifest()
    {
        var a = Node();
        await InstallAsync(a);
        await InstallAsync(a);
        File.WriteAllText(Path.Combine(_root, "modules", "Sample", "previous", "module.json"), "{не json");

        var module = Only(a);

        Assert.True(module.Loaded);
        Assert.Null(module.PreviousVersion);
    }

    [Fact]
    public void BeginUpload_cleanup_waits_for_free_cluster_lock()
    {
        var host = Node();
        var storage = new ModuleStorage(_root, persistent: true);
        var stale = Guid.NewGuid().ToString("N");
        storage.CreateUpload(stale, new UploadMeta { FileName = "old.zip", Size = 1, CreatedAt = DateTime.UtcNow.AddHours(-1) });
        var lockPath = Path.Combine(_root, ModuleStorage.LockFileName);
        File.WriteAllText(lockPath, "other-node"); // идёт чужая операция (распаковка, установка)

        host.BeginUpload("x.zip", 1);
        Assert.True(Directory.Exists(storage.UploadDir(stale)), "под чужим замком уборка не выполняется");

        File.Delete(lockPath);
        host.BeginUpload("x.zip", 1);
        Assert.False(Directory.Exists(storage.UploadDir(stale)));
    }

    [Fact]
    public async Task Interrupted_revalidation_does_not_leave_upload_installable()
    {
        if (!OperatingSystem.IsWindows())
            return; // обрыв распаковки имитируется открытым файлом, который нельзя удалить и перезаписать
        var host = Node();
        var zip = SampleZip();
        var id = host.BeginUpload("sample.zip", zip.Length);
        host.AppendChunk(id, zip);
        Assert.True((await host.ValidateAsync(id, stripServerAssemblies: true)).CanInstall);
        var extracted = Path.Combine(_root, "staging", id, "module", "Sample.servext.dll");

        using (new FileStream(extracted, FileMode.Open, FileAccess.Read, FileShare.None))
            await Assert.ThrowsAsync<IOException>(() => host.ValidateAsync(id, stripServerAssemblies: true));

        await Assert.ThrowsAsync<HostException>(() => host.InstallAsync(id, "test", null));
    }

    // ------------------------------------------------------------------

    private static async Task<(ModuleState, bool)> InstallAsync(ModuleHost host)
    {
        var zip = SampleZip();
        var id = host.BeginUpload("sample.zip", zip.Length);
        foreach (var chunk in zip.Chunk(10_000))
            host.AppendChunk(id, chunk);
        var report = await host.ValidateAsync(id, stripServerAssemblies: true);
        Assert.True(report.CanInstall, string.Join("; ", report.Errors));
        Assert.Equal(new[] { "Sample" }, report.Activities);
        return await host.InstallAsync(id, "test", "тест");
    }

    private static async Task<string> RunSlot(ModuleHost host, int slot)
    {
        var context = new FakeContext();
        await host.RunAsync(slot, a => a.RunAsync(null!, null!, context, null!));
        return (string)context.Params["result"];
    }

    /// <summary>Архив модуля + Newtonsoft.Json: «лишняя» сборка, которая есть и в Pilot-Server; хост должен её найти и отбросить.</summary>
    private static byte[] SampleZip() => Zip(
        ("Sample.servext.dll", SampleModule.Assembly),
        ("Newtonsoft.Json.dll", File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Newtonsoft.Json.dll"))));

    private static byte[] Zip(params (string Path, byte[] Data)[] files)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (path, data) in files)
            {
                using var s = zip.CreateEntry(path).Open();
                s.Write(data);
            }
        return ms.ToArray();
    }

    private static byte[] Rezip(byte[] zip, string from, string to)
    {
        using var source = new ZipArchive(new MemoryStream(zip));
        return Zip(source.Entries.Select(e =>
        {
            using var s = e.Open();
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            return (e.FullName == from ? to : e.FullName, ms.ToArray());
        }).ToArray());
    }

    private sealed class FakeContext : IServerActivityContext
    {
        public T GetService<T>() where T : class => null!;
        public Dictionary<string, object> Params { get; } = new();
        public List<string> SourceTypes { get; } = new();
        public TargetType? Target => null;
        public RelationType? RelationType => null;
        public RelationFilterByChangeKind? RelationFilterByChangeKind => null;
        public Dictionary<string, string> Errors { get; } = new();
    }
}

