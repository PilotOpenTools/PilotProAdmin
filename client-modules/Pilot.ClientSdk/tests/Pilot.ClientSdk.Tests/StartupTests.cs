using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Pilot.ClientSdk;
using Pilot.ClientSdk.Infrastructure;
using Pilot.ClientSdk.Startup;
using Xunit;

namespace Pilot.ClientSdk.Tests
{
    [Collection("Startup")]
    public class StartupTests
    {
        private sealed class OkModule : IModule
        {
            public bool Called;
            public void Initialize() => Called = true;
        }

        private sealed class FailingModule : IModule
        {
            public void Initialize() => throw new InvalidOperationException("нет типа 0_project");
        }

        private sealed class AsyncModule : IModuleAsync
        {
            public readonly TaskCompletionSource<bool> Gate = new TaskCompletionSource<bool>();

            public async Task InitializeAsync()
            {
                if (!await Gate.Task)
                    throw new TimeoutException("сервер не ответил");
            }
        }

        private static ModuleStartupReport ReportOf(Type type) => StartupRunner.Reports.Single(r => r.Part == type.FullName);

        [Fact]
        public void Run_records_success_and_swallows_errors()
        {
            StartupRunner.Reset();
            var ok = new OkModule();
            StartupRunner.Run(ok);
            StartupRunner.Run(new FailingModule());   // не бросает

            Assert.True(ok.Called);
            Assert.Equal(ModuleStartupStatus.Success, ReportOf(typeof(OkModule)).Status);
            var failed = ReportOf(typeof(FailingModule));
            Assert.Equal(ModuleStartupStatus.Error, failed.Status);
            Assert.Equal("нет типа 0_project", failed.Error.Message);
            Assert.Equal("Pilot.ClientSdk.Tests", failed.Module);
        }

        [Fact]
        public async Task RunAsync_is_running_until_task_completes_and_never_faults()
        {
            StartupRunner.Reset();
            var module = new AsyncModule();
            var task = StartupRunner.RunAsync(module);
            Assert.Equal(ModuleStartupStatus.Running, ReportOf(typeof(AsyncModule)).Status);

            module.Gate.SetResult(false);
            await task;   // не падает, хотя InitializeAsync упал

            var report = ReportOf(typeof(AsyncModule));
            Assert.Equal(ModuleStartupStatus.Error, report.Status);
            Assert.IsType<TimeoutException>(report.Error);
        }

        // ------------------------------------------------------------------ ModuleCheck

        private static readonly Guid FileA = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
        private static readonly Guid FileB = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");

        private static List<RowResult> Check(
            IReadOnlyList<ExpectedModule> expected,
            IReadOnlyList<LoadedModule> loaded,
            IReadOnlyList<ModuleStartupReport> reports = null,
            bool deadline = false,
            params Guid[] downloaded)
        {
            return ModuleCheck.Evaluate(expected,
                id => downloaded.Contains(id) ? @"C:\Plugins\" + id : null,
                folder => folder.EndsWith(FileA.ToString()) ? new[] { "087OpenStoradge.ext2" } : new[] { "Other.ext2" },
                loaded, reports ?? Array.Empty<ModuleStartupReport>(), deadline);
        }

        private static ModuleStartupReport Report(string module, Exception error = null, bool running = false)
        {
            var r = new ModuleStartupReport(module, module + ".StartPoint");
            if (!running)
                r.Finish(error);
            return r;
        }

        [Fact]
        public void Not_downloaded_module_is_an_error_immediately()
        {
            var rows = Check(new[] { new ExpectedModule(Guid.NewGuid(), "Модуль А", "1.0.0", FileA) }, new LoadedModule[0]);
            Assert.Equal(RowState.Error, rows.Single().State);
            Assert.Equal("не скачан", rows.Single().Status);
        }

        [Fact]
        public void Downloaded_but_not_loaded_waits_until_deadline()
        {
            var expected = new[] { new ExpectedModule(Guid.NewGuid(), "Модуль А", "1.0.0", FileA) };
            Assert.Equal(RowState.Waiting, Check(expected, new LoadedModule[0], downloaded: FileA).Single().State);
            Assert.Equal(RowState.Error, Check(expected, new LoadedModule[0], deadline: true, downloaded: FileA).Single().State);
        }

        [Fact]
        public void Loaded_modules_are_matched_by_folder_or_dll_name()
        {
            var expected = new[]
            {
                new ExpectedModule(Guid.NewGuid(), "01.087 - Открыть Storadge", "1.0.1", FileA),
                new ExpectedModule(Guid.NewGuid(), "DevExpress", "1.0.0.0", FileB),
            };
            var loaded = new[]
            {
                // по имени сборки (Location не совпадает - например, теневая копия)
                new LoadedModule("087OpenStoradge.ext2", "01.087 - Открыть Storadge - (CICD)", "1.0.1.0", @"D:\shadow", true, true),
                // по папке
                new LoadedModule("Ascon.Pilot.Reports.DevExpress.ext2", "", "1.0.0.0", @"C:\Plugins\" + FileB, false, false),
            };

            var rows = Check(expected, loaded, new[] { Report("087OpenStoradge.ext2") }, downloaded: new[] { FileA, FileB });

            var storadge = rows.Single(r => r.Key == FileA.ToString());
            Assert.Equal(RowState.Ok, storadge.State);
            Assert.Equal("01.087 - Открыть Storadge - (CICD)", storadge.Title);
            Assert.NotNull(storadge.DurationMs);
            Assert.Equal(RowState.Ok, rows.Single(r => r.Key == FileB.ToString()).State);   // сторонний: просто «загружен»
            Assert.Equal(2, rows.Count);
        }

        [Fact]
        public void module_statuses_follow_the_contract()
        {
            LoadedModule Own(string name, bool contract) => new LoadedModule(name, name, "1.0.0.0", null, true, contract);

            var noContract = Check(null, new[] { Own("A.ext2", false) }).Single();
            Assert.Equal(RowState.Warning, noContract.State);

            Assert.Equal(RowState.Waiting, Check(null, new[] { Own("B.ext2", true) }).Single().State);
            Assert.Equal(RowState.Warning, Check(null, new[] { Own("B.ext2", true) }, deadline: true).Single().State);

            var running = Check(null, new[] { Own("C.ext2", true) }, new[] { Report("C.ext2", running: true) }, deadline: true).Single();
            Assert.Equal(RowState.Running, running.State);
            Assert.False(running.IsDone);

            var failed = Check(null, new[] { Own("D.ext2", true) }, new[] { Report("D.ext2", new InvalidOperationException("упал")) }).Single();
            Assert.Equal(RowState.Error, failed.State);
            Assert.Contains("упал", failed.Details);
        }

        [Fact]
        public void Local_and_unknown_modules_get_their_own_rows()
        {
            var expected = new[] { new ExpectedModule(Guid.NewGuid(), "Модуль А", "1.0.0", FileA) };
            var loaded = new[]
            {
                new LoadedModule("087OpenStoradge.ext2", "Открыть Storadge", "1.0.1.0", null, true, true),
                new LoadedModule("Dev.ext2", "Модуль из Development", "2.0.0.0", @"C:\Development\Dev.ext2", true, false),
                new LoadedModule("Foreign.ext2", "Чужой", "1.0.0.0", null, false, false),
            };
            var reports = new[] { Report("087OpenStoradge.ext2"), Report("NoCompany.ext2") };

            var rows = Check(expected, loaded, reports, downloaded: FileA);

            Assert.Equal(3, rows.Count);   // модуль с сервера + Development + отчёт без сборки; чужой не с сервера не показываем
            Assert.Equal("2.0.0.0 (локально)", rows.Single(r => r.Key == "Dev.ext2").Version);
            Assert.Equal(RowState.Ok, rows.Single(r => r.Key == "NoCompany.ext2").State);
        }

        [Fact]
        public void Startup_config_defaults_and_version_parsing()
        {
            var c = ModuleConfig.Parse("{\"statsUrl\":\"http://h\"}");
            Assert.True(c.StartupWindow);
            Assert.True(c.StartupAutoClose);
            Assert.Equal(15, c.StartupLockSeconds);
            Assert.Equal(30, ModuleConfig.Parse("{\"startupLockSeconds\":30}").StartupLockSeconds);
            Assert.False(ModuleConfig.Parse("{\"startupWindow\":false}").StartupWindow);

            Assert.Equal("1.2.1", ModuleSources.VersionFromAdditional("Версия 1.2.1.0 - Размер 93 КБ")); // версия модуля - X.Y.Z
            Assert.Equal("1.2.1", ModuleSources.VersionFromAdditional("Версия 1.2.1 - Размер 93 КБ"));
            Assert.Equal("1.2.1.5", ModuleSources.VersionFromAdditional("Версия 1.2.1.5 - Размер 93 КБ")); // ненулевая 4-я часть видна
            Assert.Equal("", ModuleSources.VersionFromAdditional(null));
        }
    }
}
