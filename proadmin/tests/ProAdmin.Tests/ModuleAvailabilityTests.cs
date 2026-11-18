using ProAdmin.Infrastructure;
using ProAdmin.Jobs;
using ProAdmin.Modules;
using ProAdmin.Pilot;
using ProAdmin.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ProAdmin.Tests;

/// <summary>Отключение и удаление клиентских модулей: защита общих модулей, состояние на контуре, запрет выкладки.</summary>
public class ModuleAvailabilityTests : IDisposable
{
    private const string ModuleName = "Модуль отчётов - (OpenStoradge) - (CICD)";
    private static readonly ModuleEnv Test = new(ModuleEnv.Test, "Тест", "http://pilot", ModuleBranches.Dev);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "proadmin-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    private IOptions<AppOptions> Options => Microsoft.Extensions.Options.Options.Create(new AppOptions { DataDirectory = _dir });

    private DisabledModuleStore NewStore() => new(Options, NullLogger<DisabledModuleStore>.Instance);

    private static PilotModule Module(string name = ModuleName, string folder = "") =>
        new(Guid.NewGuid(), name, "Версия 1.2.1 - Размер 93 КБ", folder, Guid.NewGuid(), "m.zip", Guid.NewGuid(), 10, null, null);

    private static DisabledModule Off(PilotModule module) => new()
    {
        Env = ModuleEnv.Test,
        Id = module.Id,
        Name = module.Name,
        Additional = module.Additional,
        Reason = "падает при старте",
        By = "root",
    };

    private static ModulePackageInfo Package() =>
        new(new byte[] { 1 }, "114OpenStoradge.ext2.zip", "114OpenStoradge.ext2", "1.3.0.0", ModuleName);

    [Theory]
    [InlineData("Pilot.ClientSdk", "")]
    [InlineData("Клиентский SDK - (Pilot.ClientSdk) - (CICD)", "")]
    [InlineData("Любой", "Preload")]
    [InlineData("Любой", "Preload/Библиотеки")]
    public void Shared_and_preload_modules_are_protected(string name, string folder) =>
        Assert.NotNull(ModuleAvailabilityService.ProtectedReason(Module(name, folder), "Pilot.ClientSdk"));

    [Theory]
    [InlineData("Test-module", "")]
    [InlineData("Test-module", "Preloaded")]
    public void Ordinary_module_is_not_protected(string name, string folder) =>
        Assert.Null(ModuleAvailabilityService.ProtectedReason(Module(name, folder), "Pilot.ClientSdk"));

    [Fact]
    public void Store_survives_restart_and_keeps_envs_apart()
    {
        var module = Module();
        NewStore().Add(Off(module));

        var reopened = NewStore();
        Assert.Equal(module.Name, reopened.Find(ModuleEnv.Test, module.Id)?.Name);
        Assert.Equal("1.2.1", reopened.Find(ModuleEnv.Test, module.Id)?.Version);
        Assert.Empty(reopened.For(ModuleEnv.Prod));

        reopened.Remove(ModuleEnv.Test, module.Id);
        Assert.Empty(NewStore().For(ModuleEnv.Test));
    }

    [Fact]
    public void Broken_store_stops_the_start_instead_of_forgetting_disabled_modules()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "disabled-modules.json"), "{ не json");
        Assert.ThrowsAny<Exception>(() => NewStore());
    }

    [Fact]
    public void State_is_disabled_only_while_no_alive_object_exists()
    {
        var repo = new ModuleRepoState { Repo = new ProAdmin.Automations.AutomationRepo { Name = "OpenStoradge", ProjectPath = "m/o", Folder = "", WebUrl = "http://gitlab/m/o" } };
        var module = Module();
        var off = Off(module);

        Assert.Equal(ModuleSync.Disabled, ModuleCatalog.State(Test, repo, null, serverKnown: true, off).State);
        Assert.NotEqual(ModuleSync.Disabled, ModuleCatalog.State(Test, repo, module, serverKnown: true, off).State);
        Assert.Equal(ModuleSync.Unknown, ModuleCatalog.State(Test, repo, null, serverKnown: false, off).State);
    }

    [Fact]
    public void Disabled_module_does_not_ask_for_deploy() => Assert.True(ModuleNextSteps.Ok(ModuleSync.Disabled));

    [Fact]
    public async Task Deploy_of_disabled_module_is_refused_and_creates_nothing()
    {
        var store = NewStore();
        store.Add(Off(Module()));
        var pilot = new FakeTarget();

        var ex = await Assert.ThrowsAsync<WorkflowException>(() => DeployAsync(pilot, store));

        Assert.Contains("отключён", ex.Message);
        Assert.Contains("падает при старте", ex.Message);
        Assert.Equal(0, pilot.Created);
        Assert.Equal(0, pilot.Updated);
    }

    [Fact]
    public async Task Object_restored_in_pilot_is_updated_despite_stale_record()
    {
        var store = NewStore();
        var alive = Module();
        store.Add(Off(alive));
        var pilot = new FakeTarget(alive);

        await DeployAsync(pilot, store);

        Assert.Equal(1, pilot.Updated);
        Assert.Equal(0, pilot.Created);
    }

    [Fact]
    public async Task Other_modules_deploy_as_usual()
    {
        var store = NewStore();
        store.Add(Off(Module("Другой модуль - (Other) - (CICD)")));
        var pilot = new FakeTarget();

        await DeployAsync(pilot, store);

        Assert.Equal(1, pilot.Created);
    }

    [Theory]
    [InlineData("не то имя", "причина")]
    [InlineData(null, "причина")]
    [InlineData(ModuleName, " ")]
    public async Task Delete_needs_exact_name_and_reason(string? confirm, string reason)
    {
        var store = NewStore();
        var off = Off(Module());
        store.Add(off);
        var service = Service(store);

        var error = await service.DeleteAsync(Test, new PilotLogin("l", "p"), 100, "m", off, confirm, reason, new ModuleActor("l", null), CancellationToken.None);

        Assert.NotNull(error);
        Assert.NotNull(store.Find(ModuleEnv.Test, off.Id));
    }

    [Fact]
    public async Task Disable_needs_reason_and_refuses_shared_module()
    {
        var store = NewStore();
        var service = Service(store);
        var login = new PilotLogin("l", "p");
        var actor = new ModuleActor("l", null);

        Assert.NotNull(await service.DisableAsync(Test, login, 100, "m", Module(), "  ", actor, CancellationToken.None));
        Assert.NotNull(await service.DisableAsync(Test, login, 100, "m", Module("Любой", "Preload"), "причина", actor, CancellationToken.None));
        Assert.Empty(store.For(ModuleEnv.Test));
    }

    private ModuleAvailabilityService Service(DisabledModuleStore store)
    {
        var sessions = new PilotReportGateway(new NetworkAddressMapper(null), NullLogger<PilotReportGateway>.Instance);
        return new ModuleAvailabilityService(new PilotModuleGateway(sessions), store,
            new AssemblySpecStore(Options, NullLogger<AssemblySpecStore>.Instance), new JobStore(Options, NullLogger<JobStore>.Instance));
    }

    private Task DeployAsync(FakeTarget pilot, DisabledModuleStore store)
    {
        var ctx = new ModuleContext
        {
            Settings = new AdminSettings(),
            GitLabToken = null!,
            ProjectPath = "m/openstoradge",
            RepoName = "OpenStoradge",
            Logins = new Dictionary<string, PilotLogin> { [ModuleEnv.Test] = new("l", "p") },
            Disabled = store,
        };
        return new ModuleCiDeployWorkflow(pilot, new PublishJob(), ctx, Test, "dev", "abcdef1234", Package(), Array.Empty<string>())
            .RunAsync(CancellationToken.None);
    }

    private sealed class FakeTarget : IModuleTarget
    {
        private readonly PilotModule[] _modules;

        public FakeTarget(params PilotModule[] modules) => _modules = modules;

        public int Created { get; private set; }
        public int Updated { get; private set; }
        public ModuleKind Kind => ModuleKind.Client;
        public string AfterUpdateNote => "";

        public Task<PilotModuleList> ListAsync(string url, PilotLogin login, int licenseType, CancellationToken ct)
        {
            var list = new PilotModuleList { RootFolderId = Guid.NewGuid() };
            list.Modules.AddRange(_modules);
            return Task.FromResult(list);
        }

        public Task<byte[]?> DownloadAsync(string url, PilotLogin login, int licenseType, PilotModule module, CancellationToken ct) => Task.FromResult<byte[]?>(new byte[] { 1 });

        public Task UpdateAsync(string url, PilotLogin login, int licenseType, Guid moduleId, ModulePackageInfo package, string reason, CancellationToken ct)
        {
            Updated++;
            return Task.CompletedTask;
        }

        public Task<Guid> CreateAsync(string url, PilotLogin login, int licenseType, Guid folderId, ModulePackageInfo package, string reason, CancellationToken ct)
        {
            Created++;
            return Task.FromResult(Guid.NewGuid());
        }

        public Task RestoreAsync(string url, PilotLogin login, int licenseType, PilotModule previous, byte[] zip, string reason, CancellationToken ct) => Task.CompletedTask;

        public Task MoveToRecycleBinAsync(string url, PilotLogin login, int licenseType, Guid moduleId, CancellationToken ct) => Task.CompletedTask;

        public Task<IReadOnlyList<PackageAssembly>?> AssembliesAsync(string url, PilotLogin login, int licenseType, PilotModule module, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<PackageAssembly>?>(null);

        public string? AfterCreateWarning(string extensionName, string envTitle) => null;
    }
}

/// <summary>
/// Весь путь на настоящем сервере Pilot: временный модуль создаётся, отключается, включается и удаляется окончательно.
/// Запускается, если заданы PILOT_IT_WRITE_URL (только тестовый контур!), PILOT_IT_LOGIN, PILOT_IT_PASSWORD, PILOT_IT_LICENSE.
/// </summary>
public class ModuleAvailabilityIntegrationTests
{
    [RequiresEnvFact("PILOT_IT_WRITE_URL", "PILOT_IT_LOGIN", "PILOT_IT_PASSWORD")]
    public async Task Disable_enable_delete_on_real_server()
    {
        var url = Environment.GetEnvironmentVariable("PILOT_IT_WRITE_URL")!;

        var login = new PilotLogin(Environment.GetEnvironmentVariable("PILOT_IT_LOGIN")!, Environment.GetEnvironmentVariable("PILOT_IT_PASSWORD")!);
        var license = int.TryParse(Environment.GetEnvironmentVariable("PILOT_IT_LICENSE"), out var l) ? l : 100;
        var ct = CancellationToken.None;
        var gateway = new PilotModuleGateway(new PilotReportGateway(new NetworkAddressMapper(null), NullLogger<PilotReportGateway>.Instance));

        var name = "IT-" + Guid.NewGuid().ToString("N")[..8];
        var package = new ModulePackageInfo(new byte[] { 0x50, 0x4B, 5, 6, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 },
            name + ".ext2.zip", name + ".ext2", "0.0.1.0", name);

        var before = await gateway.ListAsync(url, login, license, ct);
        var id = await gateway.CreateAsync(url, login, license, before.RootFolderId!.Value, package, "проверка отключения модулей", ct);
        var deleted = false;
        try
        {
            Assert.Contains(await List(), m => m.Id == id);

            await gateway.MoveToRecycleBinAsync(url, login, license, id, ct);
            Assert.DoesNotContain(await List(), m => m.Id == id);

            Assert.Equal(RecycledModuleState.Done, await gateway.RestoreFromRecycleBinAsync(url, login, license, id, Guid.Empty, ct));
            var restored = (await List()).Single(m => m.Id == id);
            Assert.Equal(before.RootFolderId, restored.FolderId);

            // Живой объект не удаляется и не «восстанавливается» повторно.
            Assert.Equal(RecycledModuleState.Alive, await gateway.DeletePermanentlyAsync(url, login, license, id, ct));
            Assert.Equal(RecycledModuleState.Alive, await gateway.RestoreFromRecycleBinAsync(url, login, license, id, Guid.Empty, ct));
            Assert.Contains(await List(), m => m.Id == id);

            await gateway.MoveToRecycleBinAsync(url, login, license, id, ct);
            Assert.Equal(RecycledModuleState.Done, await gateway.DeletePermanentlyAsync(url, login, license, id, ct));
            deleted = true;
            Assert.Equal(RecycledModuleState.Gone, await gateway.DeletePermanentlyAsync(url, login, license, id, ct));
            Assert.Equal(RecycledModuleState.Gone, await gateway.RestoreFromRecycleBinAsync(url, login, license, id, Guid.Empty, ct));
            Assert.DoesNotContain(await List(), m => m.Id == id);
        }
        finally
        {
            if (!deleted)
            {
                // Упавшая проверка не оставляет модуль в папке расширений: никому не назначен, но в корзине ему и место.
                try { await gateway.MoveToRecycleBinAsync(url, login, license, id, ct); } catch (PilotGatewayException) { }
            }
        }

        async Task<List<PilotModule>> List() => (await gateway.ListAsync(url, login, license, ct)).Modules;
    }
}
