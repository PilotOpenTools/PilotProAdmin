using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Pilot.ClientSdk.Infrastructure;
using Pilot.ClientSdk.Telemetry;

namespace Pilot.ClientSdk
{
    /// <summary>Состояние запуска стартового класса модуля.</summary>
    public enum ModuleStartupStatus
    {
        Running,
        Success,
        Error,
    }

    /// <summary>Запуск одного стартового класса (<see cref="IModule"/> / <see cref="IModuleAsync"/>).</summary>
    public sealed class ModuleStartupReport
    {
        internal ModuleStartupReport(string module, string part)
        {
            Module = module;
            Part = part;
        }

        /// <summary>Имя сборки модуля (087OpenStoradge.ext2).</summary>
        public string Module { get; }

        /// <summary>Полное имя стартового класса.</summary>
        public string Part { get; }

        public ModuleStartupStatus Status { get; internal set; } = ModuleStartupStatus.Running;

        /// <summary>Длительность инициализации; у ещё идущей - сколько прошло.</summary>
        public long DurationMs => Status == ModuleStartupStatus.Running ? (long)Watch.Elapsed.TotalMilliseconds : _durationMs;

        public Exception Error { get; internal set; }

        internal Stopwatch Watch { get; } = Stopwatch.StartNew();

        private long _durationMs;

        internal void Finish(Exception error)
        {
            Watch.Stop();
            _durationMs = (long)Watch.Elapsed.TotalMilliseconds;
            Error = error;
            Status = error == null ? ModuleStartupStatus.Success : ModuleStartupStatus.Error;
        }
    }

    /// <summary>
    /// Запуск стартового класса модуля с замером: время, успех, ошибка. Результат виден в окне «Загрузка модулей»,
    /// пишется в лог Pilot.ClientSdk и уходит в Grafana действием «Запуск модуля».
    /// Вызывать последней строкой <c>[ImportingConstructor]</c>: <c>StartupRunner.Run(this)</c> или <c>StartupRunner.RunAsync(this)</c>.
    /// </summary>
    public static class StartupRunner
    {
        /// <summary>Название действия в статистике.</summary>
        public const string StatsAction = "Запуск модуля";

        private static readonly object Sync = new object();
        private static readonly List<ModuleStartupReport> All = new List<ModuleStartupReport>();

        /// <summary>Запуск начался или закончился (вызывается в потоке, где это произошло).</summary>
        public static event Action<ModuleStartupReport> Changed;

        /// <summary>Все запуски с начала работы клиента.</summary>
        public static IReadOnlyList<ModuleStartupReport> Reports
        {
            get { lock (Sync) return All.ToList(); }
        }

        /// <summary>Выполнить <see cref="IModule.Initialize"/> с замером. Исключение не пробрасывается: клиент должен запуститься.</summary>
        public static void Run(IModule module)
        {
            if (module == null)
                throw new ArgumentNullException(nameof(module));
            var report = Begin(module.GetType());
            try
            {
                module.Initialize();
                Complete(module.GetType().Assembly, report, null);
            }
            catch (Exception ex)
            {
                Complete(module.GetType().Assembly, report, ex);
            }
        }

        /// <summary>
        /// Запустить <see cref="IModuleAsync.InitializeAsync"/> с замером. Возвращённая задача никогда не падает,
        /// ждать её в конструкторе не нужно (и нельзя - заблокирует UI-поток).
        /// </summary>
        public static Task RunAsync(IModuleAsync module)
        {
            if (module == null)
                throw new ArgumentNullException(nameof(module));
            var assembly = module.GetType().Assembly;
            var report = Begin(module.GetType());
            Task task;
            try
            {
                task = module.InitializeAsync() ?? Task.CompletedTask;
            }
            catch (Exception ex)
            {
                Complete(assembly, report, ex);
                return Task.CompletedTask;
            }
            return task.ContinueWith(t => Complete(assembly, report,
                    t.IsFaulted ? t.Exception?.GetBaseException()
                    : t.IsCanceled ? new OperationCanceledException("Инициализация модуля отменена.")
                    : null),
                TaskScheduler.Default);
        }

        private static ModuleStartupReport Begin(Type part)
        {
            var report = new ModuleStartupReport(part.Assembly.GetName().Name, part.FullName);
            lock (Sync)
            {
                All.RemoveAll(r => r.Part == report.Part && r.Module == report.Module);   // повторный запуск того же класса
                All.Add(report);
            }
            Raise(report);
            return report;
        }

        private static void Complete(Assembly assembly, ModuleStartupReport report, Exception error)
        {
            report.Finish(error);
            try
            {
                if (error == null)
                    ModuleLog.Info($"Запуск {report.Part} ({report.Module}): {report.DurationMs} мс");
                else
                    ModuleLog.Error($"Запуск {report.Part} ({report.Module}) не удался за {report.DurationMs} мс", error);

                var status = error == null ? ModuleEventStatus.Success
                    : error is OperationCanceledException ? ModuleEventStatus.Cancelled
                    : ModuleEventStatus.Error;
                ModuleStats.For(assembly).Send(StatsAction, status, report.DurationMs, null, error);
            }
            catch
            {
                // замер не должен ломать модуль
            }
            Raise(report);
        }

        private static void Raise(ModuleStartupReport report)
        {
            try { Changed?.Invoke(report); }
            catch { /* подписчик (окно) не должен ломать модуль */ }
        }

        /// <summary>Для тестов.</summary>
        internal static void Reset()
        {
            lock (Sync) All.Clear();
        }
    }
}
